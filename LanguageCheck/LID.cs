using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.IO.Pipes;
using Whisper.net;
using Whisper.net.Ggml;
//用whisper gglm-small模型识别语言，cpu执行，13700k上占用30%~40%的样子
namespace LanguageCheck
{
    public class LID : IDisposable
    {
        private WhisperFactory factory;
        private WhisperProcessor processor;
        private bool _disposed;
        public LID()
        {
            string modelPath = ".\\model\\ggml-small.bin";
            // 加载 whisper 模型
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"fail to find speech to text model {modelPath}");
            factory = WhisperFactory.FromPath(modelPath);
            processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .Build();
        }
        private static WaveStream CreateReader(string audioPath)
        {
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
                    throw new NotSupportedException(
                        $"NAudio could not open '{audioPath}'. " +
                        $"WAV/MP3 is usually supported. FLAC often requires an additional decoder. " +
                        $"Original error: {ex.Message}", ex);
                }
            }
        }

        public static float[] Read16kMonoPCMFloat32(string audioPath, int len)
        {
            using var reader = CreateReader(audioPath);
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.SampleRate != 16000)
                sp = new WdlResamplingSampleProvider(sp, 16000);
            sp = ToMono(sp);
            return ReadAllSamples(sp, len);
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
            var samples = new List<float>(capacity: 16000 * (len+1));
            var buffer = new float[16000]; // 每次读约1秒（mono 16k）
            int read;
            while ((read = sp.Read(buffer, 0, buffer.Length)) > 0
                    && samples.Count < 16000 * len)
                for (int i = 0; i < read; i++)
                    samples.Add(buffer[i]);
            return samples.ToArray();
        }
        public bool IsChinese(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            // 读取成 16k mono pcm float32流
            // DetectLanguage似乎不管对于多长的音频，都只取前面一小段进行判断，如果音频前面都没有人声就会判断错误，small/medium/large-v3模型都一样
            // 因此取前1200秒,在0/3,1/3,2/3处分别进行三次判断，一次命中就算命中
            var samples = Read16kMonoPCMFloat32(path, 1200);
            int size = samples.Length;
            for (int i = 0; i < 3;i++)
            {
                string? detectedLang = processor.DetectLanguage(samples.Skip(size/3*i).ToArray<float>());
                if (detectedLang == "zh")
                    return true;
            }
            return false;
        }
        public async Task<string> ToText(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);

            // 读取成 16k mono pcm float32流
            var samples = Read16kMonoPCMFloat32(path, 60);
            string fullText = "";
            await foreach (var segment in processor.ProcessAsync(samples))
                fullText += segment.Text;
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