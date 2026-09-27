using asmr.one;
using MySpider.Core;
using Telegram;

namespace MySpider
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Global\MySpider.DownloadScheduler";

        private static void Main()
        {
            using var singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = singleInstanceMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    Console.WriteLine("Another MySpider instance is already running. Exiting...");
                    return;
                }

                using (BlockSyncContext.Enter())
                {
                    var config = ApplicationConfig.Load();
                    var paths = config.Paths!;
                    var telegram = config.Telegram!;
                    var downloadDirectories = new DownloadDirectoryManager(paths);
                    //模块顺序即下载优先级；同一 RJ 同时存在时优先使用排在前面的 ASMR.ONE。
                    var scheduler = new DownloadScheduler(new IDownloadModule[]
                    {
                        new Fetcher(downloadDirectories, config.Proxy, paths.Ffmpeg),
                        new TelegramModule(telegram, config.Proxy, downloadDirectories)
                    });
                    scheduler.RunAsync().Wait();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fatal exception:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                if (ownsMutex)
                    singleInstanceMutex.ReleaseMutex();
            }
        }
    }
}
