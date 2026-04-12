using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
//用whisper gglm-small模型识别语言，cpu执行，13700k上占用30%~40%的样子
namespace LanguageCheck
{
    public class LID : IDisposable
    {
        private WhisperFactory factory;
        private WhisperProcessor processor;
        private bool _disposed;
        private readonly static int SampleRate = 16000;
        public LID()
        {
            // small medium large的速度相差不大，仅识别语言的话准确性似乎也没有特别大的差距
            // https://huggingface.co/ggerganov/whisper.cpp/tree/main or WhisperGgmlDownloader
            string modelPath = ".\\model\\ggml-small.bin";
            // 加载 whisper 模型
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"fail to find speech to text model {modelPath}");
            // Whisper.net.Runtime.Cuda要装到caller所在项目里
            // 强制使用gpu
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda];
            var opt = WhisperFactoryOptions.Default;
            opt.UseGpu = true;
            opt.GpuDevice = 0; // 指定N卡
            factory = WhisperFactory.FromPath(modelPath, opt);
            processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .WithNoSpeechThreshold(0.5f) // 0.6->0.5,增加将音频视为静音的概率
                .Build();
        }
        private static WaveStream? CreateReader(string audioPath)
        {
            // 无法支持长路径，前面加\\?\也不行
            try
            {
                return new AudioFileReader(audioPath);
            }
            catch
            {
                // 如果 AudioFileReader 不支持该格式，尝试 MediaFoundationReader（Windows）
                try
                {
                    return new MediaFoundationReader(audioPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"NAudio could not open '{audioPath}'. " +
                        $"WAV/MP3 is usually supported. FLAC often requires an additional decoder. " +
                        $"Original error: {ex.Message}", ex);
                    return null;
                }
            }
        }
        private static ISampleProvider ToMono(ISampleProvider input)
        {
            int ch = input.WaveFormat.Channels;
            if (ch == 1) return input;
            if (ch == 2)
            {
                return new StereoToMonoSampleProvider(input)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            // 多声道：简化处理——取前两声道混到 mono
            var mux = new MultiplexingSampleProvider(new[] { input }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            return new StereoToMonoSampleProvider(mux)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        private static float[] ReadAllSamples(ISampleProvider sp, int len)
        {
            var buffer = new float[SampleRate * len];
            int read, totalRead = 0;
            // buffer超出实际长度的部分为0,不影响
            while ((read = sp.Read(buffer, totalRead, SampleRate)) > 0 && totalRead < SampleRate * (len - 1))
                totalRead += read;
            return buffer;
        }
        // 返回null表示无法判断。
        public bool? IsChineseImp(string path)
        {
            const int N = 8;
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            // 读取成 16k mono pcm float32流
            // DetectLanguage似乎不管对于多长的音频，都只取前面一小段进行判断，如果音频前面都没有人声就会判断错误，small/medium/large-v3模型都一样
            // 初始化时略微提高no speech threshold阈值
            // 均匀截取N段，取第一个高概率确定的语言，都未命中视作失败
            // 仍然有概率误判，例如全程都是呜呜哇哇的音频，可能以高prob被判定为zh
            using var reader = CreateReader(path);
            if (reader is null)
                return null;
            int totalLen = (int)Math.Floor(reader.TotalTime.TotalSeconds);
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.SampleRate != SampleRate)
                sp = new WdlResamplingSampleProvider(sp, SampleRate);
            sp = ToMono(sp);
            for (int i = 0; i < N; i++)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(totalLen / N * i);
                var (lang, prob) = processor.DetectLanguageWithProbability(ReadAllSamples(sp, 60));
                if (prob > 0.85f)
                {
                    Console.WriteLine($"LID {lang}-{prob:F3} : {path}");
                    return lang == "zh";
                }
            }
            Console.WriteLine($"LID Not Sure : {Path.GetFileName(path)}");
            return null;
        }
        public bool? IsChinese(string path)
        {
            var sw = Stopwatch.StartNew();
            var ret = IsChineseImp(path);
            sw.Stop();
            Console.WriteLine($"Check Language :{ret} {sw.ElapsedMilliseconds / 1000.0f :F2} sec.");
            return ret;
        }
        public async Task<string> ToText(string path)
        {
            const int N = 3;
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            // 读取成 16k mono pcm float32流
            string fullText = "";
            using var reader = CreateReader(path);
            if (reader is null)
                return "";
            int totalLen = (int)Math.Floor(reader.TotalTime.TotalSeconds);
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.SampleRate != SampleRate)
                sp = new WdlResamplingSampleProvider(sp, SampleRate);
            sp = ToMono(sp);
            for (int i = 0; i < N; i++)
            {
                reader.CurrentTime = TimeSpan.FromSeconds(totalLen / N * i);

                await foreach (var segment in processor.ProcessAsync(ReadAllSamples(sp, 60)))
                {
                    fullText += segment.Text;
                    // prob似乎并不能清楚分辨无人声
                    Console.WriteLine($"{segment.NoSpeechProbability:F4}/{segment.MinProbability:F4}-{segment.MaxProbability:F4} /: {segment.Text}");
                }
            }

            return fullText;
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                processor?.Dispose();
                factory?.Dispose();
            }
        }
        ~LID()
        {
            Dispose(false);
        }
    }
}