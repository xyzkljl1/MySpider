using LanguageCheck;

namespace MySpider.Core
{
    public enum DownloadCheckResult
    {
        Downloading,
        Completed,
        Retry,
        SourceUnavailable
    }

    public abstract class BaseWork
    {
        public abstract string Id { get; }
        public abstract IDownloadModule Module { get; }
        public virtual bool IgnoreNoDownload => false;
    }

    public interface IDownloadModule
    {
        string Name { get; }
        TimeSpan UpdateInterval { get; }

        Task<bool> InitializeAsync();
        Task UpdateAsync();
        IEnumerable<BaseWork> GetDownloadCandidates();
        Task<bool> StartDownloadAsync(string workId);
        Task<DownloadCheckResult> CheckDownloadAsync(string workId, LID LID);
    }

    public interface IExcludedWorkConsumer
    {
        void SetExcludedWorkIds(IReadOnlySet<string> ids);
    }

}
