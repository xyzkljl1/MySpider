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

namespace japaneseasmr.com
{
    class Fetcher
    {
        private ICIDMLinkTransmitter2 idm = new CIDMLinkTransmitter();
        private HttpClient httpClient;
        CookieContainer cookies_container = new CookieContainer();
        private String RootDir="";
        public Fetcher(String dir) {
            RootDir = dir;
            System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler()
            {
                MaxConnectionsPerServer = 256,
                UseCookies = true,
                CookieContainer = cookies_container,
                Proxy=new WebProxy("127.0.0.1:1196", false)
            };
            handler.ServerCertificateCustomValidationCallback = delegate { return true; };
            httpClient = new HttpClient(handler);
            httpClient.Timeout = new TimeSpan(0, 0, 35);
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3");
            httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8");
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/76.0.3809.100 Safari/537.36");
        }
        public async Task Start()
        {
            try
            {
                var download_tasks = new Dictionary<String, List<String>>();//file path->outter page的映射
                if (false)
                {
                    int pageCount = GetPageCountFromSearchPage(await RequestHtml("https://japaneseasmr.com/?orderby=date&order=asc"));
                    var work_pages = new Dictionary<String, String>();
                    {
                        var queue = new TaskQueue<Dictionary<String, String>>(100);
                        for (int p = 1; p <= pageCount; p++)
                            await queue.Add(GetPagesFromSearchPage(String.Format("https://japaneseasmr.com/page/{0}/?orderby=date&order=asc", p)));
                        await queue.Done();
                        foreach (var task in queue.done_task_list)
                            foreach (var pair in task.Result)
                                if (!work_pages.ContainsKey(pair.Key))
                                    work_pages.Add(pair.Key, pair.Value);
                    }
                    var download_pages = new Dictionary<String, KeyValuePair<bool, List<String>>>();
                    {
                        var queue = new TaskQueue<KeyValuePair<String, KeyValuePair<bool, List<String>>>>(100);
                        foreach (var item in work_pages)
                            await queue.Add(GetDownloadPagesFromPage(item.Key, item.Value));
                        await queue.Done();
                        foreach (var task in queue.done_task_list)
                            if (!download_pages.ContainsKey(task.Result.Key))
                                download_pages.Add(task.Result.Key, task.Result.Value);
                    }
                    //有anofiles和zipppyshare两种，文件名重复的只下载一个
                    var outter_pages = new Dictionary<String, HashSet<String>>();//file name->outter page的映射
                    foreach (var item in download_pages)
                    {
                        var regex = new Regex("#.*");
                        foreach (var url in item.Value.Value)
                        {
                            var name = url.Substring(url.LastIndexOf("#") + 1);
                            List<String> outter_url = null;
                            if (!outter_pages.ContainsKey(name))
                            {
                                var real_url = url.Insert(url.LastIndexOf("#"), "/");//省去一次重定向
                                foreach (var pair in await GetOutterLinksFromDownloadPage(real_url))
                                    if (!outter_pages.ContainsKey(pair.Key))
                                        outter_pages.Add(pair.Key, pair.Value);
                                    else
                                        foreach (var v in pair.Value)
                                            outter_pages[pair.Key].Add(v);
                            }
                            if (outter_pages.ContainsKey(name))
                                outter_url = outter_pages[name].ToList<String>();
                            else//有的就是没有文件的
                                continue;
                            var dir = RootDir + (item.Value.Key ? "_R18/" : "/") + item.Key;
                            var path = dir + "/" + name + ".mp3";
                            if (!download_tasks.ContainsKey(path))//一个文件可能给出多个链接
                                download_tasks.Add(path, outter_url);
                            else
                                download_tasks[path].AddRange(outter_url);
                        }
                    }
                    var tempo = "";
                    foreach (var item in download_tasks)
                        foreach (var page in item.Value)
                            tempo += item.Key + "\n" + page + "\n";
                    File.WriteAllText(@"E:\MyWebsiteHelper\MySpider\1.txt", tempo, Encoding.UTF8);
                }
                else
                {
                    var array=File.ReadAllLines(@"E:\MyWebsiteHelper\MySpider\1.txt");
                    for(int i=0;i+1<array.Count();i+=2)
                    {
                        if (download_tasks.ContainsKey(array[i]))
                            download_tasks[array[i]].Add(array[i + 1]);
                        else
                            download_tasks[array[i]] = new List<String> { array[i + 1] };
                    }
                }
                //anonfiles的直接下载,zippyshare还需要找到链接
                foreach (var item in download_tasks)
                {
                    bool success = false;
                    foreach(var page in item.Value)
                    {
                        var url = "";
                        var cookie = "";
                        if (Regex.IsMatch(page, "https://www.*.zippyshare.com/.*"))
                        {
                            var ret=await GetRealURLFromZippyshare(page);
                            url = ret.Key;
                            cookie = ret.Value;
                        }
                        else if (Regex.IsMatch(page, "https://anonfiles.com/.*"))
                        {
                            var ret =await GetRealURLFromAnofiles(page);
                            url = ret.Key;
                            cookie = ret.Value;
                        }
                        else
                            Console.WriteLine("Unknown Outter Site");
                        if (url == "")
                            continue;
                        var finfo = new FileInfo(item.Key);
                        var dir=finfo.DirectoryName;
                        var file_name = finfo.Name;
                        Directory.CreateDirectory(dir);
                        idm.SendLinkToIDM(url, "", cookie, "", "", "", dir, file_name, 0x01 | 0x02);
                        success = true;
                        break;
                    }
                    if(!success)
                    {
                        Console.WriteLine("Fail");
                    }
                }

            }
            catch (Exception e)
            {
                string msg = e.Message;//e.InnerException.InnerException.Message;
                Console.WriteLine("Fail :" + msg);
            }
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
        private async Task<Dictionary<String,String>> GetPagesFromSearchPage(String _url)
        {
            try
            {
                var doc = await RequestHtml(_url);
                var regex = new Regex("[RVBJ]{2}[0-9]{3,6}");
                var ret = new Dictionary<String, String>();
                if (doc != null)
                    foreach (var node in doc.DocumentNode.SelectNodes("//div[@class='entry-preview-wrapper clearfix']"))
                    {
                        var title_node = node.SelectSingleNode("h2[@class='entry-title']");
                        var url = title_node.SelectSingleNode("a").Attributes["href"].Value;
                        var id = "";
                        foreach (var p in node.SelectNodes("p"))
                            foreach (var m in regex.Matches(p.InnerText))
                                id = m.ToString();
                        if (id != "" && url != "")
                            if (!ret.ContainsKey(id))//同一个rj号可能有多个页面，暂不处理
                                ret.Add(id, url);
                    }
                return ret;
            }
            catch (Exception e)
            {
                string msg = e.Message;//e.InnerException.InnerException.Message;
                Console.WriteLine("Fail :" + msg);
            }
            return null;
        }
        private async Task<KeyValuePair<String,KeyValuePair<bool,List<String>>>> GetDownloadPagesFromPage(String key,String _url)
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
                    if (node.InnerText == "SFW"||node.InnerText== "NSFW (R-15)")
                        r18 = false;
            }
            return new KeyValuePair<string, KeyValuePair<bool, List<string>>>(
                key,
                new KeyValuePair<bool, List<string>>(r18,urls));
        }
        private async Task<Dictionary<String, HashSet<String>>> GetOutterLinksFromDownloadPage(String _url)
        {
            var doc = await RequestHtml(_url);
            var ret = new Dictionary<String, HashSet<String>>();
            if (doc != null)
                foreach (var node in doc.DocumentNode.SelectNodes("//div[@class='entry-content']"))
                    foreach (var a in node.SelectNodes("a"))
                    {
                        if (!ret.ContainsKey(a.Id))
                            ret.Add(a.Id, new HashSet<String>());
                        ret[a.Id].Add(a.Attributes["href"].Value);
                    }
            return ret;
        }
        private async Task<KeyValuePair<String,String>> GetRealURLFromZippyshare(String _url)
        {
            var doc = await RequestHtml(_url);
            var btn_node = doc.DocumentNode.SelectSingleNode("//a[@id='dlbutton']");
            if (btn_node is null)
                return new KeyValuePair<string, string>("", "");
            var parent_node = btn_node.ParentNode;
            var script = parent_node.SelectSingleNode("script").InnerText;
            var match = Regex.Match(script, "document.getElementById\\('dlbutton'\\).href.*?=(.*?);");
            var express = match.Groups[1].Value;
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
