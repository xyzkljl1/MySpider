using LanguageCheck;
using MySpider.Core;

namespace Telegram
{
    internal sealed class TelegramWork : BaseWork
    {
        private readonly TelegramModule module;

        public TelegramWork(TelegramModule module, string id)
        {
            this.module = module;
            Id = id;
        }

        public override string Id { get; }
        public override IDownloadModule Module => module;
    }

    public sealed class TelegramModule : IDownloadModule
    {
        private readonly Dictionary<string, TelegramWork> works = new(StringComparer.OrdinalIgnoreCase);

        public string Name => "Telegram";
        public TimeSpan UpdateInterval => TimeSpan.FromDays(1);

        public Task<bool> InitializeAsync()
        {
            return Task.FromResult(true);
        }

        public Task UpdateAsync()
        {
            return Task.CompletedTask;
        }

        public IEnumerable<BaseWork> GetDownloadCandidates()
        {
            return works.Values.Cast<BaseWork>().ToList();
        }

        public Task<bool> StartDownloadAsync(string workId)
        {
            return Task.FromResult(false);
        }

        public Task<DownloadCheckResult> CheckDownloadAsync(string workId, LID LID)
        {
            return Task.FromResult(DownloadCheckResult.SourceUnavailable);
        }
    }
}
