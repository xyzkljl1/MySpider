using System.Text.RegularExpressions;
using LanguageCheck;
using MySpider.Core;
using TdLib;
using static TdLib.TdApi.ChatType;
using static TdLib.TdApi.MessageContent;
using static TdLib.TdApi.MessageSender;

namespace Telegram
{
    public sealed record TelegramMessageInfo(long Id, DateTimeOffset Date, string ContentType, string Text);

    internal enum TelegramWorkKind
    {
        ChineseVoice,
        TranslatedVoice
    }

    internal sealed class TelegramDownloadFile
    {
        public TelegramDownloadFile(int fileId, string fileName)
        {
            FileId = fileId;
            FileName = fileName;
        }

        public int FileId { get; }
        public string FileName { get; }
    }

    internal sealed class TelegramWork : BaseWork
    {
        private readonly TelegramModule module;

        public TelegramWork(
            TelegramModule module,
            string id,
            long messageId,
            string directoryName,
            TelegramWorkKind kind)
        {
            this.module = module;
            Id = id;
            MessageId = messageId;
            DirectoryName = directoryName;
            Kind = kind;
        }

        public override string Id { get; }
        public override IDownloadModule Module => module;
        public long MessageId { get; }
        public string DirectoryName { get; }
        public TelegramWorkKind Kind { get; }
        public List<TelegramDownloadFile> Files { get; } = new();
        public bool FilesLoaded { get; set; }
        public bool Active { get; set; }
        public int FailedChecks { get; set; }
        public DateTime NextStartAttemptUtc { get; set; }
    }

    public sealed class TelegramModule : IDownloadModule, IExcludedWorkConsumer
    {
        private const string ChannelUsername = "ASMRultra";
        private const string ChineseVoiceTag = "#中文音声";
        private const string TranslatedVoiceTag = "#汉化音声";
        private const int MaximumPendingWorks = 20;
        private const int MaximumScannedMessages = 300;
        private const int HistoryPageSize = 100;
        private const int DownloadPriority = 16;
        private const int MaximumFailedChecks = 144;
        private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(300);
        private static readonly Regex RjRegex = new(
            @"(?<![A-Z0-9])RJ\d{6,10}(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly string RuntimeDirectory =
            Path.GetDirectoryName(typeof(TelegramModule).Assembly.Location)!;
        private static readonly object TdLogConfigLock = new();
        private static bool tdLogConfigured;

        private readonly TelegramOptions options;
        private readonly string proxy;
        private readonly IDownloadDirectoryManager downloadDirectoryManager;
        private readonly Dictionary<string, TelegramWork> works = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> locallyExcludedWorkIds = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> excludedWorkIds = new(StringComparer.OrdinalIgnoreCase);
        private TdClient? client;
        private TdApi.Chat? channel;
        private string dataDirectory = "";
        private string mediaDirectory = "";
        private string cursorPath = "";
        private long? cursor;
        private bool hasExcludedWorkIds;

        public string Name => "Telegram";
        public TimeSpan UpdateInterval => TimeSpan.FromMinutes(30);

        public TelegramModule(
            TelegramOptions options,
            string proxy,
            IDownloadDirectoryManager downloadDirectoryManager)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.proxy = proxy;
            this.downloadDirectoryManager = downloadDirectoryManager
                ?? throw new ArgumentNullException(nameof(downloadDirectoryManager));
        }

        public async Task<bool> InitializeAsync()
        {
            try
            {
                ValidateOptions(options);
                ConfigureTdLibLogging();

                dataDirectory = Path.Combine(RuntimeDirectory, ".TelegramData");
                cursorPath = Path.Combine(dataDirectory, "cursor.txt");
                var databaseDirectory = Path.Combine(dataDirectory, "database");
                mediaDirectory = Path.Combine(downloadDirectoryManager.GetTemporaryDirectory(Name), "TdFiles");
                Directory.CreateDirectory(databaseDirectory);
                Directory.CreateDirectory(mediaDirectory);

                if (File.Exists(cursorPath))
                {
                    var cursorText = File.ReadAllText(cursorPath).Trim();
                    if (!long.TryParse(cursorText, out var savedCursor) || savedCursor < 0)
                        throw new InvalidOperationException("Telegram cursor.txt is invalid.");
                    cursor = savedCursor;
                    Console.WriteLine($"[Telegram] Loaded cursor: {savedCursor}");
                }

                client = new TdClient();
                await client.SetTdlibParametersAsync(
                    databaseDirectory: databaseDirectory,
                    filesDirectory: mediaDirectory,
                    useFileDatabase: true,
                    useChatInfoDatabase: true,
                    useMessageDatabase: false,
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

        public void SetExcludedWorkIds(IReadOnlySet<string> ids)
        {
            excludedWorkIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            hasExcludedWorkIds = true;

            foreach (var pair in works.ToList())
                if (!pair.Value.Active && IsExcluded(pair.Key))
                    works.Remove(pair.Key);
        }

        public async Task UpdateAsync()
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");
            if (!hasExcludedWorkIds)
            {
                Console.WriteLine("[Telegram] No excluded-work snapshot; skip scanning.");
                return;
            }

            if (cursor is null)
            {
                cursor = await FindInitialCursorAsync();
                SaveCursor(cursor.Value);
                Console.WriteLine($"[Telegram] Initial cursor: {cursor.Value}");
            }

            var messages = await GetMessagesAfterCursorAsync(cursor.Value, MaximumScannedMessages);
            if (messages.Count == 0)
            {
                Console.WriteLine($"[Telegram] No message after cursor {cursor.Value}.");
                return;
            }

            var oldCursor = cursor.Value;
            var newCursor = oldCursor;
            var pendingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
            foreach (var message in messages)
            {
                scanned++;
                if (!TryCreateWork(message, out var discoveredWork))
                {
                    if (pendingIds.Count == 0)
                        newCursor = message.Id;
                    continue;
                }

                works.TryGetValue(discoveredWork.Id, out var existingWork);
                if (existingWork is not null && existingWork.MessageId != discoveredWork.MessageId)
                    throw new InvalidOperationException(
                        $"Duplicate Telegram RJ {discoveredWork.Id}: messages {existingWork.MessageId} and {discoveredWork.MessageId}.");

                if (existingWork?.Active == true)
                {
                    pendingIds.Add(discoveredWork.Id);
                    if (pendingIds.Count >= MaximumPendingWorks)
                        break;
                    continue;
                }

                if (IsExcluded(discoveredWork.Id))
                {
                    if (pendingIds.Count == 0)
                        newCursor = message.Id;
                    continue;
                }

                if (existingWork is null)
                {
                    works.Add(discoveredWork.Id, discoveredWork);
                }

                pendingIds.Add(discoveredWork.Id);
                if (pendingIds.Count >= MaximumPendingWorks)
                    break;
            }

            if (newCursor > oldCursor)
            {
                cursor = newCursor;
                SaveCursor(newCursor);
            }
            foreach (var pair in locallyExcludedWorkIds.Where(pair => pair.Value <= cursor.Value).ToList())
                locallyExcludedWorkIds.Remove(pair.Key);

            Console.WriteLine(
                $"[Telegram] Scan done: messages={scanned}, pending={pendingIds.Count}, " +
                $"cursor={oldCursor}->{cursor.Value}.");
        }

        public IEnumerable<BaseWork> GetDownloadCandidates()
        {
            return works.Values
                .Where(work => !work.Active && !IsExcluded(work.Id) && work.NextStartAttemptUtc <= DateTime.UtcNow)
                .OrderBy(work => work.MessageId)
                .Cast<BaseWork>()
                .ToList();
        }

        public async Task<bool> StartDownloadAsync(string workId)
        {
            if (client is null || channel is null)
                return false;
            if (!works.TryGetValue(workId, out var work) || IsExcluded(workId))
                return false;

            try
            {
                if (!work.FilesLoaded)
                {
                    var files = await GetWorkFilesAsync(work);
                    if (files.Count == 0)
                    {
                        Console.WriteLine($"[Telegram] No target documents: {work.Id} message {work.MessageId}");
                        work.NextStartAttemptUtc = DateTime.UtcNow.AddHours(6);
                        return false;
                    }

                    work.Files.AddRange(files);
                    work.FilesLoaded = true;
                }

                work.Active = true;
                work.FailedChecks = 0;
                foreach (var file in work.Files)
                {
                    var state = await client.GetFileAsync(file.FileId);
                    if (state.Local.IsDownloadingCompleted && File.Exists(state.Local.Path))
                        continue;
                    await client.DownloadFileAsync(
                        fileId: file.FileId,
                        priority: DownloadPriority,
                        offset: 0,
                        limit: 0,
                        synchronous: false);
                }

                Console.WriteLine($"[Telegram] Download started: {work.Id}, files={work.Files.Count}");
                return true;
            }
            catch (Exception ex)
            {
                work.Active = false;
                work.NextStartAttemptUtc = DateTime.UtcNow.AddMinutes(30);
                Console.WriteLine($"[Telegram] Start download failed {work.Id}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return false;
            }
        }

        public async Task<DownloadCheckResult> CheckDownloadAsync(string workId, LID LID)
        {
            if (client is null || !works.TryGetValue(workId, out var work))
                return DownloadCheckResult.SourceUnavailable;

            try
            {
                var downloadedFiles = new List<(TelegramDownloadFile Source, TdApi.File File)>(work.Files.Count);
                foreach (var file in work.Files)
                {
                    var state = await client.GetFileAsync(file.FileId);
                    if (!state.Local.IsDownloadingCompleted)
                    {
                        work.FailedChecks++;
                        if (work.FailedChecks > MaximumFailedChecks)
                        {
                            work.Active = false;
                            work.FailedChecks = 0;
                            Console.WriteLine($"[Telegram] Download timed out and will be retried: {work.Id}");
                            return DownloadCheckResult.Retry;
                        }
                        return DownloadCheckResult.Downloading;
                    }

                    if (string.IsNullOrWhiteSpace(state.Local.Path) || !File.Exists(state.Local.Path))
                        throw new FileNotFoundException("TDLib reported a completed file without a local path.", state.Local.Path);
                    if (new FileInfo(state.Local.Path).Length == 0)
                        throw new InvalidOperationException($"TDLib downloaded an empty file: {file.FileName}");
                    downloadedFiles.Add((file, state));
                }

                var directoryKind = work.Kind == TelegramWorkKind.ChineseVoice
                    ? DownloadDirectoryKind.Chinese
                    : DownloadDirectoryKind.ReliableR;
                downloadDirectoryManager.FinalizeDownload(new CompletedDownload(
                    work.Id,
                    work.DirectoryName,
                    mediaDirectory,
                    directoryKind,
                    downloadedFiles.Select(item => new DownloadedFile(
                        item.File.Local.Path,
                        "",
                        item.Source.FileName)).ToList(),
                    DeleteTemporaryDirectory: false));

                foreach (var file in work.Files)
                    try
                    {
                        await client.DeleteFileAsync(file.FileId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Telegram] Failed to clear TDLib file {file.FileId}: {ex.Message}");
                    }

                work.Active = false;
                locallyExcludedWorkIds[work.Id] = work.MessageId;
                Console.WriteLine($"[Telegram] Download done: {work.Id}");
                return DownloadCheckResult.Completed;
            }
            catch (Exception ex)
            {
                work.Active = false;
                Console.WriteLine($"[Telegram] Finalize/check failed {work.Id}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return DownloadCheckResult.Retry;
            }
        }

        public async Task<IReadOnlyList<TelegramMessageInfo>> FetchLatestMessagesAsync(int limit = 5)
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");

            var boundedLimit = Math.Clamp(limit, 1, 100);
            var history = await client.GetChatHistoryAsync(
                chatId: channel.Id,
                fromMessageId: 0,
                offset: 0,
                limit: boundedLimit,
                onlyLocal: false);
            return history.Messages_
                .Select(message => new TelegramMessageInfo(
                    message.Id,
                    DateTimeOffset.FromUnixTimeSeconds(message.Date),
                    message.Content.DataType,
                    GetMessageText(message)))
                .ToList();
        }

        private bool IsExcluded(string workId)
        {
            return excludedWorkIds.Contains(workId) || locallyExcludedWorkIds.ContainsKey(workId);
        }

        private async Task<long> FindInitialCursorAsync()
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");

            Console.WriteLine("[Telegram] Locating the oldest channel message; media will not be downloaded.");
            long fromMessageId = 0;
            long oldestMessageId = 0;
            var pages = 0;
            while (true)
            {
                var history = await client.GetChatHistoryAsync(
                    chatId: channel.Id,
                    fromMessageId: fromMessageId,
                    offset: 0,
                    limit: HistoryPageSize,
                    onlyLocal: false);
                if (history.Messages_.Length == 0)
                    break;

                var oldestInPage = history.Messages_.Min(message => message.Id);
                if (oldestInPage == fromMessageId)
                    break;
                oldestMessageId = oldestInPage;
                fromMessageId = oldestInPage;
                pages++;
                if (pages % 100 == 0)
                    Console.WriteLine($"[Telegram] Initial history scan: {pages * HistoryPageSize} messages.");
                await Task.Delay(RequestInterval);
            }

            return oldestMessageId > 0 ? oldestMessageId - 1 : 0;
        }

        private async Task<List<TdApi.Message>> GetMessagesAfterCursorAsync(long afterMessageId, int limit)
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");

            var messages = new Dictionary<long, TdApi.Message>();
            var position = afterMessageId;
            while (messages.Count < limit)
            {
                TdApi.Messages history;
                if (position == 0)
                {
                    history = await client.GetChatHistoryAsync(
                        chatId: channel.Id,
                        fromMessageId: 0,
                        offset: 0,
                        limit: HistoryPageSize,
                        onlyLocal: false);
                }
                else
                {
                    history = await client.GetChatHistoryAsync(
                        chatId: channel.Id,
                        fromMessageId: position,
                        offset: -(HistoryPageSize - 1),
                        limit: HistoryPageSize,
                        onlyLocal: false);
                }

                var newerMessages = history.Messages_
                    .Where(message => message.Id > position)
                    .OrderBy(message => message.Id)
                    .ToList();
                if (newerMessages.Count == 0)
                    break;

                foreach (var message in newerMessages)
                {
                    messages.TryAdd(message.Id, message);
                    if (messages.Count == limit)
                        break;
                }

                var nextPosition = newerMessages[^1].Id;
                if (nextPosition <= position)
                    break;
                position = nextPosition;
                if (messages.Count < limit)
                    await Task.Delay(RequestInterval);
            }

            return messages.Values.OrderBy(message => message.Id).ToList();
        }

        private bool TryCreateWork(TdApi.Message message, out TelegramWork work)
        {
            work = null!;
            var text = GetMessageText(message).TrimStart();
            TelegramWorkKind kind;
            string tag;
            if (text.StartsWith(ChineseVoiceTag, StringComparison.Ordinal))
            {
                kind = TelegramWorkKind.ChineseVoice;
                tag = ChineseVoiceTag;
            }
            else if (text.StartsWith(TranslatedVoiceTag, StringComparison.Ordinal))
            {
                kind = TelegramWorkKind.TranslatedVoice;
                tag = TranslatedVoiceTag;
            }
            else
            {
                return false;
            }

            var rjs = RjRegex.Matches(text)
                .Select(match => match.Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (rjs.Count != 1)
            {
                Console.WriteLine($"[Telegram] Skip tagged message {message.Id}: expected one RJ, found {rjs.Count}.");
                return false;
            }

            var rj = rjs[0];
            var description = Regex.Replace(text[tag.Length..], @"\s+", " ").Trim();
            var directoryName = description.StartsWith(rj, StringComparison.OrdinalIgnoreCase)
                ? description
                : $"{rj} {description}".Trim();
            directoryName = SanitizeFileName(directoryName);
            if (directoryName.Length > 100)
                directoryName = directoryName[..100];
            if (directoryName == "")
                directoryName = rj;

            work = new TelegramWork(this, rj, message.Id, directoryName, kind);
            return true;
        }

        private async Task<List<TelegramDownloadFile>> GetWorkFilesAsync(TelegramWork work)
        {
            if (client is null || channel is null)
                throw new InvalidOperationException("Telegram channel is not initialized.");

            var thread = await client.GetMessageThreadAsync(channel.Id, work.MessageId);
            var messages = thread.Messages.ToDictionary(message => message.Id);
            long fromMessageId = 0;
            while (true)
            {
                var history = await client.GetMessageThreadHistoryAsync(
                    chatId: channel.Id,
                    messageId: work.MessageId,
                    fromMessageId: fromMessageId,
                    offset: 0,
                    limit: HistoryPageSize);
                if (history.Messages_.Length == 0)
                    break;

                foreach (var message in history.Messages_)
                    messages.TryAdd(message.Id, message);
                var nextFromMessageId = history.Messages_.Min(message => message.Id);
                if (nextFromMessageId == fromMessageId)
                    break;
                fromMessageId = nextFromMessageId;
                await Task.Delay(RequestInterval);
            }

            var files = new List<TelegramDownloadFile>();
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages.Values.OrderBy(message => message.Id))
            {
                if (message.SenderId is not MessageSenderChat sender || sender.ChatId != thread.ChatId)
                    continue;
                if (message.Content is not MessageDocument content)
                    continue;

                var fileName = SanitizeFileName(Path.GetFileName(content.Document.FileName));
                if (fileName == "")
                    throw new InvalidOperationException($"Telegram document {message.Id} has no valid file name.");
                if (!fileNames.Add(fileName))
                    throw new InvalidOperationException($"Duplicate Telegram file name {fileName} in {work.Id}.");
                files.Add(new TelegramDownloadFile(content.Document.Document_.Id, fileName));
            }

            return files;
        }

        private void SaveCursor(long value)
        {
            File.WriteAllText(cursorPath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                        Console.WriteLine("[Telegram] Two-step verification is enabled; password authentication is not supported.");
                        return false;
                    case TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation:
                        Console.WriteLine("[Telegram] Other-device confirmation is not supported; use verification-code login.");
                        return false;
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

        private static string SanitizeFileName(string value)
        {
            var result = Regex.Replace(value, "[/\\\\?*<>:\"\t|]", "_");
            return result.TrimEnd(' ', '.');
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
    }

    public sealed class TelegramOptions
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
    }
}
