using System.Text;
using LanguageCheck;
using MySpider.Core;
using TdLib;
using static TdLib.TdApi.ChatType;
using static TdLib.TdApi.MessageContent;

namespace Telegram
{
    public sealed record TelegramMessageInfo(long Id, DateTimeOffset Date, string ContentType, string Text);

    public sealed class TelegramModule : IDownloadModule
    {
        private const string ChannelUsername = "ASMRultra";
        private static readonly string RuntimeDirectory =
            Path.GetDirectoryName(typeof(TelegramModule).Assembly.Location)!;
        private static readonly object TdLogConfigLock = new();
        private static bool tdLogConfigured;

        private TdClient? client;
        private readonly TelegramOptions options;
        private readonly string proxy;
        private TdApi.Chat? channel;

        public string Name => "Telegram";
        public TimeSpan UpdateInterval => TimeSpan.FromDays(1);

        public TelegramModule(TelegramOptions options, string proxy)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.proxy = proxy;
        }

        public async Task<bool> InitializeAsync()
        {
            try
            {
                ValidateOptions(options);
                ConfigureTdLibLogging();

                var dataDirectory = Path.Combine(RuntimeDirectory, "TelegramData");
                var databaseDirectory = Path.Combine(dataDirectory, "database");
                var filesDirectory = Path.Combine(dataDirectory, "files");
                Directory.CreateDirectory(databaseDirectory);
                Directory.CreateDirectory(filesDirectory);

                client = new TdClient();
                await client.SetTdlibParametersAsync(
                    databaseDirectory: databaseDirectory,
                    filesDirectory: filesDirectory,
                    useFileDatabase: true,
                    useChatInfoDatabase: true,
                    useMessageDatabase: true,
                    useSecretChats: false,
                    apiId: options.ApiId,
                    apiHash: options.ApiHash,
                    systemLanguageCode: "zh-hans",
                    deviceModel: "Desktop",
                    systemVersion: Environment.OSVersion.VersionString,
                    applicationVersion: "1.0");

                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    var parsedProxy = ParseProxy(proxy);
                    await client.AddProxyAsync(new TdApi.Proxy
                    {
                        Server = parsedProxy.Host,
                        Port = parsedProxy.Port,
                        Type = new TdApi.ProxyType.ProxyTypeHttp()
                    }, true);
                }

                if (!await LoginAsync())
                    return false;

                channel = await FindChannelAsync();
                Console.WriteLine($"[Telegram] Channel found: @{ChannelUsername} {channel.Title} ({channel.Id})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Telegram] Initialize failed: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                client?.Dispose();
                client = null;
                return false;
            }
        }

        public Task UpdateAsync()
        {
            //作品列表获取尚未实现。
            return Task.CompletedTask;
        }

        public IEnumerable<BaseWork> GetDownloadCandidates()
        {
            return Array.Empty<BaseWork>();
        }

        public Task<bool> StartDownloadAsync(string workId)
        {
            return Task.FromResult(false);
        }

        public Task<DownloadCheckResult> CheckDownloadAsync(string workId, LID LID)
        {
            return Task.FromResult(DownloadCheckResult.SourceUnavailable);
        }

        public async Task<IReadOnlyList<TelegramMessageInfo>> FetchLatestMessagesAsync(int limit = 5)
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");

            var boundedLimit = Math.Clamp(limit, 1, 100);
            var messages = new List<TdApi.Message>(boundedLimit);
            var messageIds = new HashSet<long>();
            long fromMessageId = 0;
            var requestCount = 0;
            while (messages.Count < boundedLimit && requestCount < boundedLimit)
            {
                requestCount++;
                var history = await client.GetChatHistoryAsync(
                    chatId: channel.Id,
                    fromMessageId: fromMessageId,
                    offset: 0,
                    limit: boundedLimit - messages.Count,
                    onlyLocal: false);
                if (history.Messages_.Length == 0)
                    break;

                foreach (var message in history.Messages_)
                    if (messageIds.Add(message.Id))
                    {
                        messages.Add(message);
                        if (messages.Count == boundedLimit)
                            break;
                    }

                var nextFromMessageId = history.Messages_[^1].Id;
                if (nextFromMessageId == fromMessageId)
                    break;
                fromMessageId = nextFromMessageId;
            }

            return messages
                .Select(message => new TelegramMessageInfo(
                    message.Id,
                    DateTimeOffset.FromUnixTimeSeconds(message.Date),
                    message.Content.DataType,
                    GetMessageText(message)))
                .ToList();
        }

        private async Task<bool> LoginAsync()
        {
            if (client is null)
                return false;

            while (true)
            {
                var state = await client.GetAuthorizationStateAsync();
                switch (state)
                {
                    case TdApi.AuthorizationState.AuthorizationStateReady:
                        Console.WriteLine("[Telegram] Login succeeded.");
                        return true;
                    case TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber:
                        await client.SetAuthenticationPhoneNumberAsync(options.PhoneNumber);
                        break;
                    case TdApi.AuthorizationState.AuthorizationStateWaitCode:
                        await client.CheckAuthenticationCodeAsync(ReadRequiredLine("Telegram verification code: "));
                        break;
                    case TdApi.AuthorizationState.AuthorizationStateWaitPassword:
                        await client.CheckAuthenticationPasswordAsync(ReadRequiredSecret("Telegram two-step verification password: "));
                        break;
                    case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation confirmation:
                        Console.WriteLine("[Telegram] Confirm this login on another device: " + confirmation.Link);
                        Console.Write("Press Enter after confirmation...");
                        Console.ReadLine();
                        break;
                    case TdApi.AuthorizationState.AuthorizationStateClosing:
                    case TdApi.AuthorizationState.AuthorizationStateClosed:
                    case TdApi.AuthorizationState.AuthorizationStateLoggingOut:
                        Console.WriteLine("[Telegram] Login stopped: " + state.DataType);
                        return false;
                    default:
                        throw new InvalidOperationException("Unsupported Telegram authorization state: " + state.DataType);
                }
            }
        }

        private async Task<TdApi.Chat> FindChannelAsync()
        {
            if (client is null)
                throw new InvalidOperationException("Telegram client is not initialized.");

            var found = await client.SearchPublicChatAsync(ChannelUsername);
            if (found.Type is not ChatTypeSupergroup supergroupType)
                throw new InvalidOperationException($"@{ChannelUsername} is not a channel.");

            var supergroup = await client.GetSupergroupAsync(supergroupType.SupergroupId);
            if (!supergroup.IsChannel)
                throw new InvalidOperationException($"@{ChannelUsername} is a supergroup, not a channel.");

            var usernames = supergroup.Usernames?.ActiveUsernames ?? Array.Empty<string>();
            if (!usernames.Contains(ChannelUsername, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Found channel username does not match @{ChannelUsername}.");

            return found;
        }

        private static void ValidateOptions(TelegramOptions value)
        {
            if (value.ApiId <= 0)
                throw new InvalidOperationException("Telegram.ApiId must be configured.");
            if (string.IsNullOrWhiteSpace(value.ApiHash))
                throw new InvalidOperationException("Telegram.ApiHash must be configured.");
            if (string.IsNullOrWhiteSpace(value.PhoneNumber))
                throw new InvalidOperationException("Telegram.PhoneNumber must be configured.");
        }

        private static (string Host, int Port) ParseProxy(string value)
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1)
                throw new InvalidOperationException("Proxy must use host:port format.");

            var host = value[..separator].Trim();
            if (!int.TryParse(value[(separator + 1)..], out var port) || port is <= 0 or > 65535)
                throw new InvalidOperationException("Proxy port must be between 1 and 65535.");
            return (host, port);
        }

        private static void ConfigureTdLibLogging()
        {
            lock (TdLogConfigLock)
            {
                if (tdLogConfigured)
                    return;

                TdJsonClient.GlobalExecute("{\"@type\":\"setLogStream\",\"log_stream\":{\"@type\":\"logStreamEmpty\"}}");
                TdJsonClient.GlobalExecute("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
                tdLogConfigured = true;
            }
        }

        private static string GetMessageText(TdApi.Message message)
        {
            return message.Content switch
            {
                MessageText content => content.Text.Text,
                MessagePhoto content => content.Caption.Text,
                MessageVideo content => content.Caption.Text,
                MessageDocument content => content.Caption.Text,
                MessageAudio content => content.Caption.Text,
                MessageAnimation content => content.Caption.Text,
                MessageVoiceNote content => content.Caption.Text,
                _ => ""
            };
        }

        private static string ReadRequiredLine(string prompt)
        {
            Console.Write(prompt);
            var value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Required Telegram input was empty.");
            return value.Trim();
        }

        private static string ReadRequiredSecret(string prompt)
        {
            if (Console.IsInputRedirected)
                return ReadRequiredLine(prompt);

            Console.Write(prompt);
            var value = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0)
                        value.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                    value.Append(key.KeyChar);
            }
            Console.WriteLine();

            if (value.Length == 0)
                throw new InvalidOperationException("Required Telegram input was empty.");
            return value.ToString();
        }
    }

    public sealed class TelegramOptions
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
    }
}
