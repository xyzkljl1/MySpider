using System.Text.Json;
using Telegram;

namespace MySpider
{
    internal sealed class ApplicationConfig
    {
        public string Proxy { get; set; } = "";
        public DownloadPathOptions? Paths { get; set; }
        public TelegramOptions? Telegram { get; set; }

        public static ApplicationConfig Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(path))
                throw new FileNotFoundException("Application config file was not found.", path);

            var config = JsonSerializer.Deserialize<ApplicationConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Failed to read config.json.");

            if (config.Paths is null)
                throw new InvalidOperationException("Paths section is missing from config.json.");
            if (string.IsNullOrWhiteSpace(config.Paths.TemporaryRoot))
                throw new InvalidOperationException("Paths.TemporaryRoot must be configured.");
            if (string.IsNullOrWhiteSpace(config.Paths.Chinese))
                throw new InvalidOperationException("Paths.Chinese must be configured.");
            if (string.IsNullOrWhiteSpace(config.Paths.ReliableR))
                throw new InvalidOperationException("Paths.ReliableR must be configured.");
            if (string.IsNullOrWhiteSpace(config.Paths.Reliable))
                throw new InvalidOperationException("Paths.Reliable must be configured.");
            if (string.IsNullOrWhiteSpace(config.Paths.Ffmpeg))
                throw new InvalidOperationException("Paths.Ffmpeg must be configured.");
            if (config.Telegram is null)
                throw new InvalidOperationException("Telegram section is missing from config.json.");
            if (string.IsNullOrWhiteSpace(config.Proxy))
                throw new InvalidOperationException("Proxy must be configured.");

            return config;
        }
    }

    internal sealed class DownloadPathOptions
    {
        public string TemporaryRoot { get; set; } = "";
        public string Chinese { get; set; } = "";
        public string ReliableR { get; set; } = "";
        public string Reliable { get; set; } = "";
        public string Ffmpeg { get; set; } = "";
    }
}
