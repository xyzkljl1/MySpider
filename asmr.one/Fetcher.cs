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

namespace asmr.one
{
    using FuncStringPair = System.Collections.Generic.KeyValuePair<Func<Work, bool>, String>;
    public struct IDMTask
    {
        public string name;
        public string dir;
        public string url;
    }
    class Work
    {
        public enum Status
        {
            Waiting,
            Downloading,
            Done
        };
        public struct File_
        {
            public File_(String _n,String _d,String _u){
                name = _n;
                subdir = _d;
                url = _u;
                tmp_name = Guid.NewGuid().ToString();//下载用文件名
                if(name.Contains("."))//需要使用相同的后缀名以避免IDM弹窗
                    tmp_name+= "."+name.Split('.').Last();
            }
            public String tmp_name;
            public string name;
            public string subdir;//相对目录
            public string url;
        }
        public Status status=Status.Waiting;
        public bool r=false;
        public String RJ = "";
        public String title = "";
        public int group=0;//社团(maker/group/circle)的id
        public List<String> alter_dir = new List<string>();//由于一些坑爹的原因，可能会有多个
        public List<File_> files= new List<File_>();
        public int fail_ct=0;
    }
    class Fetcher
    {
        private enum RequestResult {
            Good,
            Skip,
            Bad
        };
        //依序检查是否符合条件，符合条件则下载到对应目录
        private List<FuncStringPair> RootDirs =new List<FuncStringPair> { 
                                            new FuncStringPair(IsChinese, "Z:/ASMR_Chinese"),
                                            new FuncStringPair(IsR, "Z:/ASMR_ReliableR"),
                                            new FuncStringPair(ReturnTrue, "Z:/ASMR_Reliable") };
        //几个中文社团的id，前面加上RG则是DLSite的RG号(如RG48509),同时是ASMRONE的circleId
        static private List<int> ChineseGroupId = new List<int> { 48509,37402,46806,39804, 57900, 64486, 64435 , 74042, 47550 };
        //如果某作品处于以下目录，则删除它们并强制重新下载
        private List<String> AlterDirs = new List<string>{ "Z:/ASMR_Unreliable", "Z:/ASMR_UnreliableR" };
        private String TmpDir = "E:/Tmp/MySpider/ASMRONE";
        public String query_addr = "http://127.0.0.1:4567/?QueryInvalidDLSite";
        private ICIDMLinkTransmitter2 idm = new CIDMLinkTransmitter();
        private HttpClient httpClient;
        CookieContainer cookies_container = new CookieContainer();
        private DateTime LastFetchTime =DateTime.MinValue;
        String bearer_token="";
        private Dictionary<int, Work> works = new Dictionary<int, Work>();
        private List<String> suffixs=new List<string> { ".mp3",".wav",".wave",".flac", ".wma",".mpa",".ram",".ra",".aac",".aif",".m4a",".tsa",".mp4",".wmv" };
        private Queue<IDMTask> tasks=new Queue<IDMTask>();
        private int download_interval = 1000 * 30 * 60;//每半小时尝试一次下载
        private bool auto_start = false;//true:分批向IDM发送任务并立刻开始下载任务 false:一次向IDM发送所有任务，不立刻开始下载(等待IDM的每日自动队列下载)
        private int test_id= -1;
        public Fetcher() {
            System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            {
                var handler = new HttpClientHandler()
                {
                    MaxConnectionsPerServer = 256,
                    UseCookies = true,
                    CookieContainer = cookies_container,
                    Proxy = new WebProxy("127.0.0.1:1196", false)
                };
                handler.ServerCertificateCustomValidationCallback = delegate { return true; };
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
        public async Task Start()
        {
            try
            {
                int index = 0;
                //清理临时目录
                if (Directory.Exists(TmpDir))
                    Directory.Delete(TmpDir, true);
                Directory.CreateDirectory(TmpDir);
                foreach(var pair in RootDirs)
                    if (!Directory.Exists(pair.Value))
                        Directory.CreateDirectory(pair.Value);
                if (!await Login())
                {
                    Console.WriteLine("Login Fail,Exiting...");
                    Thread.Sleep(100000);
                    return;
                }
                //将IDM任务分散发送以避免拥堵
                _ = Task.Run(() => SendingIDMTask());
                while (true)
                {
                    if (index % (24 * 7 * 2 * 1000*60*60/download_interval) == 0)//每2周
                        await FetchWorkList();
                    //一次下载太多会有429 Too Many Requests?
                    //429的文件会在一定时间内持续429，更换代理可能解除，经过一段时间可能解除
                    await Download(25,60);
                    CheckDownload();
                    Thread.Sleep(download_interval);
                    index++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }
        private static bool IsChinese(Work work)
        {
            return ChineseGroupId.Contains(work.group);
        }
        private static bool IsR(Work work)
        {
            return work.r;
        }
        private static bool ReturnTrue(Work w)
        {
            return true;
        }
        public void SendingIDMTask()
        {
            int interval = 60*1000*5;//每隔300s发送一次
            int send_ct = 0;
            try
            {
                while (true)
                {
                    lock(tasks)
                    {
                        if(tasks.Count>0&& download_interval > interval && interval>0)
                        {
                            try
                            {
                                if (idm is null)
                                    idm = new CIDMLinkTransmitter();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("ReCreate IDM Instance Fail，Abort Download Try:" + ex.Message);
                                Thread.Sleep(interval*10);
                                continue;
                            }
                            //auto_start为真时根据剩余任务数量均摊，至少发送一个，否则发送全部
                            int ct = auto_start ? Math.Max(tasks.Count / (download_interval / interval), 1) : tasks.Count;
                            for (int i=0; i < ct; i++)
                            {
                                try
                                {
                                    var task = tasks.Dequeue();
                                    //TODO:IDM未启动时，SendLinkToIDM可以自动启动IDM，然而有时还是会出现IDM崩溃、SendLinkToIDM抛出RPC服务不可用的异常、无法自动启动IDM的情况，WHY？或许是因为缓存硬盘故障？
                                    idm.SendLinkToIDM(task.url, "", "", "", "", "", task.dir, task.name, auto_start? 0x01:0x02);
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
                            Console.WriteLine("Left IDM Task:"+tasks.Count);
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
        String FileNameCheck(String name)//检查单级目录/文件名是否合法
        {
            String ret = name;
            ret = Regex.Replace(ret, "[/\\\\?*<>:\"|]", "_");
            /* 目录以空格结尾会导致windows和IDM的bug
             * 该目录无法正常删除(可通过压缩文件勾选删除源文件删除)，且打开无空格版本目录会导向该目录
             * 似乎以.结尾也会有问题
             */
            while (ret.EndsWith(" ")||ret.EndsWith("."))
                ret=ret.Substring(0, ret.Length-1);
            return ret;
        }
        private void CheckDownload()
        {
            var downloading_works = new List<String>();
            foreach (var work_pair in works)
                if(work_pair.Value.status ==Work.Status.Downloading)
                {
                    var id = work_pair.Key;
                    var work = work_pair.Value;
                    var dest_dir = "";                    
                    var mid_dir ="";
                    {
                        String parent_dir = null;
                        foreach (var pair in RootDirs)//依次根据条件决定下载到哪个目录
                            if (pair.Key(work))
                            {
                                parent_dir = pair.Value;
                                break;
                            }
                        if (parent_dir is null)
                            throw (new Exception("Fatal,Invalid RootDir"));
                        foreach (var d in Directory.GetFileSystemEntries(parent_dir, work.RJ + "*"))//如果已经存在则用存在的，否则创建一个
                            dest_dir = d;
                        if (dest_dir == "")
                            dest_dir = parent_dir + "/" + work.title;//title包含了RJ号
                        mid_dir = parent_dir + "/Tmp";
                    }

                    var src_dir = TmpDir + "/" + work.title;
                    bool done = true;
                    foreach (var file in work.files)
                    {
                        var src_path = src_dir +"/"+ file.subdir + "/" + file.tmp_name;
                        if (!File.Exists(src_path))
                            done = false;
                    }
                    if (done)
                    {
                        try
                        {
                            Thread.Sleep(5000);//略微等待，防止文件正在写入
                                               //Directory没有copy，Move不能跨卷移动
                                               //先拷贝到同卷的中转目录，防止中途失败导致文件不全
                            if (Directory.Exists(mid_dir))//清空中转目录防止带有多余的文件
                                Directory.Delete(mid_dir, true);
                            Directory.CreateDirectory(mid_dir);
                            foreach (var file in work.files)
                            {
                                var dir = mid_dir + "/" + file.subdir;
                                if (!Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);
                                File.Copy(src_dir + "/" + file.subdir + "/" + file.tmp_name, dir + "/" + file.name, true);
                            }
                            //清空目的目录防止带有多余的文件
                            if (Directory.Exists(dest_dir))
                                Directory.Delete(dest_dir, true);
                            Thread.Sleep(5000);//略微等待，防止文件正在写入
                            Directory.Move(mid_dir, dest_dir);
                            Directory.Delete(src_dir, true);
                            //清空替换目录
                            foreach (var dir in work.alter_dir)
                            {
                                Directory.Delete(dir, true);
                                Console.WriteLine("Remove {0}", dir);
                            }
                            work.status = Work.Status.Done;
                            work.alter_dir.Clear();
                            work.files.Clear();
                            Console.WriteLine(String.Format("Download {0} Done", work.RJ));
                        }
                        catch (Exception ex)
                        {
                            //视作失败重来
                            Console.WriteLine("Can't Rename Finished Work " + id+":"+ex.Message);
                            work.fail_ct = 0;
                            work.status = Work.Status.Waiting;
                            work.files.Clear();
                        }
                    }
                    else
                    {
                        work.fail_ct++;
                        if (work.fail_ct > 2 * (1000 * 60 * 60*24 / download_interval))//两天没下载完视作失败
                        {
                            work.fail_ct = 0;
                            work.status = Work.Status.Waiting;
                            work.files.Clear();
                        }
                        else
                            downloading_works.Add(work.RJ);
                    }
                }
            Console.Write(String.Format("Downloading Check {0} ", downloading_works.Count));
            //foreach (var work in downloading_works)
                //Console.Write(work+" ");
            Console.WriteLine();
        }
        private async Task Download(int limit,int max_concurrency)
        {
            var eliminated = await GetEliminatedWorks();
            var alter =GetAlterWorks();
            Dictionary<int, Work> _works = new Dictionary<int, Work>();
            int ct = 0;
            int downloading_ct = works.Count(ele => ele.Value.status == Work.Status.Downloading);
            foreach (var pair in works)
                if(pair.Value.status == Work.Status.Waiting)
                {
                    bool need_download=false;
                    int id=pair.Key;
                    var work = pair.Value;
                    if (alter.ContainsKey(id))
                    {
                        need_download = true;
                        work.alter_dir = alter[id];
                    }
                    else if (!eliminated.Contains(id))
                        need_download = true;
                    else if(test_id==id)//测试模式
                        need_download=true;
                    if (!need_download)
                    {
                        work.status=Work.Status.Done;
                        continue;
                    }
                    var tracks_str = await Get(String.Format("https://api.asmr.one/api/tracks/{0}", id));
                    //网络错误和其它原因(例如网站上没有任何文件时会返回403:No Tracks)都会导致请求不成功，考虑到现在网络较为稳定，不作区分统统标记为Done，不重新尝试
                    if (tracks_str is null || tracks_str=="")
                    {
                        Console.WriteLine("Can't Get Track_1 " + work.RJ);
                        work.status = Work.Status.Done;
                        continue;
                    }
                    bool get_track_success = true;
                    foreach (var track in (JArray)JsonConvert.DeserializeObject(tracks_str))
                        get_track_success&=await ParseTracks(work, "", track.ToObject<JObject>());
                    if (work.files.Count == 0||!get_track_success)//未能正常获取所有文件的跳过
                    {
                        Console.WriteLine("Can't Get Track_2 "+work.RJ);
                        work.status = Work.Status.Done;
                        continue;
                    }
                    foreach (var file in work.files)
                    {
                        var dir = TmpDir + "/" + work.title + (file.subdir==""?"":"/" + file.subdir);
                        if (!Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        /*
                         * 使用随机生成的文件名下载
                         * 由于迷之原因，SendLinkToIDM时文件名中的一些字符(例如"母"/"食")会被替换成其它东西，Chrome插件则可以正确下载包含这些字符的文件
                         * 可能是编码问题，尚不清楚如何解决，通过重命名绕过
                        */
                        lock (tasks)
                            tasks.Enqueue(new IDMTask { url = file.url, dir = dir, name = file.tmp_name });
                    }
                    work.status = Work.Status.Downloading;
                    ct++;
                    if (ct >= limit)
                        break;
                    if (ct+downloading_ct >= max_concurrency)
                        break;
                }
            {
                int process_ct = 0, wait_ct = 0, done_ct = 0;
                foreach (var pair in works)
                    if (pair.Value.status == Work.Status.Downloading)
                        process_ct++;
                    else if(pair.Value.status == Work.Status.Waiting)
                        wait_ct++;
                    else if(pair.Value.status == Work.Status.Done)
                        done_ct++;
                Console.WriteLine("{0} Waiting/{1} Downloading/{2} Ready",wait_ct,process_ct,done_ct);
            }
        }
        private async Task<RequestResult> CheckURL(string url,bool is_audio)
        {
            //DLSite的文件是分段压缩的，此处不是，所以有单个文件会超过2G
            if (url == "" || url is null)
                return RequestResult.Bad;
            try
            {
                /*
                 如果不指定HttpCompletionOption.ResponseHeadersRead，即使是Head请求也会分配缓冲区(但是不会下载)
                 分配的缓冲区占用内存在任务管理器中显示为"提交"，在VS调试工具中显示为"专用"
                 这些内存不会随着response析构/httpclient.Dispose/GC.Collect而释放，Why??
                */
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                    using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        if (response.IsSuccessStatusCode)
                        {
                            if (response.Content.Headers.Contains("Content-Length"))
                            {
                                //单位:byte，排除小于200KB的音频，以避免坑爹的情况，如RJ066580
                                var len = Int64.Parse(response.Content.Headers.GetValues("Content-Length").First());
                                if (len == 0)
                                    return RequestResult.Bad;
                                else if (is_audio && len < 1024 * 200)
                                    return RequestResult.Skip;
                                else
                                    return RequestResult.Good;
                            }
                            else//有的content类型不带length
                                return RequestResult.Good;
                        }
            }
            catch (Exception ex)
            {
                //请求失败什么都不做
                Console.WriteLine("Check URL Bad:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            return RequestResult.Bad;
        }
        private bool IsAudio(String title)
        {
            var tmp = title.ToLower();
            foreach (var suffix in suffixs)
                if (title.EndsWith(suffix))
                    return true;
            return false;
        }
        private async Task<bool> ParseTracks(Work work,String parent,JObject json)
        {
            if (json == null)
                return false;
            if (!json.ContainsKey("type"))
                return false;
            if (!json.ContainsKey("title"))
                return false;
            if (json.Value<String>("type")=="folder")
            {
                bool ret = true;
                //由于谜之原因，目录里会有非法字符，如RJ047447
                //部分作品自带乱码，如RJ066580
                //可能有多于1级目录，此时拆开分别检查
                var dir = parent;
                foreach (var sub_dir in json.Value<String>("title").Split(new char[]{ '\\','/'}))
                {
                    var tmp= FileNameCheck(sub_dir);
                    if (tmp != "")
                        dir += "/" + tmp;
                }
                if (json.ContainsKey("children"))
                    foreach (var item in json.Value<JArray>("children"))
                        ret&=await ParseTracks(work, dir, item.ToObject<JObject>());
                return ret;
            }
            else if (json.ContainsKey("mediaDownloadUrl")|| json.ContainsKey("mediaStreamUrl"))
            {
                var url_download = json.Value<String>("mediaDownloadUrl");
                //stream_url要加上token
                var url_stream = json.Value<String>("mediaStreamUrl")+"?token="+ bearer_token;
                var title = FileNameCheck(json.Value<String>("title"));
                bool is_audio = IsAudio(title);
                var ret_download = await CheckURL(url_download, is_audio);
                var ret_stream = await CheckURL(url_stream, is_audio);
                String url = null;
                //个别文件的downloadurl无效，而streamurl有效，如RJ061291
                //由于谜之原因，部分文件大小为0，这些文件IDM无法完成下载，直接排除
                //请求失败可能是短暂的网络错误，此时也视作无效
                if (ret_download == RequestResult.Good)//若其中一个url确定生效则使用它；url_download总是优先于url_stream
                    url = url_download;
                else if (ret_stream == RequestResult.Good)
                    url = url_stream;
                else if (ret_download == RequestResult.Skip || ret_stream == RequestResult.Skip)//一个为skip一个为bad/skip则不下载
                    url = null;
                else if(is_audio)//音频文件都为bad则失败，否则跳过不下载
                    return false;
                //如果不替换则IDM会自动替换非法字符为-
                if(!(url is null))
                    work.files.Add(new Work.File_(title, parent, url));
                return true;
            }
            return false;
        }
        private Dictionary<int,List<String>> GetAlterWorks()
        {
            var ret=new Dictionary<int, List<String>>();
            var regex = new Regex("[RVBJ]{2}([0-9]{3,8})");
            foreach (var parent_dir in AlterDirs)
            {
                var di=new DirectoryInfo(parent_dir);
                foreach (DirectoryInfo NextFolder in di.GetDirectories())
                {
                    int id = 0;
                    var matches = regex.Matches(NextFolder.Name);
                    if (matches.Count > 0)
                        id = Int32.Parse(matches[0].Groups[1].Value);
                    else
                        continue;
                    if(ret.ContainsKey(id))
                        ret[id].Append(NextFolder.FullName);
                    else
                        ret.Add(id, new List<String> { NextFolder.FullName });
                }
            }
            return ret;
        }
        private async Task FetchWorkList()
        {
            try
            {
                Console.WriteLine("Start Fetch Work List");
                //seed不知道是什么,subtitle=1是带字幕，subtitle=0包含subtitle=1,page从1开始而非0
                String base_url = "https://api.asmr.one/api/works?order=id&sort=asc&page={0}&seed=35&subtitle=0";
                var first_page = await GetJson(String.Format(base_url, 1));
                var total_count = first_page.Value<JObject>("pagination").Value<Int32>("totalCount");
                var page_size = first_page.Value<JObject>("pagination").Value<Int32>("pageSize");
                for(int p=0;p*page_size<total_count;p++)//变量p从0开始,页数为p+1
                {
                    var page = await GetJson(String.Format(base_url, p+1));
                    if(page is null)
                    {
                        Console.WriteLine("Fail Fetch Page {0}", page);
                        continue;
                    }
                    var list=page.Value<JArray>("works");
                    foreach(var item in list)
                    {
                        var work_object=item.ToObject<JObject>();
                        var id = work_object.Value<Int32>("id");
                        if(!works.ContainsKey(id))//此处只获取了基本信息，无需更新
                        {
                            var work = new Work();
                            work.r = work_object.Value<Boolean>("nsfw");
                            //id即是RJ号，5位的补到6位，7位的补到8位；使用该网站给出的title，title可能为空如RJ087362
                            if (id < 1000000) //6位或更低
                                work.RJ = String.Format("RJ{0:D6}", id);
                            else if (id<100000000)//6~8位
                                work.RJ = String.Format("RJ{0:D8}", id);
                            else//8位以上(目前无)
                                work.RJ = String.Format("RJ{0:D10}", id);

                            work.group = work_object.Value<int>("circle_id");
                            work.title = String.Format("{0} {1}", work.RJ, work_object.Value<String>("title"));
                            work.title = FileNameCheck(work.title);
                            if(test_id>0)//测试模式,只下载特定作品
                            {
                                if(id==test_id)
                                {
                                    works.Add(id, work);
                                    Console.WriteLine("Page:{0}",p);
                                    return;
                                }
                            }
                            else
                                works.Add(id, work);
                        }
                    }
                    if(p%100==0)
                        Console.WriteLine("Fetching {0} page", p);
                    //防止请求过快
                    Thread.Sleep(300);
                }
                Console.WriteLine("Fetch Work List Done {0}/{1}",works.Count,total_count);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't Fetch Work List:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }
        public async Task<bool> Login()
        {
            JObject jdoc = null;
            jdoc = await PostJson("https://api.asmr.one/api/auth/me", "{\"name\": \"guest\", \"password\": \"guest\"}", Encoding.UTF8, "application/json");
            if(jdoc != null)
                if (jdoc.ContainsKey("token"))
                {
                    bearer_token = jdoc.Value<String>("token");
                    //可以再get验证一下，但是没必要
                    httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",bearer_token);
                    return true;
                }
            return false;
        }
        private async Task<HashSet<int>> GetEliminatedWorks()
        {
            var ret = new HashSet<int>();
            var response = await Get(query_addr);
            foreach (var id in response.Split(' '))
                ret.Add(Int32.Parse(id.Substring(2,6)));
            return ret;
        }
        private async Task<JObject> GetJson(String addr)
        {
            var result=await Get(addr);
            if(result!=null)
                return (JObject)JsonConvert.DeserializeObject(result);
            return null;
        }
        private async Task<JObject> PostJson(String addr, String data,Encoding encoding,String type)
        {
            var result = await Post(addr,data,encoding,type);
            if (result != null)
                return (JObject)JsonConvert.DeserializeObject(result);
            return null;
        }
        private async Task<String> Get(String addr)
        {
            for (int i = 5; i > 0; --i)
                try
                {
                    using (HttpResponseMessage response = await httpClient.GetAsync(addr))
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
        private async Task<String> Post(String addr,String data, Encoding encoding, String type)
        {
            for (int i = 5; i > 0; --i)
                try
                {
                    using (var content = new StringContent(data, encoding, type))
                        using (HttpResponseMessage response = await httpClient.PostAsync(addr, content))
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
