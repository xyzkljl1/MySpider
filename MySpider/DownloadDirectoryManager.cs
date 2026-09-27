using MySpider.Core;

namespace MySpider
{
    internal sealed class DownloadDirectoryManager : IDownloadDirectoryManager
    {
        private readonly string temporaryRootDirectory;
        private readonly string chineseDirectory;
        private readonly string reliableRDirectory;
        private readonly string reliableDirectory;
        private readonly IReadOnlyList<string> finalDirectories;

        public DownloadDirectoryManager(DownloadPathOptions paths)
        {
            temporaryRootDirectory = paths.TemporaryRoot;
            chineseDirectory = paths.Chinese;
            reliableRDirectory = paths.ReliableR;
            reliableDirectory = paths.Reliable;
            finalDirectories = Array.AsReadOnly(new[] { chineseDirectory, reliableRDirectory, reliableDirectory });
        }

        public IReadOnlyList<string> FinalDirectories => finalDirectories;

        public string GetTemporaryDirectory(string moduleName)
        {
            var directoryName = moduleName switch
            {
                "ASMR.ONE" => "ASMRONE",
                "Telegram" => "Telegram",
                _ => throw new ArgumentOutOfRangeException(nameof(moduleName), moduleName, null)
            };
            return Path.Combine(temporaryRootDirectory, directoryName);
        }

        public void FinalizeDownload(CompletedDownload download)
        {
            var parentDirectory = download.DirectoryKind switch
            {
                DownloadDirectoryKind.Chinese => chineseDirectory,
                DownloadDirectoryKind.ReliableR => reliableRDirectory,
                DownloadDirectoryKind.Reliable => reliableDirectory,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(download.DirectoryKind), download.DirectoryKind, null)
            };
            var existingDirectories = Directory.EnumerateDirectories(parentDirectory)
                .Where(path =>
                {
                    var directoryName = Path.GetFileName(path);
                    return directoryName.Equals(download.WorkId, StringComparison.OrdinalIgnoreCase) ||
                           directoryName.StartsWith(download.WorkId + " ", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            if (existingDirectories.Length > 1)
                throw new InvalidOperationException(
                    $"Multiple destination directories found for {download.WorkId}: {string.Join(", ", existingDirectories)}");

            var destinationDirectory = existingDirectories.Length == 1
                ? existingDirectories[0]
                : Path.Combine(parentDirectory, download.DirectoryName);
            var stagingDirectory = Path.Combine(parentDirectory, "Tmp");

            Thread.Sleep(5000); //略微等待，防止文件正在写入
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, true);
            Directory.CreateDirectory(stagingDirectory);

            foreach (var file in download.Files)
            {
                var relativeDirectory = file.RelativeDirectory.TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var directory = Path.Combine(stagingDirectory, relativeDirectory);
                Directory.CreateDirectory(directory);
                File.Copy(file.SourcePath, Path.Combine(directory, file.FileName), true);
            }

            if (Directory.Exists(destinationDirectory))
                Directory.Delete(destinationDirectory, true);
            Thread.Sleep(5000); //略微等待，防止文件正在写入
            Directory.Move(stagingDirectory, destinationDirectory);
            if (download.DeleteTemporaryDirectory)
                Directory.Delete(download.TemporaryDirectory, true);
        }
    }
}
