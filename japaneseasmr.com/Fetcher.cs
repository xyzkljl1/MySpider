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
using MSScriptControl;
using IDManLib;
using System.Threading;

namespace japaneseasmr.com
{
    class Work
    {
        public bool r=false;
        public List<String> work_pages=new List<string>();
        public List<String> download_pages=new List<string>();
        public Dictionary<String,HashSet<String>> outter_pages=new Dictionary<string, HashSet<string>>();
        public int fail_ct=0;
    }
    class Fetcher
    {
        private String RootDir = "G:/ASMR_Unreliable";
        private String RootDirR = "G:/ASMR_UnreliableR";
        private String TmpDir = "E:/Tmp/MySpider";
        public String query_addr = "http://127.0.0.1:4567/?QueryInvalidDLSite";
        private ICIDMLinkTransmitter2 idm = new CIDMLinkTransmitter();
        private HttpClient httpClient;
        private HttpClient httpClient_redirect;
        CookieContainer cookies_container = new CookieContainer();
        private DateTime LastFetchTime =DateTime.MinValue;
        private Dictionary<String, Work> works = new Dictionary<string, Work>();
        private Dictionary<String, Work> downloading_works = new Dictionary<string, Work>();


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
                httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3");
                httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8");
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/76.0.3809.100 Safari/537.36");
            }
            {
                var handler = new HttpClientHandler()
                {
                    MaxConnectionsPerServer = 256,
                    UseCookies = true,
                    AllowAutoRedirect=false,
                    CookieContainer = cookies_container,
                    Proxy = new WebProxy("127.0.0.1:1196", false)
                };
                handler.ServerCertificateCustomValidationCallback = delegate { return true; };
                httpClient_redirect = new HttpClient(handler);
                httpClient_redirect.Timeout = new TimeSpan(0, 0, 35);
                httpClient_redirect.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3");
                httpClient_redirect.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8");
                httpClient_redirect.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/76.0.3809.100 Safari/537.36");
            }
        }
        public async Task Start()
        {
            int index=0;
            while(true)
            {
                if(index%(24*14)==0)//每周
                {
                    int tmp = works.Count;
                    await FetchSearchPage();
                    Console.WriteLine(String.Format("Fetch Search Page {0} => {1}",tmp,works.Count));
                }
                await Download(25);
                CheckDownload();
                Thread.Sleep(1000 *60*60);//每小时一次
                index++;
            }
        }
        private void CheckDownload()
        {
            var done_tasks = new List<String>();
            foreach(var work_pair in downloading_works)
            {
                var id = work_pair.Key;
                var work = work_pair.Value;
                var dest_dir = "";
                {
                    String parent_dir = work.r ? RootDirR : RootDir;
                    foreach (var d in Directory.GetFileSystemEntries(parent_dir, id + "*"))//如果已经存在则用存在的，否则创建一个
                        dest_dir = d;
                    if (dest_dir == "")
                        dest_dir = parent_dir + "/" + id;
                }
                var src_dir = TmpDir + "/" + id;
                bool done = true;
                foreach (var outter_pair in work.outter_pages)
                {
                    var file_name = outter_pair.Key + ".mp3";
                    var src_path = src_dir + "/" + file_name;
                    if (!File.Exists(src_path))
                        done = false;
                }
                if(done)
                {
                    Thread.Sleep(3000);//略微等待，防止文件正在写入
                    if (!Directory.Exists(dest_dir))
                        Directory.CreateDirectory(dest_dir);
                    done_tasks.Add(id);
                    foreach (var outter_pair in work.outter_pages)
                    {
                        var file_name = outter_pair.Key + ".mp3";
                        var src_path = src_dir + "/" + file_name;
                        File.Copy(src_path, dest_dir+"/"+file_name,true);
                    }
                    Directory.Delete(src_dir,true);
                    Console.WriteLine(String.Format("Download {0} Done",id));
                }
                else
                {
                    work.fail_ct++;
                    if(work.fail_ct>48)//两天没下载完视作失败
                    {
                        work.fail_ct = 0;
                        works.Add(id,work);
                    }
                }
            }
            foreach (var id in done_tasks)
                downloading_works.Remove(id);
            Console.Write(String.Format("Downloading Check {0} ",downloading_works.Count));
            foreach (var work in downloading_works)
                Console.Write(work.Key+" ");
            Console.WriteLine();
        }
        private async Task Download(int ct)
        {
            try
            {
                var eliminated = await GetEliminatedWorks();
                var processed_works =works.Take(ct).ToDictionary(kv=>kv.Key,kv=>kv.Value);
                foreach (var work_pair in processed_works)
                {
                    var id = work_pair.Key;
                    var work = work_pair.Value;
                    bool work_success = true;
                    works.Remove(id);
                    if (eliminated.Contains(id))
                        continue;
                    //获取下载页面
                    foreach (var url in work.work_pages)
                    {
                        var ret = await GetDownloadPagesFromPage(url);
                        if(ret is null)//因网络错误未获取到页面，无论是否获取到其它页面都视作失败
                        {
                            work_success = false;
                            break;
                        }
                        work.r = ret.Value.Key;
                        work.download_pages.AddRange(ret.Value.Value);
                    }
                    //获取外链页面，有anofiles和zipppyshare两种
                    //一个下载页面会有很多work的外链地址，为了保证获得更完整更新的链接，无视多余的链接
                    var regex = new Regex("#.*");
                    foreach (var url in work.download_pages)
                    {
                        var name = url.Substring(url.LastIndexOf("=") + 1);//文件名
                        var outter_page = await RequestRedirect(url);
                        if (work.outter_pages.ContainsKey(name))
                            work.outter_pages[name].Add(outter_page);
                        else
                            work.outter_pages.Add(name,new HashSet<string> { outter_page });
                    }
                    //获取真实链接
                    if (work.outter_pages.Count == 0)//无论因何种原因导致没有下载链接，都视作失败
                        work_success = false;
                    foreach (var outter_pair in work.outter_pages)
                    {
                        var file_name = outter_pair.Key+".mp3";
                        var dir = TmpDir + "/" + id;//下载到临时目录
                        if (!Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        else
                            foreach (var f in new DirectoryInfo(dir).GetFiles("*.html"))//删除失效而被重定向到网页的文件
                                f.Delete();
                        if (File.Exists(dir+"/"+file_name))
                            continue;
                        bool file_success = false;
                        foreach (var page in outter_pair.Value)
                        {
                            var url = "";
                            var cookie = "";
                            if (Regex.IsMatch(page, "https://www.*.zippyshare.com/.*"))
                            {
                                var ret = await GetRealURLFromZippyshare(page);
                                url = ret.Key;
                                cookie = ret.Value;
                            }
                            else if (Regex.IsMatch(page, "https://anonfiles.com/.*"))
                            {
                                var ret = await GetRealURLFromAnofiles(page);
                                url = ret.Key;
                                cookie = ret.Value;
                            }
                            else
                                Console.WriteLine("Unknown Outter Site");
                            if (url == "")
                                continue;
                            if (url.EndsWith("html"))
                                continue;
                            idm.SendLinkToIDM(url, "", cookie, "", "", "", dir, file_name, 0x01 /*| 0x02*/);
                            file_success = true;
                            break;
                        }
                        if (!file_success)
                        {
                            work_success = false;
                            continue;
                        }
                    }
                    if (!work_success)
                    {
                        Console.WriteLine("Fail On {0}", id);
                        work.fail_ct++;
                        if (work.fail_ct < 15)
                            works.Add(id,work);//再试一次
                    }
                    else if(!downloading_works.ContainsKey(id))
                    {
                        work.fail_ct = 0;
                        downloading_works.Add(id, work);
                    }
                }
                Console.WriteLine(String.Format("Process Download Queue {0}", works.Count));
            }
            catch (Exception e)
            {
                string msg = e.Message;//e.InnerException.InnerException.Message;
                Console.Error.WriteLine("Fail :" + msg);
            }
        }
        public async Task FetchSearchPage()
        {
            int pageCount = GetPageCountFromSearchPage(await RequestHtml("https://japaneseasmr.com/?orderby=date&order=dsc"));//日期降序
            for (int pi = 1; pi <= pageCount; pi++)
            {
                try
                {
                    var doc = await RequestHtml(String.Format("https://japaneseasmr.com/page/{0}/?orderby=date&order=dsc", pi));
                    var regex = new Regex("[RVBJ]{2}[0-9]{3,6}");
                    if (doc != null)
                        foreach (var node in doc.DocumentNode.SelectNodes("//div[@class='entry-preview-wrapper clearfix']"))
                        {
                            if (node is null)
                                continue;
                            var time = DateTime.Parse(node.SelectSingleNode("//time").Attributes["datetime"].Value);
                            var title_node = node.SelectSingleNode("h2[@class='entry-title']");
                            var url = title_node.SelectSingleNode("a").Attributes["href"].Value;
                            var id = "";
                            foreach (var p_tag in node.SelectNodes("p"))
                                foreach (var m in regex.Matches(p_tag.InnerText))
                                    id = m.ToString();
                            if (id != "" && url != "")
                            {
                                if (!works.ContainsKey(id))
                                    works.Add(id, new Work());
                                works[id].work_pages.Add(url);
                                if(time<LastFetchTime)//只获取上次更新之后更新的work
                                {
                                    LastFetchTime = DateTime.Now.AddDays(-1);//偏移一天防止漏掉
                                    return;
                                }
                            }
                        }
                    else
                    {
                        Console.Error.WriteLine(String.Format("Cant Fetch Page {0}", pi));
                        return;//中断,不更新LastFetchTime
                    }
                }
                catch (Exception e)
                {
                    string msg = e.Message;//e.InnerException.InnerException.Message;
                    Console.Error.WriteLine(String.Format("Cant Fetch Page {0}:{1}", pi,msg));
                }
            }
            //初次运行，所有work都在LastFetchTime之后
            LastFetchTime = DateTime.Now.AddDays(-1);//偏移一天防止漏掉
            return;
        }
        private async Task<HashSet<String>> GetEliminatedWorks()
        {
            var ret = new HashSet<String>();
            var response=await RequestPage(query_addr);
            foreach (var id in response.Split(' '))
                ret.Add(id);
            return ret;
        }
        private async Task<String> RequestPage(String addr)
        {
            for(int i=12;i>0;--i)
                try
                {
                    HttpResponseMessage response = await httpClient.GetAsync(addr);
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
        private async Task<String> RequestRedirect(String addr)
        {
            for (int i = 12; i > 0; --i)
                try
                {
                    HttpResponseMessage response = await httpClient_redirect.GetAsync(addr);
                    if(response.Headers.Location is null)
                        throw new Exception("HTTP Not Success");
                    return response.Headers.Location.ToString();
                }
                catch (Exception e)
                {
                    string msg = e.Message;//e.InnerException.InnerException.Message;
                    Console.WriteLine("Request Fail :" + msg);
                    Thread.Sleep(20);
                }
            return null;
        }
        private async Task<HtmlDocument> RequestHtml(string url)
        {
            var doc = new HtmlDocument();
            var result = await RequestPage(url);
            if (result is null)
                return null;
            doc.LoadHtml(result);
            return doc;
        }
        private int GetPageCountFromSearchPage(HtmlDocument doc)
        {
            int ret = 0;
            if (doc != null)
                foreach (var node in doc.DocumentNode.SelectNodes("//a[@class='page-numbers']"))
                    ret= Math.Max(ret, int.Parse(node.InnerText));
            return ret;
        }
        private async Task<KeyValuePair<bool,List<String>>?> GetDownloadPagesFromPage(String _url)
        {
            var doc = await RequestHtml(_url);
            bool r18 =true;
            var urls = new List<String>();
            if (doc != null)
            {
                foreach (var node in doc.DocumentNode.SelectNodes("//div[@class='download_links']"))
                    foreach (var p in node.SelectNodes("p[@id='downloadlink']"))
                        urls.Add(p.SelectSingleNode("a").Attributes["href"].Value);
                foreach (var node in doc.DocumentNode.SelectSingleNode("//span[@class='post-meta-span post-meta-span-category']").SelectNodes("a"))
                    if (node.InnerText == "SFW" || node.InnerText == "NSFW (R-15)")
                        r18 = false;
                return new KeyValuePair<bool, List<string>>(r18, urls);
            }
            return null;
        }
        private async Task<KeyValuePair<String,String>> GetRealURLFromZippyshare(String _url)
        {
            var doc = await RequestHtml(_url);
            if(doc is null)
            {
                doc = await RequestHtml(_url);
                if (doc is null)
                {
                    doc = await RequestHtml(_url);//由于蜜汁原因，有的网页明明能访问还是会走到这一步，调试时强行重来一遍又能访问了
                    return new KeyValuePair<String, String>("","");
                }
            }
            var btn_node = doc.DocumentNode.SelectSingleNode("//a[@id='dlbutton']");
            if (btn_node is null)
                return new KeyValuePair<string, string>("", "");
            var parent_node = btn_node.ParentNode;
            var script = parent_node.SelectSingleNode("script").InnerText;
            //提取脚本中的定义和赋值
            /*
            var n = 616401%2;
            var b = 616401%3;
            var z = 616404;
            document.getElementById('dlbutton').href = "/d/DhfQti7M/"+(n + b + z - 3)+"/RJ362225.mp3";
            if (document.getElementById('fimage')) {
            document.getElementById('fimage').href = "/i/DhfQti7M/"+(n + b + z - 3)+"/RJ362225.mp3";
            }
             */
            var express = "";
            foreach (var m in Regex.Matches(script, "var.*?[a-z0-9_]+.*?=.*?;"))
                express += m.ToString();
            express += Regex.Match(script, "document.getElementById\\('dlbutton'\\).href.*?=(.*?);").Groups[1].Value;
            ScriptControl scriptControl = new MSScriptControl.ScriptControl();
            scriptControl.UseSafeSubset = true;
            scriptControl.Language = "JScript";
            string path = scriptControl.Eval(express).ToString();
            var real_url=Regex.Match(_url, "https://.*?/").ToString()+path.Substring(1);
            var cookies = "";
            foreach (var cookie in cookies_container.GetCookies(new Uri(real_url)).Cast<Cookie>())
                cookies += cookie.Name + "=" + cookie.Value + "; ";
            return new KeyValuePair<String, String> ( real_url, cookies );
        }
        private async Task<KeyValuePair<String, String>> GetRealURLFromAnofiles(String _url)
        {
            var doc = await RequestHtml(_url);
            if (doc is null)
                return new KeyValuePair<String, String>("", "");
            var node = doc.DocumentNode.SelectSingleNode("//a[@id='download-url']");           
            return new KeyValuePair<String, String>(node.Attributes["href"].Value, "");
        }
    }
}
