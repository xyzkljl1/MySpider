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
            }
            public string name;
            public string subdir;//相对目录
            public string url;
        }
        public Status status=Status.Waiting;
        public bool r=false;
        public String RJ = "";
        public String title = "";
        public List<String> alter_dir = new List<string>();//由于一些坑爹的原因，可能会有多个
        //subdir->url
        public List<File_> files= new List<File_>();
        public int fail_ct=0;
    }
    class Fetcher
    {
        private String RootDir = "G:/ASMR_Reliable";
        private String RootDirR = "G:/ASMR_ReliableR";
        //如果某作品处于以下目录，则删除它们并强制重新下载
        private List<String> AlterDirs = new List<string>{ "G:/ASMR_Unreliable", "G:/ASMR_UnreliableR" };
        private String TmpDir = "E:/Tmp/MySpider/ASMRONE";
        public String query_addr = "http://127.0.0.1:4567/?QueryInvalidDLSite";
        private ICIDMLinkTransmitter2 idm = new CIDMLinkTransmitter();
        private HttpClient httpClient;
        CookieContainer cookies_container = new CookieContainer();
        private DateTime LastFetchTime =DateTime.MinValue;
        private Dictionary<int, Work> works = new Dictionary<int, Work>();

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

                httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
                httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8");
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.82 Safari/537.36");
            }
        }
        public async Task Start()
        {
            try {
                int index = 0;
                if (!await Login())
                {
                    Console.WriteLine("Login Fail,Exiting...");
                    Thread.Sleep(100000);
                    return;
                }
                //清理临时目录
                Directory.Delete(TmpDir, true);
                Directory.CreateDirectory(TmpDir);
                while (true)
                {
                    if (index % (24 * 7 * 2 * 2) == 0)//每2周
                        await FetchWorkList();
                    //一次下载太多会有429 Too Many Requests
                    await Download(7);
                    CheckDownload();
                    Thread.Sleep(1000 * 30 * 60);//每半小时一次
                    index++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception:" + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
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
                    {
                        String parent_dir = work.r ? RootDirR : RootDir;
                        foreach (var d in Directory.GetFileSystemEntries(parent_dir, work.RJ + "*"))//如果已经存在则用存在的，否则创建一个
                            dest_dir = d;
                        if (dest_dir == "")
                            dest_dir = parent_dir + "/" + work.title;//title包含了RJ号
                    }
                    var src_dir = TmpDir + "/" + work.title;
                    bool done = true;
                    foreach (var file in work.files)
                    {
                        var src_path = src_dir +"/"+ file.subdir + "/" + file.name;
                        if (!File.Exists(src_path))
                            done = false;
                    }
                    if (done)
                    {
                        Thread.Sleep(3000);//略微等待，防止文件正在写入
                        Console.WriteLine(String.Format("Moving {0}", work.RJ));
                        //Directory没有copy，Move不能跨卷移动
                        foreach (var file in work.files)
                        {
                            var dir = dest_dir + "/" + file.subdir;
                            if (!Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            File.Copy(src_dir + "/" + file.subdir + "/" + file.name, dir + "/" + file.name,true);
                        }
                        Directory.Delete(src_dir, true);
                        foreach (var dir in work.alter_dir)
                        {
                            Directory.Delete(dir, true);
                            Console.WriteLine("Remove {0}", dir);
                        }
                        work.status = Work.Status.Done;
                        Console.WriteLine(String.Format("Download {0} Done", work.RJ));
                    }
                    else
                    {
                        work.fail_ct++;
                        if (work.fail_ct > 48 * 2)//两天没下载完视作失败
                        {
                            work.fail_ct = 0;
                            work.status = Work.Status.Waiting;
                        }
                        else
                            downloading_works.Add(work.RJ);
                    }
                }
            Console.Write(String.Format("Downloading Check {0} ", downloading_works.Count));
            foreach (var work in downloading_works)
                Console.Write(work+" ");
            Console.WriteLine();
        }
        private async Task Download(int limit)
        {
            var eliminated = await GetEliminatedWorks();
            var alter =GetAlterWorks();
            Dictionary<int, Work> _works = new Dictionary<int, Work>();
            int ct = 0;
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
                    if (!need_download)
                    {
                        work.status=Work.Status.Done;
                        continue;
                    }
                    var tracks= (JArray)JsonConvert.DeserializeObject(await Get(String.Format("https://api.asmr.one/api/tracks/{0}", id)));
                    foreach (var track in tracks)
                        ParseTracks(work, "", track.ToObject<JObject>());
                    foreach(var file in work.files)
                        try
                        {
                            var dir = TmpDir + "/" + work.title + "/" + file.subdir;
                            if (!Directory.Exists(dir))
                                Directory.CreateDirectory(dir);
                            idm.SendLinkToIDM(file.url, "", "", "", "", "",dir , file.name, 0x01 /*| 0x02*/);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                            Console.WriteLine(ex.StackTrace);
                            Console.WriteLine(TmpDir + "/" + work.title + "/" + file.subdir);
                            Console.WriteLine(work.RJ);
                        }
                    work.status = Work.Status.Downloading;
                    ct++;
                    if (ct >= limit)
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
        private void ParseTracks(Work work,String parent,JObject json)
        {
            if (json == null)
                return;
            if (!json.ContainsKey("type"))
                return;
            if (!json.ContainsKey("title"))
                return;
            if (json.Value<String>("type")=="folder")
            {
                //由于谜之原因，目录里会有非法字符，如RJ047447
                //可能有多于1级目录，因此不替换/
                //部分作品自带乱码，如RJ066580
                var dir = parent + "/" + Regex.Replace(json.Value<String>("title"), "[\\\\?*<>:\"|.]", "_");
                dir=Regex.Replace(dir, "[\\\\?*<>:\"|.]", "_");
                if (json.ContainsKey("children"))
                    foreach (var item in json.Value<JArray>("children"))
                        ParseTracks(work, dir, item.ToObject<JObject>());
            }
            else if(json.ContainsKey("mediaDownloadUrl"))
                work.files.Add(new Work.File_(json.Value<String>("title"), parent, json.Value<String>("mediaDownloadUrl")));
            else if(json.ContainsKey("mediaStreamUrl"))
                work.files.Add(new Work.File_(json.Value<String>("title"), parent, json.Value<String>("mediaStreamUrl")));
        }
        private Dictionary<int,List<String>> GetAlterWorks()
        {
            var ret=new Dictionary<int, List<String>>();
            foreach (var parent_dir in AlterDirs)
            {
                var di=new DirectoryInfo(parent_dir);
                foreach (DirectoryInfo NextFolder in di.GetDirectories())
                {
                    int id = 0;
                    if(!Int32.TryParse(NextFolder.Name.Substring(2, 6),out id))//第3~8个字符是RJ号的数字部分
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
                for(int p=0;p*page_size<total_count;p++)
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
                            //id即是RJ号，前面补0；信任该网站给出的title
                            work.RJ = String.Format("RJ{0:D6}", id);
                            work.title = String.Format("{0} {1}", work.RJ, work_object.Value<String>("title"));
                            work.title = Regex.Replace(work.title, "[/\\\\?*<>:\"|.]", "_");
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

            var jdoc = await PostJson("https://api.asmr.one/api/auth/me", new StringContent("{\"name\": \"guest\", \"password\": \"guest\"}", Encoding.UTF8, "application/json"));
            if(jdoc != null)
                if (jdoc.ContainsKey("token"))
                {
                    var bearer_token = jdoc.Value<String>("token");
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
        private async Task<JObject> PostJson(String addr, HttpContent data)
        {
            var result = await Post(addr,data);
            if (result != null)
                return (JObject)JsonConvert.DeserializeObject(result);
            return null;
        }
        private async Task<String> Get(String addr)
        {
            for (int i = 12; i > 0; --i)
                try
                {
                    HttpResponseMessage response = await httpClient.GetAsync(addr);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine(await response.Content.ReadAsStringAsync());
                        throw new Exception("HTTP Not Success");
                    }
                    return await response.Content.ReadAsStringAsync();
                }
                catch (Exception e)
                {
                    string msg = e.Message;//e.InnerException.InnerException.Message;
                    Console.WriteLine("Request Fail :" + msg);
                    Thread.Sleep(20);
                }
            return null;
        }
        private async Task<String> Post(String addr,HttpContent data)
        {
            for (int i = 12; i > 0; --i)
                try
                {
                    HttpResponseMessage response = await httpClient.PostAsync(addr,data);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("HTTP Not Success");
                    return await response.Content.ReadAsStringAsync();
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
