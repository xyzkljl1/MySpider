using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IDManLib;
using System.Threading;
using System.Security.Policy;
using System.Security.Cryptography;
using LanguageCheck;
using Whisper.net.Wave;
using System.Diagnostics;
using MoreLinq;
using MySpider.Core;

namespace asmr.one
{
    using static System.Runtime.InteropServices.JavaScript.JSType;
    public struct IDMTask
    {
        public string name;
        public string dir;
        public string url;
    }
    class Work : BaseWork
    {
        private readonly Fetcher module;

        public Work(Fetcher module)
        {
            this.module = module;
        }

        public override string Id => RJ;
        public override IDownloadModule Module => module;
        public override bool IgnoreNoDownload => module.IsTestWork(source_id);
        public class File_
        {
            public string MD5Sum(string input)
            {
                // step 1, calculate MD5 hash from input

                MD5 md5 = System.Security.Cryptography.MD5.Create();
                byte[] inputBytes = System.Text.Encoding.Unicode.GetBytes(input);
                byte[] hash = md5.ComputeHash(inputBytes);

                // step 2, convert byte array to hex string

                StringBuilder sb = new StringBuilder();

                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("X2"));
                }

                return sb.ToString();
            }
            public File_(string _n, string _d, string _u, string? _tmpIdentityName = null)
            {
                downloaded = false;
                name = _n;
                subdir = _d;
                url = _u;
                //临时文件名，用相对路径的hash以防止重名并保证重启后不重新下载
                //下载时保留响应信息推导的扩展名，签名确定的最终名称在归档时使用
                var temporaryName = _tmpIdentityName ?? name;
                tmp_name = MD5Sum($"{subdir}/{temporaryName}");
                tmp_name += Path.GetExtension(temporaryName);
            }
            public string tmp_name;
            public string name;
            public string subdir;//相对目录
            public string url;
            public bool downloaded;//仅用于下载任务部分失败时排除已下载的
        }
        public bool r = false;
        public int source_id = 0;
        public string RJ = "";
        public string title = "";
        public int group = 0;//社团(maker/group/circle)的id
        public List<File_> files = new List<File_>();
        public int fail_ct = 0;
        public bool source_unavailable = false;
        public bool cursor_resolved = false;
    }
    public class Fetcher : IDownloadModule, IExcludedWorkConsumer
    {
        private enum RequestResult
        {
            Good,
            Skip,
            Bad
        };
        private sealed record UrlCheckResult(RequestResult Result, string? MediaType, string? FileName);
        private static readonly Dictionary<string, string> MediaTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["audio/mpeg"] = ".mp3",
            ["audio/mp3"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["video/mp4"] = ".mp4",
            ["video/quicktime"] = ".mov",
            ["video/x-quicktime"] = ".mov",
            ["audio/flac"] = ".flac",
            ["audio/x-flac"] = ".flac",
            ["audio/wav"] = ".wav",
            ["audio/x-wav"] = ".wav",
            ["audio/aac"] = ".aac",
            ["audio/ogg"] = ".ogg",
            ["audio/webm"] = ".webm",
            ["video/webm"] = ".webm",
            ["audio/x-ms-wma"] = ".wma",
            ["video/x-ms-wmv"] = ".wmv"
        };
        //几个中文社团的id，前面加上RG则是DLSite的RG号(如RG48509),同时是ASMRONE的circleId
        static private List<int> ChineseGroupId = new List<int> { 37402, 39322, 39804, 40142, 44853, 46806, 47550, 48509, 49620, 50114, 53009, 55123, 57900, 63016, 64294, 63553, 64435, 64486,
                                                                  65763, 68414, 68744, 70687, 74042, 74454, 1001551, 1005315, 1005809,
                                                                  1006167, 1001621,1008739, 1009187, 1009377, 1011490, 1012045, 1012472,1013694, 1017685, 1029695, 1036219, 1045004, 1048599, 1052118, 1054049, 1054434, 1066326, 1067886 };
        //IDM传入长度超过256的下载目的地会出现问题，因此入口传入的临时目录不能太长
        private readonly string TmpDir;
        private readonly IDownloadDirectoryManager downloadDirectoryManager;
        private readonly string ffmpegPath;
        private ICIDMLinkTransmitter2? idm;
        private HttpClient httpClient;
        CookieContainer cookies_container = new CookieContainer();
        private int process_id = 0;
        string bearer_token = "";
        //id to  work,此处的id是asmrone的id，可能不等于dlsite id
        private Dictionary<int, Work> works = new Dictionary<int, Work>();
        private Dictionary<string, Work> works_by_rj = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
        private static List<string> audio_extensions = new List<string> { "mp3", "wav", "wave", "flac", "wma", "aac", "m4a", "mp4", "wmv" };
        private static readonly HashSet<string> media_extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "wav", "wave", "flac", "ogg", "wma", "aac", "m4a", "mp4", "mov", "webm", "mkv", "wmv", "asf"
        };
        public HashSet<string> exclude_extensions = new HashSet<string> { "png", "jpg", "jpeg", "gif", "webp", "tiff", "jfif", "bmp", "txt", "pdf" };
        private static HashSet<string> wavflac_extensions = new HashSet<string> { ".wav", ".wave", ".flac" };
        private static readonly string RuntimeDirectory =
            Path.GetDirectoryName(typeof(Fetcher).Assembly.Location)!;

        private Queue<IDMTask> tasks = new Queue<IDMTask>();
        private int download_interval = 1000 * 30 * 60;//每半小时尝试一次下载
        private bool auto_start = false;//true:分批向IDM发送任务并立刻开始下载任务 false:一次向IDM发送所有任务，不立刻开始下载(等待IDM的每日自动队列下载)
        private int test_id = -1;
        private int last_source_id = 0;
        private HashSet<string> excludedWorkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool hasExcludedWorkIds = false;
        private string cursorPath = "";
        private int cursor = 0;
        public string Name => "ASMR.ONE";
        public TimeSpan UpdateInterval => TimeSpan.FromDays(14);
        internal bool IsTestWork(int sourceId) => test_id == sourceId;
        public Fetcher(
            IDownloadDirectoryManager downloadDirectoryManager,
            string proxy,
            string ffmpegPath)
        {
            this.downloadDirectoryManager = downloadDirectoryManager ?? throw new ArgumentNullException(nameof(downloadDirectoryManager));
            this.ffmpegPath = ffmpegPath;
            TmpDir = downloadDirectoryManager.GetTemporaryDirectory(Name);
            process_id = System.Diagnostics.Process.GetCurrentProcess().Id;
            System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            {
                //直连可以连接和下载，问题是经常抽风，而代理可以稳定连接，用SNI代理也无法改善
                //直连时api.asmr.one api.asmr-100.com api.asmr-200.com api.asmr-300.com中有的时不时能连上？
                var handler = new HttpClientHandler()
                {
                    MaxConnectionsPerServer = 256,
                    UseCookies = true,
                    CookieContainer = cookies_container,
                    Proxy = new WebProxy(proxy, false)
                };
                httpClient = new HttpClient(handler);
                httpClient.Timeout = new TimeSpan(0, 0, 35);
                httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.asmr.one");
                httpClient.DefaultRequestHeaders.AcceptEncoding.ParseAdd("none");
                httpClient.DefaultRequestHeaders.Add("origin", "https://www.asmr.one");
                //出现了人机验证问题，同时浏览器可以正常访问，在使用edge登录/加上sec字段/更新user-agent/经过一段时间后不再需要人机验证，why？
                /*
                httpClient.DefaultRequestHeaders.Referrer=new Uri("https://www.asmr.one/");
                httpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\" Not A; Brand\";v=\"99\", \"Chromium\";v=\"99\", \"Microsoft Edge\";v=\"99\"");
                httpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
                httpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
                httpClient.DefaultRequestHeaders.Add("sec-fetch-dest", "emoty");
                httpClient.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
                httpClient.DefaultRequestHeaders.Add("sec-fetch-site", "same-site");
*/
                httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
                httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8");
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/104.0.0.0 Safari/537.36");
            }
        }
        private void CleanupOldTemporaryDirectories()
        {
            const int retentionDays = 30;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var deleted = 0;
            var kept = 0;
            var failed = 0;

            foreach (var path in Directory.EnumerateDirectories(TmpDir).ToList())
                try
                {
                    var directory = new DirectoryInfo(path);
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        failed++;
                        Console.WriteLine("Skip Cleanup Temporary Reparse Point:" + path);
                        continue;
                    }

                    var lastWriteTime = directory.LastWriteTimeUtc;
                    if (lastWriteTime >= cutoff)
                    {
                        kept++;
                        continue;
                    }

                    Directory.Delete(path, true);
                    deleted++;
                    Console.WriteLine($"Cleanup Old Temporary Directory:{path} LastWriteTime:{lastWriteTime:O}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"Cleanup Old Temporary Directory Fail:{ex.Message}:{path}");
                }

            Console.WriteLine($"Cleanup Temporary Directory Done Deleted:{deleted} Kept:{kept} Failed:{failed}");
        }
        public async Task<bool> InitializeAsync()
        {
            try
            {
                var dataDirectory = Path.Combine(RuntimeDirectory, ".asmrone");
                cursorPath = Path.Combine(dataDirectory, "cursor.txt");
                Directory.CreateDirectory(dataDirectory);
                if (File.Exists(cursorPath))
                {
                    var cursorText = File.ReadAllText(cursorPath).Trim();
                    if (!int.TryParse(cursorText, out cursor) || cursor < 0)
                        throw new InvalidOperationException("ASMR.ONE cursor.txt is invalid.");
                    Console.WriteLine($"[ASMR.ONE] Loaded cursor: {cursor}");
                }
                else
                {
                    Console.WriteLine("[ASMR.ONE] No saved cursor; scanning from the beginning.");
                }
                last_source_id = cursor;

                if (!Directory.Exists(TmpDir))
                    Directory.CreateDirectory(TmpDir);
                CleanupOldTemporaryDirectories();
                foreach (var directory in downloadDirectoryManager.FinalDirectories)
                    if (!Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                idm = new CIDMLinkTransmitter();
                if (!await Login())
                {
                    Console.WriteLine("Login Fail,Exiting...");
                    return false;
                }

                //将IDM任务分散发送以避免拥堵
                _ = Task.Run(() => SendingIDMTask());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return false;
            }
        }

        public Task UpdateAsync()
        {
            return FetchWorkList();
        }

        public void SetExcludedWorkIds(IReadOnlySet<string> ids)
        {
            excludedWorkIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            hasExcludedWorkIds = true;
            AdvanceCursor();
        }

        public IEnumerable<BaseWork> GetDownloadCandidates()
        {
            return works.Values
                .Where(work => work.source_id > cursor && !work.source_unavailable)
                .Cast<BaseWork>()
                .ToList();
        }

        private void AdvanceCursor()
        {
            if (!hasExcludedWorkIds || last_source_id <= cursor || cursorPath == "")
                return;

            var firstPendingSourceId = works.Values
                .Where(work => work.source_id > cursor)
                .Where(work => !work.cursor_resolved && !excludedWorkIds.Contains(work.RJ))
                .Select(work => work.source_id)
                .DefaultIfEmpty(last_source_id + 1)
                .Min();
            var newCursor = Math.Min(last_source_id, firstPendingSourceId - 1);
            if (newCursor <= cursor)
                return;

            var oldCursor = cursor;
            var temporaryCursorPath = cursorPath + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryCursorPath,
                    newCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(temporaryCursorPath, cursorPath, true);
                cursor = newCursor;
                Console.WriteLine($"[ASMR.ONE] Cursor advanced: {oldCursor}->{newCursor}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASMR.ONE] Save cursor failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryCursorPath))
                        File.Delete(temporaryCursorPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ASMR.ONE] Delete temporary cursor failed: {ex.Message}");
                }
            }
        }

        private bool IsChinese(LID LID, Work work)
        {
            if (ChineseGroupId.Contains(work.group))
                return true;
            string src_dir = Path.Combine(TmpDir, work.title);
            var files = work.files.Where(file => file.downloaded && IsAudio(file.name))
                .Select(file => Path.Combine(src_dir, file.tmp_name))
                .ToList()
                .Shuffle();
            int ct = 0;
            // 随机抽取文件检验，不能判断则使用下一个文件，最多重复3次
            foreach (var file in files)
                if (ct <= 3)
                {
                    var ret = LID.IsChinese(file);
                    if (ret is null)
                        ct++;
                    else
                        return (bool)ret;
                }
            return false;
        }
        private static bool IsR(LID LID, Work work)
        {
            return work.r;
        }
        public void SendingIDMTask()
        {
            int interval = 60 * 1000 * 5;//每隔300s发送一次
            int send_ct = 0;
            try
            {
                while (true)
                {
                    lock (tasks)
                    {
                        if (tasks.Count > 0 && interval > 0)
                        {
                            try
                            {
                                if (idm is null)
                                    idm = new CIDMLinkTransmitter();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("ReCreate IDM Instance Fail，Abort Download Try:" + ex.Message);
                                Thread.Sleep(interval * 10);
                                continue;
                            }
                            //auto_start为真时根据剩余任务数量均摊，至少发送一个，否则发送全部
                            int ct = auto_start ? Math.Max(tasks.Count / Math.Max(download_interval / interval, 1), 1) : tasks.Count;
                            for (int i = 0; i < ct; i++)
                            {
                                try
                                {
                                    var task = tasks.Dequeue();
                                    //TODO:IDM未启动时，SendLinkToIDM可以自动启动IDM，然而有时还是会出现IDM崩溃、SendLinkToIDM抛出RPC服务不可用的异常、无法自动启动IDM的情况，WHY？或许是因为缓存硬盘故障？
                                    idm.SendLinkToIDM(task.url, "", "", "", "", "", task.dir, task.name, auto_start ? 0x01 : 0x03);
                                }
                                catch (Exception ex)//任务太多或其它情况时idm服务可能卡死，此时终止该次下载尝试，而不终止程序，防止某个文件多的作品卡死idm导致反复重启
                                {
                                    Console.WriteLine("SendLinkToIDM Fail:" + ex.Message);
                                    Console.WriteLine("Discard IDM Instance,Abort Downloading Try");
                                    idm = null;
                                    break;
                                }
                            }
                        }
                        send_ct++;
                        if (send_ct * interval > 1000 * 60 * 60)
                        {
                            Console.WriteLine("Waiting sending to IDM Task:" + tasks.Count);
                            send_ct = 0;
                        }
                    }
                    Thread.Sleep(interval);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fatal Exception:" + ex.Message);
                Console.WriteLine("Stop Sending IDM Task");
            }
        }
        string FileNameCheck(string name)//检查单级目录/文件名是否合法
        {
            string ret = name;
            ret = Regex.Replace(ret, "[/\\\\?*<>:\"\t|]", "_");
            /* 目录以空格结尾会导致windows和IDM的bug
             * 该目录无法正常删除(可通过压缩文件勾选删除源文件删除)，且打开无空格版本目录会导向该目录
             * 似乎以.结尾也会有问题
             */
            while (ret.EndsWith(" ") || ret.EndsWith("."))
                ret = ret.Substring(0, ret.Length - 1);
            return ret;
        }
        public async Task<DownloadCheckResult> CheckDownloadAsync(string workId, LID LID)
        {
            var work = works_by_rj[workId];

            var RJ = work.RJ;
            var src_dir = Path.Combine(TmpDir, work.title);
            foreach (var file in work.files)
                file.downloaded = File.Exists(Path.Combine(src_dir, file.tmp_name));
            if (work.files.All(f => f.downloaded))
            {
                try
                {
                    var directoryKind = IsChinese(LID, work)
                        ? DownloadDirectoryKind.Chinese
                        : IsR(LID, work)
                            ? DownloadDirectoryKind.ReliableR
                            : DownloadDirectoryKind.Reliable;
                    var downloadedFiles = new List<DownloadedFile>(work.files.Count);
                    foreach (var file in work.files)
                    {
                        if (isWavOrFlac(file.name))
                            if (await ConvertToMp3(new FileInfo($"{src_dir}/{file.tmp_name}")))
                            {
                                file.tmp_name += ".mp3";
                                file.name += ".mp3";
                            }
                        downloadedFiles.Add(new DownloadedFile(
                            Path.Combine(src_dir, file.tmp_name),
                            file.subdir,
                            file.name));
                    }

                    downloadDirectoryManager.FinalizeDownload(new CompletedDownload(
                        work.RJ,
                        work.title,
                        src_dir,
                        directoryKind,
                        downloadedFiles));
                    work.files.Clear();
                    excludedWorkIds.Add(work.RJ);
                    AdvanceCursor();
                    Console.WriteLine(string.Format("Download {0} Done", work.RJ));
                    return DownloadCheckResult.Completed;
                }
                catch (Exception ex)
                {
                    //保留已下载文件和文件列表，只释放活动任务并在下一轮重试整理
                    Console.WriteLine("Can't Finalize Finished Work " + RJ + ":" + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    work.fail_ct = 0;
                    return DownloadCheckResult.Retry;
                }
            }

            work.fail_ct++;
            if (work.fail_ct > 3 * (1000 * 60 * 60 * 24 / download_interval))//三天没下载完视作失败
            {
                work.fail_ct = 0;
                work.files.Clear();
                return DownloadCheckResult.Retry;
            }

            return DownloadCheckResult.Downloading;
        }
        public async Task<bool> ConvertToMp3(FileInfo fi)
        {
            var dest = fi.FullName + ".mp3";
            var tempDest = dest + ".converting.mp3";
            try
            {
                if (File.Exists(tempDest))
                    File.Delete(tempDest);

                using System.Diagnostics.Process process = new System.Diagnostics.Process();
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    FileName = ffmpegPath
                };
                // 加\\?\以支持长路径,C#自身的api支持长路径不需要加，但是某些库不支持
                startInfo.ArgumentList.Add("-nostdin");
                startInfo.ArgumentList.Add("-hide_banner");
                startInfo.ArgumentList.Add("-loglevel");
                startInfo.ArgumentList.Add("error");
                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add($"\\\\?\\{fi.FullName}");
                startInfo.ArgumentList.Add("-vn");
                startInfo.ArgumentList.Add("-ar");
                startInfo.ArgumentList.Add("32000");
                startInfo.ArgumentList.Add("-ac");
                startInfo.ArgumentList.Add("2");
                startInfo.ArgumentList.Add("-b:a");
                startInfo.ArgumentList.Add("320k");
                startInfo.ArgumentList.Add($"\\\\?\\{tempDest}");
                process.StartInfo = startInfo;
                process.Start();
                // 两个输出流都需要持续读取，否则缓冲区满后ffmpeg会卡死
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        process.Kill(true);
                    await process.WaitForExitAsync();
                    Console.WriteLine($"Convert Fail Timeout: {await outputTask} {await errorTask}");
                    return false;
                }

                var output = await outputTask;
                var error = await errorTask;
                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"Convert Fail ExitCode {process.ExitCode}: {output} {error}");
                    return false;
                }

                var tempFile = new FileInfo(tempDest);
                if (!tempFile.Exists || tempFile.Length == 0)
                {
                    Console.WriteLine($"Convert Fail Empty Output: {fi.FullName}");
                    return false;
                }

                File.Move(tempDest, dest, true);
                fi.Delete();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Convert Fail:{e.Message}: {fi.FullName}");
            }
            finally
            {
                // 转换失败只删除未完成的临时输出，始终保留源文件
                try
                {
                    if (File.Exists(tempDest))
                        File.Delete(tempDest);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Delete Convert Temp File Fail:{e.Message}: {tempDest}");
                }
            }
            return false;
        }
        public async Task<bool> StartDownloadAsync(string workId)
        {
            var work = works_by_rj[workId];
            if (work.files.Count == 0)
            {
                var (tracks_str, noTracks) = await GetTracks(work.source_id);
                if (noTracks)
                {
                    Console.WriteLine("No Tracks " + work.RJ);
                    work.source_unavailable = true;
                    work.cursor_resolved = true;
                    AdvanceCursor();
                    return false;
                }
                if (string.IsNullOrEmpty(tracks_str))
                {
                    Console.WriteLine("Can't Get Track_1 " + work.RJ);
                    return false;
                }

                bool get_track_success = true;
                foreach (var track in (JArray)JsonConvert.DeserializeObject(tracks_str)!)
                    get_track_success &= await ParseTracks(work, "", track.ToObject<JObject>()!);
                if (!get_track_success)
                {
                    Console.WriteLine("Can't Get Track_2 " + work.RJ);
                    work.files.Clear();
                    return false;
                }
                if (work.files.Count == 0)
                {
                    Console.WriteLine("No Downloadable Track " + work.RJ);
                    work.source_unavailable = true;
                    work.cursor_resolved = true;
                    AdvanceCursor();
                    return false;
                }
            }

            work.files = work.files
                .DistinctBy(file => file.tmp_name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in work.files)
                if (!file.downloaded)
                {
                    var dir = TmpDir + "/" + work.title;
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    //兼容旧版本按签名扩展名保存的临时文件，避免重新下载
                    var previousName = Path.ChangeExtension(file.tmp_name, Path.GetExtension(file.name));
                    if (!File.Exists(Path.Combine(dir, file.tmp_name)) &&
                        File.Exists(Path.Combine(dir, previousName)))
                        file.tmp_name = previousName;
                    //程序启动前就已经下载的文件
                    if (File.Exists($"{dir}/{file.tmp_name}"))
                    {
                        file.downloaded = true;
                        continue;
                    }
                    /*
                        * 使用生成的文件名下载
                        * 由于迷之原因，SendLinkToIDM时文件名中的一些字符(例如"母"/"食")会被替换成其它东西，Chrome插件则可以正确下载包含这些字符的文件
                        * 可能是编码问题，尚不清楚如何解决，通过重命名绕过
                    */
                    lock (tasks)
                        tasks.Enqueue(new IDMTask { url = file.url, dir = dir, name = file.tmp_name });
                }

            work.fail_ct = 0;
            return true;
        }
        private static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return "";
            extension = extension.Trim().ToLowerInvariant();
            if (!extension.StartsWith('.'))
                extension = "." + extension;
            return Regex.IsMatch(extension, "^\\.[a-z0-9]{1,10}$") ? extension : "";
        }
        private static string GetFileExtension(UrlCheckResult result, string url, string title)
        {
            if (!string.IsNullOrWhiteSpace(result.MediaType) && MediaTypeExtensions.TryGetValue(result.MediaType, out var mediaTypeExtension))
                return mediaTypeExtension;

            var contentDispositionExtension = NormalizeExtension(Path.GetExtension(result.FileName?.Trim('"')));
            if (contentDispositionExtension != "")
                return contentDispositionExtension;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var urlExtension = NormalizeExtension(Path.GetExtension(uri.AbsolutePath));
                if (urlExtension != "")
                    return urlExtension;
            }

            return NormalizeExtension(Path.GetExtension(title));
        }
        private static bool HasAscii(ReadOnlySpan<byte> data, int offset, string value)
        {
            if (offset < 0 || offset + value.Length > data.Length)
                return false;
            for (var i = 0; i < value.Length; i++)
                if (data[offset + i] != (byte)value[i])
                    return false;
            return true;
        }
        private static int FindAscii(ReadOnlySpan<byte> data, string value)
        {
            for (var offset = 0; offset + value.Length <= data.Length; offset++)
                if (HasAscii(data, offset, value))
                    return offset;
            return -1;
        }
        private static string DetectIsoBaseMediaExtension(ReadOnlySpan<byte> data)
        {
            //ftyp通常是第一个box；也允许前面存在一个很小的free/wide box
            for (var typeOffset = 4; typeOffset + 8 <= data.Length; typeOffset++)
            {
                if (!HasAscii(data, typeOffset, "ftyp"))
                    continue;

                var boxOffset = typeOffset - 4;
                var boxSize = ((uint)data[boxOffset] << 24) |
                              ((uint)data[boxOffset + 1] << 16) |
                              ((uint)data[boxOffset + 2] << 8) |
                              data[boxOffset + 3];
                if (boxSize < 16)
                    return "";
                var availableBoxLength = (int)Math.Min((uint)(data.Length - boxOffset), boxSize);
                if (availableBoxLength < 16)
                    return "";
                var brandStart = typeOffset + 4;
                var brandEnd = boxOffset + availableBoxLength;

                //QuickTime品牌优先；RJ01604150的文件头为“ftyp qt  ”
                for (var brandOffset = brandStart; brandOffset + 4 <= brandEnd; brandOffset += 4)
                    if (brandOffset != brandStart + 4 && HasAscii(data, brandOffset, "qt  "))
                        return ".mov";

                if (HasAscii(data, brandStart, "M4A ") || HasAscii(data, brandStart, "M4P "))
                    return ".m4a";
                //同时检查主品牌和兼容品牌，跳过主品牌后面的4字节版本号。
                for (var brandOffset = brandStart; brandOffset + 4 <= brandEnd; brandOffset += 4)
                {
                    if (brandOffset == brandStart + 4)
                        continue;
                    if (HasAscii(data, brandOffset, "M4V ") ||
                        HasAscii(data, brandOffset, "isom") || HasAscii(data, brandOffset, "iso") ||
                        HasAscii(data, brandOffset, "mp4") || HasAscii(data, brandOffset, "avc1") ||
                        HasAscii(data, brandOffset, "dash") || HasAscii(data, brandOffset, "MSNV"))
                        return ".mp4";
                }
                return "";
            }
            return "";
        }
        private static string DetectFileExtensionFromSignature(ReadOnlySpan<byte> data)
        {
            //文本BOM不是媒体签名；尤其FF FE也满足宽松的MPEG同步位判断
            if ((data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) ||
                                      (data[0] == 0xFE && data[1] == 0xFF))) ||
                (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF))
                return "";
            if (data.Length >= 12 && HasAscii(data, 0, "RIFF") && HasAscii(data, 8, "WAVE"))
                return ".wav";
            if (HasAscii(data, 0, "fLaC"))
                return ".flac";
            if (HasAscii(data, 0, "OggS"))
                return ".ogg";
            if (HasAscii(data, 0, "ID3"))
                return ".mp3";
            if (data.Length >= 16 &&
                data[0] == 0x30 && data[1] == 0x26 && data[2] == 0xB2 && data[3] == 0x75 &&
                data[4] == 0x8E && data[5] == 0x66 && data[6] == 0xCF && data[7] == 0x11 &&
                data[8] == 0xA6 && data[9] == 0xD9 && data[10] == 0x00 && data[11] == 0xAA &&
                data[12] == 0x00 && data[13] == 0x62 && data[14] == 0xCE && data[15] == 0x6C)
                return ".asf";
            var isoBaseMediaExtension = DetectIsoBaseMediaExtension(data);
            if (isoBaseMediaExtension != "")
                return isoBaseMediaExtension;

            //EBML同时用于WebM和Matroska，只有找到DocType后才能安全决定扩展名
            if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            {
                if (FindAscii(data, "webm") >= 0)
                    return ".webm";
                if (FindAscii(data, "matroska") >= 0)
                    return ".mkv";
            }

            //区分MP3帧同步头和AAC ADTS头
            if (data.Length >= 3 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
            {
                var version = (data[1] >> 3) & 0x03;
                var layer = (data[1] >> 1) & 0x03;
                var bitrate = (data[2] >> 4) & 0x0F;
                var sampleRate = (data[2] >> 2) & 0x03;
                if (version != 0x01 && layer == 0x01 && bitrate is > 0 and < 0x0F && sampleRate != 0x03)
                    return ".mp3";
                if ((data[1] & 0xF6) == 0xF0)
                    return ".aac";
            }
            return "";
        }
        private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(Func<Task<HttpResponseMessage>> send)
        {
            while (true)
            {
                var response = await send();
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    return response;

                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta
                    ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMinutes(30);
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromMinutes(1);
                response.Dispose();
                Console.WriteLine($"[ASMR.ONE] HTTP 429; waiting {Math.Ceiling(delay.TotalSeconds)} seconds until {DateTimeOffset.Now + delay:O}, then retrying the request.");
                await Task.Delay(delay);
            }
        }
        private async Task<string> ProbeFileExtension(string url)
        {
            const int probeLength = 64;
            try
            {
                using var response = await SendWithRateLimitRetryAsync(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, probeLength - 1);
                    request.Headers.AcceptEncoding.Clear();
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                });
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Probe File Type Bad HTTP {(int)response.StatusCode}");
                    return "";
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[probeLength];
                var readLength = 0;
                while (readLength < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(readLength, buffer.Length - readLength));
                    if (read == 0)
                        break;
                    readLength += read;
                }
                return DetectFileExtensionFromSignature(buffer.AsSpan(0, readLength));
            }
            catch (Exception ex)
            {
                //探测失败不影响旧下载逻辑，只在日志中记录一行并退回响应头/URL扩展名
                Console.WriteLine("Probe File Type Bad:" + ex.Message);
                return "";
            }
        }
        private async Task<UrlCheckResult> CheckURL(string? url, bool is_audio)
        {
            //DLSite的文件是分段压缩的，此处不是，所以有单个文件会超过2G
            if (url == "" || url is null)
                return new UrlCheckResult(RequestResult.Bad, null, null);
            try
            {
                /*
                 如果不指定HttpCompletionOption.ResponseHeadersRead，即使是Head请求也会分配缓冲区(但是不会下载)
                 分配的缓冲区占用内存在任务管理器中显示为"提交"，在VS调试工具中显示为"专用"
                 这些内存不会随着response析构/httpclient.Dispose/GC.Collect而释放，Why??
                */
                using (var response = await SendWithRateLimitRetryAsync(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                }))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var mediaType = response.Content.Headers.ContentType?.MediaType;
                        var contentDisposition = response.Content.Headers.ContentDisposition;
                        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
                        if (response.Content.Headers.Contains("Content-Length"))
                        {
                            //单位:byte，排除小于200KB的音频，以避免坑爹的情况，如RJ066580
                            var len = Int64.Parse(response.Content.Headers.GetValues("Content-Length").First());
                            if (len == 0)
                                return new UrlCheckResult(RequestResult.Skip, mediaType, fileName);
                            else if (is_audio && len < 1024 * 200)
                                return new UrlCheckResult(RequestResult.Skip, mediaType, fileName);
                            else
                                return new UrlCheckResult(RequestResult.Good, mediaType, fileName);
                        }
                        else//有的content类型不带length
                            return new UrlCheckResult(RequestResult.Good, mediaType, fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                //请求失败什么都不做
                Console.WriteLine("Check URL Bad:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            return new UrlCheckResult(RequestResult.Bad, null, null);
        }
        private bool IsUselessFiles(string title)
        {
            var ext = Path.GetExtension(title.ToLower()).TrimStart(new char[] { '.' });
            return exclude_extensions.Contains(ext);
        }
        private static bool IsAudio(string title)
        {
            var ext = Path.GetExtension(title.ToLower()).TrimStart(new char[] { '.' });
            return audio_extensions.Contains(ext);
        }
        private static bool IsMediaExtension(string extension)
        {
            return media_extensions.Contains(extension.TrimStart('.'));
        }
        private static bool IsMedia(string title)
        {
            return IsMediaExtension(Path.GetExtension(title));
        }
        public static bool isWavOrFlac(string name)
        {
            return wavflac_extensions.Contains(Path.GetExtension(name).ToLower());
        }
        private async Task<bool> ParseTracks(Work work, string parent, JObject json)
        {
            if (json == null)
                return false;
            if (!json.ContainsKey("type"))
                return false;
            if (!json.ContainsKey("title"))
                return false;
            if (json.Value<string>("type") == "folder")
            {
                bool ret = true;
                //由于谜之原因，目录里会有非法字符，如RJ047447
                //部分作品自带乱码，如RJ066580
                //可能有多于1级目录，此时拆开分别检查
                var dir = parent;
                foreach (var sub_dir in json.Value<string>("title")!.Split(new char[] { '\\', '/' }))
                {
                    var tmp = FileNameCheck(sub_dir);
                    if (tmp != "")
                        dir += "/" + tmp;
                }
                if (json.ContainsKey("children"))
                    foreach (var item in json.Value<JArray>("children")!)
                        ret &= await ParseTracks(work, dir, item.ToObject<JObject>()!);
                return ret;
            }
            else if (json.ContainsKey("mediaDownloadUrl") || json.ContainsKey("mediaStreamUrl"))
            {
                var title = FileNameCheck(json.Value<string>("title")!);
                if (IsUselessFiles(title) || !IsMedia(title))
                    return true;

                //对于某些文件(常见于wav，mp4一般没有fast版)，mediaDownloadUrl是large.kiko-play-niptan.one下的原版文件，而streamLowQualityUrl/mediaStreamUrl中的一个或两个是fast.kiko-play-niptan.one下转换格式后的文件
                //由于large.kiko-play-niptan.one的rate limit严重，尽量使用另外两种
                var url_download = json.Value<string>("mediaDownloadUrl");
                //stream_url要加上token
                var streamAddress = json.Value<string>("mediaStreamUrl");
                var lowAddress = json.Value<string>("streamLowQualityUrl");
                var url_stream = string.IsNullOrEmpty(streamAddress) || streamAddress == url_download
                    ? streamAddress : streamAddress + "?token=" + bearer_token;
                var url_low = string.IsNullOrEmpty(lowAddress) || lowAddress == url_download
                    ? lowAddress : lowAddress == streamAddress ? url_stream : lowAddress + "?token=" + bearer_token;
                bool is_audio = IsAudio(title);
                string? url = null;
                UrlCheckResult? selectedResult = null;
                var canSkip = false;
                //按原顺序选择可用地址；普通地址成功后不再探测备用地址。
                //large地址仍尝试寻找非large替代来源，完全相同的地址只检查一次。
                foreach (var candidate in new[] { url_download, url_stream, url_low }
                             .Where(candidate => !string.IsNullOrEmpty(candidate))
                             .Distinct(StringComparer.Ordinal))
                {
                    if (url is not null && candidate!.Contains("large.kiko-play-niptan.one"))
                        continue;
                    var result = await CheckURL(candidate, is_audio);
                    canSkip |= result.Result == RequestResult.Skip;
                    if (result.Result == RequestResult.Good)
                    {
                        url = candidate;
                        selectedResult = result;
                        if (!url!.Contains("large.kiko-play-niptan.one"))
                            break;
                    }
                }
                //没有可用地址时，只有明确的空文件或小音频才跳过；全部失败则返回解析失败。
                if (url is null && !canSkip && is_audio)
                    return false;
                if (!(url is null) && selectedResult is not null)
                {
                    // 优先使用服务器声明的媒体类型，避免URL后缀与实际文件类型不一致时IDM修改文件名
                    var fallbackExtension = GetFileExtension(selectedResult, url, title);
                    var fallbackTitle = fallbackExtension == "" ? title : Path.ChangeExtension(title, fallbackExtension);
                    var extension = await ProbeFileExtension(url);
                    if (!IsMediaExtension(extension))
                    {
                        Console.WriteLine($"Skip Non-Media Signature:{title}");
                        return true;
                    }
                    if (!string.Equals(extension, fallbackExtension, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"Correct Extension By Signature:{fallbackExtension} -> {extension} {title}");
                    title = Path.ChangeExtension(title, extension);
                    //临时名称用于IDM下载和完成检查，签名确定的名称用于最终归档
                    work.files.Add(new Work.File_(title, parent, url, fallbackTitle));
                }
                return true;
            }
            return false;
        }
        private async Task FetchWorkList()
        {
            try
            {
                Console.WriteLine("Start Fetch Work List");
                //seed不知道是什么,subtitle=1是带字幕，subtitle=0包含subtitle=1,page从1开始而非0
                string base_url = "https://api.asmr.one/api/works?order=id&sort=desc&page={0}&seed=35&subtitle=0";
                var first_page = await GetJson(string.Format(base_url, 1))!;
                if (first_page is null)
                    return;
                var total_count = first_page!.Value<JObject>("pagination")!.Value<Int32>("totalCount");
                var page_size = first_page.Value<JObject>("pagination")!.Value<Int32>("pageSize");
                var new_works = new List<KeyValuePair<int, Work>>();
                var source_ids_by_rj = works_by_rj.ToDictionary(pair => pair.Key, pair => pair.Value.source_id, StringComparer.OrdinalIgnoreCase);
                var new_last_source_id = last_source_id;
                var stop_fetching = false;
                for (int p = 0; p * page_size < total_count; p++)//变量p从0开始,页数为p+1
                {
                    var page = p == 0 ? first_page : await GetJson(string.Format(base_url, p + 1));
                    if (page is null)
                    {
                        Console.WriteLine("Fail Fetch Page {0}", p + 1);
                        return;
                    }
                    var list = page.Value<JArray>("works");
                    foreach (var item in list!)
                    {
                        var work_object = item.ToObject<JObject>()!;
                        var id = work_object.Value<Int32>("id");
                        if (test_id <= 0 && id <= last_source_id)
                        {
                            stop_fetching = true;
                            break;
                        }
                        new_last_source_id = Math.Max(new_last_source_id, id);
                        var type = work_object.Value<string>("source_type");
                        if (type != "DLSITE")
                        {
                            throw new Exception("not DLSITE");
                        }
                        if (test_id > 0 && id != test_id)
                            continue;
                        if (!works.ContainsKey(id))//此处只获取了基本信息，无需更新
                        {
                            var work = new Work(this);
                            work.source_id = id;
                            work.r = work_object.Value<bool>("nsfw");
                            if (!work.r)//忽略全年龄作品
                                continue;
                            work.RJ = work_object.Value<string>("source_id")!;
                            if (source_ids_by_rj.TryGetValue(work.RJ, out var existing_source_id))
                                throw new Exception($"Duplicate RJ {work.RJ}: ASMR.ONE IDs {existing_source_id} and {id}");
                            source_ids_by_rj.Add(work.RJ, id);
                            /*
                            //id即是RJ号，5位的补到6位，7位的补到8位；使用该网站给出的title，title可能为空如RJ087362
                            if (id < 1000000) //6位或更低
                                work.RJ = string.Format("RJ{0:D6}", id);
                            else if (id<100000000)//6~8位
                                work.RJ = string.Format("RJ{0:D8}", id);
                            else//8位以上(目前无)
                                work.RJ = string.Format("RJ{0:D10}", id);
                            */
                            //测试模式,只下载特定作品
                            work.group = work_object.Value<int>("circle_id");
                            work.title = string.Format("{0} {1}", work.RJ, work_object.Value<string>("title"));
                            work.title = FileNameCheck(work.title);
                            if (work.title.Length > 100)//IDM传入长度超过256的下载目的地会出现问题，因此裁剪title到100以预防
                                work.title = work.title.Substring(0, 100);
                            new_works.Add(new KeyValuePair<int, Work>(id, work));
                        }
                        if (test_id > 0 && id == test_id)
                        {
                            stop_fetching = true;
                            break;
                        }
                    }
                    if (stop_fetching)
                        break;
                    if (p % 100 == 0)
                        Console.WriteLine("Fetching {0} page", p);
                    //防止请求过快
                    Thread.Sleep(300);
                }
                foreach (var pair in new_works.OrderBy(pair => pair.Key))
                {
                    works.Add(pair.Key, pair.Value);
                    works_by_rj.Add(pair.Value.RJ, pair.Value);
                }
                last_source_id = new_last_source_id;
                AdvanceCursor();
                Console.WriteLine("Fetch Work List Done Added:{0} Total:{1}/{2}", new_works.Count, works.Count, total_count);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't Fetch Work List:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }
        public async Task<bool> Login()
        {
            JObject? jdoc = await PostJson("https://api.asmr.one/api/auth/me", "{\"name\": \"guest\", \"password\": \"guest\"}", Encoding.UTF8, "application/json");
            if (jdoc != null)
                if (jdoc.ContainsKey("token"))
                {
                    bearer_token = jdoc.Value<string>("token")!;
                    //可以再get验证一下，但是没必要
                    httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer_token);
                    return true;
                }
            return false;
        }
        private async Task<JObject?> GetJson(string addr)
        {
            var result = await Get(addr);
            if (result != null)
                return (JObject?)JsonConvert.DeserializeObject(result);
            return null;
        }
        private async Task<JObject?> PostJson(string addr, string data, Encoding encoding, string type)
        {
            var result = await Post(addr, data, encoding, type);
            if (result != null)
                return (JObject?)JsonConvert.DeserializeObject(result);
            return null;
        }
        private async Task<string?> Get(string addr)
        {
            for (int i = 5; i > 0; --i)
                try
                {
                    using (HttpResponseMessage response = await SendWithRateLimitRetryAsync(() => httpClient.GetAsync(addr)))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine(await response.Content.ReadAsStringAsync());
                            throw new Exception("HTTP Not Success");
                        }
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception e)
                {
                    string msg = e.Message;//e.InnerException.InnerException.Message;
                    Console.WriteLine("Request Fail :" + msg);
                    Thread.Sleep(20);
                }
            return null;
        }
        private async Task<(string? Content, bool NoTracks)> GetTracks(int sourceId)
        {
            var addr = string.Format("https://api.asmr.one/api/tracks/{0}", sourceId);
            for (int i = 5; i > 0; --i)
                try
                {
                    using var response = await SendWithRateLimitRetryAsync(() => httpClient.GetAsync(addr));
                    var content = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                        return (content, false);
                    if (content.Contains("No tracks found", StringComparison.OrdinalIgnoreCase))
                        return (null, true);

                    Console.WriteLine(content);
                    throw new Exception($"HTTP Not Success: {(int)response.StatusCode}");
                }
                catch (Exception e)
                {
                    Console.WriteLine("Request Fail :" + e.Message);
                    Thread.Sleep(20);
                }
            return (null, false);
        }
        private async Task<string?> Post(string addr, string data, Encoding encoding, string type)
        {
            for (int i = 5; i > 0; --i)
                try
                {
                    using (var content = new StringContent(data, encoding, type))
                    using (HttpResponseMessage response = await SendWithRateLimitRetryAsync(() => httpClient.PostAsync(addr, content)))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var x = await response.Content.ReadAsStringAsync();
                            throw new Exception("HTTP Not Success");
                        }
                        return await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception e)
                {
                    string msg = e.Message;//e.InnerException.InnerException.Message;
                    Console.WriteLine("Request Fail :" + msg);
                    Thread.Sleep(20);
                }
            return null;
        }
    }
}
