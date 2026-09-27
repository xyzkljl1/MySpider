using LanguageCheck;
using MySpider.Core;

namespace MySpider
{
    internal sealed class DownloadScheduler
    {
        private static readonly TimeSpan DownloadInterval = TimeSpan.FromMinutes(30);
        private const string QueryAddress = "http://127.0.0.1:4567/?QueryInvalidDLSite";
        private const int NewWorkLimit = 25;
        private const int MaxDownloadingWorks = 150;
        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(35) };

        private readonly List<IDownloadModule> modules;
        private readonly Dictionary<IDownloadModule, DateTime> lastUpdates = new();
        private readonly HashSet<string> noDownloadIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> completedIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BaseWork> downloading = new(StringComparer.OrdinalIgnoreCase);

        public DownloadScheduler(IEnumerable<IDownloadModule> modules)
        {
            this.modules = modules.ToList();
        }

        public async Task RunAsync()
        {
            var initializedModules = new List<IDownloadModule>();
            foreach (var module in modules)
                try
                {
                    if (await module.InitializeAsync())
                    {
                        initializedModules.Add(module);
                        lastUpdates[module] = DateTime.MinValue;
                    }
                    else
                    {
                        Console.WriteLine($"[{module.Name}] Initialize failed, module disabled.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{module.Name}] Initialize exception, module disabled: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }

            if (initializedModules.Count == 0)
            {
                Console.WriteLine("No download module initialized. Exiting...");
                return;
            }

            while (true)
            {
                try
                {
                    await RunCycleAsync(initializedModules);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Scheduler cycle exception:" + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }

                await Task.Delay(DownloadInterval);
            }
        }

        private async Task RunCycleAsync(IReadOnlyCollection<IDownloadModule> initializedModules)
        {
            var canStartNewDownloads = false;
            try
            {
                var currentNoDownloadIds = await GetNoDownloadIdsAsync();
                noDownloadIds.Clear();
                noDownloadIds.UnionWith(currentNoDownloadIds);

                var excludedIds = new HashSet<string>(noDownloadIds, StringComparer.OrdinalIgnoreCase);
                excludedIds.UnionWith(completedIds);
                foreach (var consumer in initializedModules.OfType<IExcludedWorkConsumer>())
                    consumer.SetExcludedWorkIds(excludedIds);
                canStartNewDownloads = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get no-download IDs; no new task will start this cycle: {ex.Message}");
            }

            var now = DateTime.UtcNow;
            foreach (var module in initializedModules)
                if (now - lastUpdates[module] >= module.UpdateInterval)
                {
                    try
                    {
                        await module.UpdateAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{module.Name}] Update exception: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                    finally
                    {
                        lastUpdates[module] = now;
                    }
                }

            if (canStartNewDownloads)
                await StartNewDownloadsAsync(initializedModules);

            await CheckDownloadsAsync();
            Console.WriteLine($"[Scheduler] NoDownload:{noDownloadIds.Count} Completed:{completedIds.Count} Downloading:{downloading.Count}");
        }

        private async Task StartNewDownloadsAsync(IEnumerable<IDownloadModule> initializedModules)
        {
            var availableSlots = Math.Min(NewWorkLimit, MaxDownloadingWorks - downloading.Count);
            if (availableSlots <= 0)
                return;

            var moduleList = initializedModules.ToList();
            var candidates = new List<BaseWork>();
            foreach (var module in moduleList)
                try
                {
                    candidates.AddRange(module.GetDownloadCandidates());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{module.Name}] Failed to get download candidates: {ex.Message}");
                }

            var deduplicatedWorks = candidates
                .Where(work => !string.IsNullOrWhiteSpace(work.Id))
                .Where(work => work.IgnoreNoDownload || !noDownloadIds.Contains(work.Id))
                .Where(work => !completedIds.Contains(work.Id))
                .Where(work => !downloading.ContainsKey(work.Id))
                .GroupBy(work => work.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var moduleQueues = moduleList.ToDictionary(
                module => module,
                module => new Queue<BaseWork>(deduplicatedWorks.Where(work => ReferenceEquals(work.Module, module))));
            var selectedWorks = new List<BaseWork>();
            while (selectedWorks.Count < availableSlots && moduleQueues.Values.Any(queue => queue.Count > 0))
                foreach (var module in moduleList)
                    if (selectedWorks.Count < availableSlots && moduleQueues[module].TryDequeue(out var work))
                        selectedWorks.Add(work);

            foreach (var work in selectedWorks)
            {
                if (!downloading.TryAdd(work.Id, work))
                    continue;

                try
                {
                    if (!await work.Module.StartDownloadAsync(work.Id))
                        downloading.Remove(work.Id);
                }
                catch (Exception ex)
                {
                    downloading.Remove(work.Id);
                    Console.WriteLine($"[{work.Module.Name}] Start download exception {work.Id}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        private async Task CheckDownloadsAsync()
        {
            if (downloading.Count == 0)
                return;

            using var LID = new LID(); // 只在检查下载时加载模型，避免长期占用显存
            foreach (var pair in downloading.ToList())
            {
                var work = pair.Value;
                try
                {
                    switch (await work.Module.CheckDownloadAsync(work.Id, LID))
                    {
                        case DownloadCheckResult.Completed:
                            downloading.Remove(pair.Key);
                            completedIds.Add(pair.Key);
                            break;
                        case DownloadCheckResult.Retry:
                        case DownloadCheckResult.SourceUnavailable:
                            downloading.Remove(pair.Key);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{work.Module.Name}] Check download exception {work.Id}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        private static async Task<HashSet<string>> GetNoDownloadIdsAsync()
        {
            string? response = null;
            for (int i = 5; i > 0; --i)
                try
                {
                    using var httpResponse = await httpClient.GetAsync(QueryAddress);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine(await httpResponse.Content.ReadAsStringAsync());
                        throw new Exception("HTTP Not Success");
                    }
                    response = await httpResponse.Content.ReadAsStringAsync();
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Request Fail :" + ex.Message);
                    Thread.Sleep(20);
                }

            if (response is null)
            {
                Console.WriteLine("Fail to connnect to DLSiteHelperServer");
                throw new Exception("abort download");
            }

            return response
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
