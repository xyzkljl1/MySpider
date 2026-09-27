namespace MySpider.Core
{
    public enum DownloadDirectoryKind
    {
        Chinese,
        ReliableR,
        Reliable
    }

    public sealed record DownloadedFile(
        string SourcePath,
        string RelativeDirectory,
        string FileName);

    public sealed record CompletedDownload(
        string WorkId,
        string DirectoryName,
        string TemporaryDirectory,
        DownloadDirectoryKind DirectoryKind,
        IReadOnlyList<DownloadedFile> Files);

    public interface IDownloadDirectoryManager
    {
        IReadOnlyList<string> FinalDirectories { get; }
        string GetTemporaryDirectory(string moduleName);
        void FinalizeDownload(CompletedDownload download);
    }
}
