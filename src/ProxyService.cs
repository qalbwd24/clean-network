using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CleanBrowser {
    public sealed class ProxyService : IDisposable {
        readonly Paths paths; readonly Settings settings;
        HttpClient api; Process process; string secret; string config;
        public int Port {get;private set;} public int ApiPort {get;private set;}
        public int[] SlotPorts {get;private set;}
        public string Source {get;private set;} public string SourcePath {get;private set;}
        public string Current {get;private set;}
        public const string Route="CB_ROUTE";
        public ProxyService(Paths p,Settings s) {paths=p;settings=s;config=Path.Combine(p.Data,"core","config.yaml");}
        public static int FreePort() {var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
        public static string ExtractNodes(string yaml) {
            var lines=yaml.Replace("\r","").TrimStart('\ufeff').Split('\n');var sb=new StringBuilder();
            bool keep=false,found=false;
            foreach(var line in lines) {
                if(Regex.IsMatch(line,@"^[A-Za-z_][A-Za-z0-9_-]*\s*:")) {
                    keep=Regex.IsMatch(line,@"^(proxies|proxy-providers)\s*:");if(keep)found=true;
                }
                if(keep)sb.AppendLine(line);
            }
            if(!found)throw new InvalidDataException("配置中没有 proxies 或 proxy-providers 节点表。");
            return sb.ToString();
        }
        string FindSource() {
            string live=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"io.github.clash-verge-rev.clash-verge-rev\clash-verge.yaml");
            var candidates=new List<string>();if(!String.IsNullOrWhiteSpace(settings.NodeSource))candidates.Add(settings.NodeSource);candidates.Add(live);
            foreach(string p in candidates)if(File.Exists(p))try {
                ExtractNodes(File.ReadAllText(p,Encoding.UTF8));Source=p==live?"Clash Verge":"自选配置";SourcePath=p;return p;
            }catch(InvalidDataException){}
            throw new FileNotFoundException("没有读到节点表。请打开 Clash Verge，或在设置里选择 Clash YAML 配置文件。");
        }
        string CoreExe() {
            string p=Path.Combine(paths.Install,@"assets\bin\mihomo.exe");
            if(!File.Exists(p))throw new FileNotFoundException("缺少 assets\\bin\\mihomo.exe 代理内核。");return p;
        }
        string PrivateProviders(string yaml,string source) {
            // Provider caches must belong to this tool, never to the source subscription.
            return Regex.Replace(yaml,@"(?ms)^proxy-providers\s*:.*?(?=^[A-Za-z_][A-Za-z0-9_-]*\s*:|\z)",delegate(Match section) {
            return Regex.Replace(section.Value,@"(?m)^([ \t]+)path\s*:\s*(.+?)[ \t]*$",delegate(Match m) {
                string raw=m.Groups[2].Value.Trim().Trim('"','\'');
                string original=Path.IsPathRooted(raw)?raw:Path.Combine(Path.GetDirectoryName(source),raw);
                string leaf="provider-"+Hash(original)+".yaml";string dest=Path.Combine(paths.Data,"core","providers",leaf);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if(File.Exists(original))File.Copy(original,dest,true);
                return m.Groups[1].Value+"path: "+Json.Write(dest.Replace('\\','/'));
            });
            });
        }
        static string Hash(string value) {using(var h=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,16);}
        void WriteCore() {
            string source=FindSource();string nodes=PrivateProviders(ExtractNodes(File.ReadAllText(source,Encoding.UTF8)),source);
            var sb=new StringBuilder();sb.AppendLine("# CleanBrowser 2 private runtime. Contains subscription credentials; do not share.");
            sb.AppendLine("mixed-port: "+Port);sb.AppendLine("allow-lan: false\nbind-address: 127.0.0.1\nipv6: false\nmode: rule\nlog-level: silent\nfind-process-mode: off");
            sb.AppendLine("external-controller: 127.0.0.1:"+ApiPort);sb.AppendLine("secret: "+Json.Write(secret));
            sb.AppendLine("profile:\n  store-selected: false\n  store-fake-ip: false\ndns:\n  enable: false");
            sb.AppendLine(nodes);sb.AppendLine("proxy-groups:\n- name: CB_ROUTE\n  type: select\n  include-all: true");
            for(int i=0;i<SlotPorts.Length;i++)sb.AppendLine("- name: CB_SLOT_"+i+"\n  type: select\n  include-all: true");
            sb.AppendLine("listeners:");for(int i=0;i<SlotPorts.Length;i++)sb.AppendLine("- name: browser-"+i+"\n  type: mixed\n  listen: 127.0.0.1\n  port: "+SlotPorts[i]+"\n  udp: false\n  proxy: CB_SLOT_"+i);
            sb.AppendLine("rules:\n- MATCH,CB_ROUTE");
            Atomic.Write(config,sb.ToString());
        }
        public async Task<List<string>> Start(CancellationToken ct) {
            if(process!=null&&!process.HasExited)return await Names(ct);
            Port=FreePort();do{ApiPort=FreePort();}while(ApiPort==Port);
            var occupied=new HashSet<int>{Port,ApiPort};SlotPorts=new int[4];for(int i=0;i<4;i++){int port;do{port=FreePort();}while(!occupied.Add(port));SlotPorts[i]=port;}
            byte[] b=new byte[32];using(var rng=System.Security.Cryptography.RandomNumberGenerator.Create())rng.GetBytes(b);secret=BitConverter.ToString(b).Replace("-","");
            await Task.Run(()=>WriteCore(),ct);
            var start=new ProcessStartInfo(CoreExe(),"-f \""+config+"\" -d \""+Path.Combine(paths.Data,"core")+"\"") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.Combine(paths.Data,"core"),RedirectStandardError=true,RedirectStandardOutput=true};
            process=Process.Start(start);
            // Drain asynchronously without persisting proxy internals or credentials.
            process.OutputDataReceived+=(s,e)=>{};process.ErrorDataReceived+=(s,e)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
            api=new HttpClient(new HttpClientHandler {UseProxy=false});api.Timeout=TimeSpan.FromSeconds(12);api.BaseAddress=new Uri("http://127.0.0.1:"+ApiPort);api.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",secret);
            for(int i=0;i<70;i++) {
                ct.ThrowIfCancellationRequested();if(process.HasExited)throw new Exception("独立代理内核未能启动，请检查节点配置。");
                try {await Call("GET","/version",null,ct);var names=await Names(ct);if(names.Count==0)throw new Exception("节点表为空。");
                    await Select(names.Contains(settings.LastNode)?settings.LastNode:names[0],ct);paths.Log("core-start","nodes="+names.Count);return names;
                }catch(HttpRequestException){}catch(TaskCanceledException){ct.ThrowIfCancellationRequested();}
                await Task.Delay(180,ct);
            }
            throw new TimeoutException("独立代理内核启动超时。");
        }
        public async Task<Dictionary<string,object>> Call(string method,string route,object body,CancellationToken ct) {
            using(var req=new HttpRequestMessage(new HttpMethod(method),route)) {
                if(body!=null)req.Content=new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
                using(var response=await api.SendAsync(req,ct)) {
                    if(!response.IsSuccessStatusCode)throw new HttpRequestException("内核请求失败（"+(int)response.StatusCode+"）。");
                    string text=await response.Content.ReadAsStringAsync();return String.IsNullOrWhiteSpace(text)?new Dictionary<string,object>():Json.Read(text);
                }
            }
        }
        public async Task<List<string>> Names(CancellationToken ct) {var r=await Call("GET","/proxies/"+Route,null,ct);object all;if(!r.TryGetValue("all",out all))throw new Exception("内核没有返回可选节点。");return Json.Arr(all).Select(Convert.ToString).Where(n=>n!="DIRECT"&&n!="REJECT"&&n!="COMPATIBLE"&&n!=Route).Distinct().ToList();}
        public async Task<List<string>> Reload(CancellationToken ct) {await Task.Run(()=>WriteCore(),ct);await Call("PUT","/configs?force=true",Json.D("path",config),ct);var names=await Names(ct);await Select(names.Contains(Current)?Current:names[0],ct);return names;}
        public async Task Select(string name,CancellationToken ct) {
            await Call("PUT","/proxies/"+Route,Json.D("name",name),ct);
            var r=await Call("GET","/proxies/"+Route,null,ct);if(Json.Str(r,"now")!=name)throw new Exception("节点切换未生效。");
            Current=name;
            // Old keep-alive tunnels must not carry the previous node into a new check.
            await ClearConnections(Route,ct);
        }
        public async Task ClearConnections(string group,CancellationToken ct) {var r=await Call("GET","/connections",null,ct);object connections;if(r.TryGetValue("connections",out connections))foreach(var entry in Json.Arr(connections)){var c=Json.Obj(entry);object chains;if(c.TryGetValue("chains",out chains)&&Json.Arr(chains).Select(Convert.ToString).Contains(group))await Call("DELETE","/connections/"+Uri.EscapeDataString(Json.Str(c,"id")),null,ct);}}
        public async Task SelectSlot(int slot,string name,CancellationToken ct) {string group="CB_SLOT_"+slot;await Call("PUT","/proxies/"+group,Json.D("name",name),ct);var r=await Call("GET","/proxies/"+group,null,ct);if(Json.Str(r,"now")!=name)throw new Exception("独立窗口的节点绑定失败。");await ClearConnections(group,ct);}
        public async Task<int> Delay(string name,CancellationToken ct) {
            var r=await Call("GET","/proxies/"+Uri.EscapeDataString(name)+"/delay?timeout=7000&url="+Uri.EscapeDataString(settings.LatencyUrl),null,ct);
            int ms=(int)Json.Num(r,"delay",0);if(ms<=0)throw new Exception("节点未在 7 秒内返回有效延迟。");return ms;
        }
        public HttpClient ProxyClient(int timeout=18,int port=0) {var h=new HttpClientHandler {UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+(port==0?Port:port)),UseCookies=false};var c=new HttpClient(h){Timeout=TimeSpan.FromSeconds(timeout)};c.DefaultRequestHeaders.UserAgent.ParseAdd("CleanBrowser/2.0");return c;}
        public async Task<ExitInfo> Exit(CancellationToken ct,int port=0) {
            using(var c=ProxyClient(14,port)) {
                foreach(string url in new[]{"https://ipwho.is/","https://ipapi.co/json/"})try {
                    using(var r=await c.GetAsync(url,ct)) {r.EnsureSuccessStatusCode();var o=Json.Read(await r.Content.ReadAsStringAsync());string ip=Json.Str(o,"ip");IPAddress parsed;
                        if(!IPAddress.TryParse(ip,out parsed))continue;
                        object tz;string zone=o.TryGetValue("timezone",out tz)?(tz is string?(string)tz:Json.Str(Json.Obj(tz),"id")):"";
                        return new ExitInfo {IP=ip,Country=Json.Str(o,"country",Json.Str(o,"country_name")),City=Json.Str(o,"city"),Timezone=zone,ISP=Json.Str(Json.Obj(o.ContainsKey("connection")?o["connection"]:null),"isp",Json.Str(o,"org"))};
                    }
                }catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}catch(HttpRequestException){}catch(ArgumentException){}
            }
            throw new Exception("出口查询失败；不会沿用上一个节点的 IP 或时区。");
        }
        public async Task<double> Speed(CancellationToken ct,int maxBytes=50000000) {
            using(var c=ProxyClient(16))using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(15000);
                using(var r=await c.GetAsync("https://speed.cloudflare.com/__down?bytes="+maxBytes,HttpCompletionOption.ResponseHeadersRead,linked.Token)) {
                    r.EnsureSuccessStatusCode();using(var stream=await r.Content.ReadAsStreamAsync()) {
                        byte[] buf=new byte[65536];long total=0;var clock=Stopwatch.StartNew();linked.CancelAfter(4000);
                        try {while(true){int n=await stream.ReadAsync(buf,0,buf.Length,linked.Token);if(n==0)break;total+=n;}}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}
                        if(total==0)throw new Exception("未收到下载数据。");return Math.Round(total*8.0/Math.Max(clock.Elapsed.TotalSeconds,0.05)/1000000,1);
                    }
                }
            }
        }
        public async Task<double> QuickSpeed(bool upload,Action<double> progress,CancellationToken ct){using(var client=ProxyClient(16)){client.DefaultRequestHeaders.ExpectContinue=false;return upload?await SpeedMeasurements.Upload(client,"https://speed.cloudflare.com/__up",progress,ct):await SpeedMeasurements.Download(client,"https://speed.cloudflare.com/__down?bytes="+SpeedMeasurements.MaxBytes,progress,ct);}}
        public void Dispose() {if(api!=null)api.Dispose();if(process!=null){try{if(!process.HasExited){process.Kill();process.WaitForExit(2500);}}catch{}process.Dispose();}process=null;}
    }
}
