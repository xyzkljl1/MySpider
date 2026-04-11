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
        // DetectLanguage只使用前约30秒音频，每个采样点读取60秒足够
        private const int DetectSegmentSeconds = 60;
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

        /// <summary>
        /// 从音频文件的指定偏移处读取指定长度的16kHz单声道PCM float32数据。
        /// </summary>
        /// <param name="audioPath">音频文件路径</param>
        /// <param name="len">读取长度（秒）</param>
        /// <param name="offsetSeconds">起始偏移（秒），默认为0</param>
        public static float[] Read16kMonoPCMFloat32(string audioPath, int len, double offsetSeconds = 0)
        {
            using var reader = CreateReader(audioPath);
            if (offsetSeconds > 0 && reader.TotalTime.TotalSeconds > offsetSeconds)
                reader.CurrentTime = TimeSpan.FromSeconds(offsetSeconds);
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
            int maxSamples = 16000 * len;
            var samples = new float[maxSamples];
            var buffer = new float[16000]; // 每次读约1秒（mono 16k）
            int totalRead = 0;
            int read;
            while (totalRead < maxSamples
                    && (read = sp.Read(buffer, 0, Math.Min(buffer.Length, maxSamples - totalRead))) > 0)
            {
                Array.Copy(buffer, 0, samples, totalRead, read);
                totalRead += read;
            }
            if (totalRead < maxSamples)
                return samples.AsSpan(0, totalRead).ToArray();
            return samples;
        }
        public bool IsChinese(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            // 读取成 16k mono pcm float32流
            // DetectLanguage似乎不管对于多长的音频，都只取前面一小段进行判断，如果音频前面都没有人声就会判断错误，small/medium/large-v3模型都一样
            // 因此获取音频总时长，在0/3,1/3,2/3处分别seek并只读取短片段进行判断，一次命中就算命中
            double totalSeconds;
            using (var reader = CreateReader(path))
                totalSeconds = reader.TotalTime.TotalSeconds;

            for (int i = 0; i < 3; i++)
            {
                double offset = totalSeconds / 3 * i;
                var samples = Read16kMonoPCMFloat32(path, DetectSegmentSeconds, offset);
                if (samples.Length == 0)
                    continue;
                string? detectedLang = processor.DetectLanguage(samples);
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