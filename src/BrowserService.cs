using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CleanBrowser {
    public sealed class Cdp : IDisposable {
        readonly ClientWebSocket socket=new ClientWebSocket();
        readonly ConcurrentDictionary<int,TaskCompletionSource<Dictionary<string,object>>> pending=new ConcurrentDictionary<int,TaskCompletionSource<Dictionary<string,object>>>();
        readonly SemaphoreSlim sendGate=new SemaphoreSlim(1,1);
        readonly CancellationTokenSource life=new CancellationTokenSource();int sequence;
        public event Action<Dictionary<string,object>> Event;
        public Task ReceiveTask {get;private set;}
        public bool Open {get{return socket.State==WebSocketState.Open;}}
        public async Task Connect(string url,CancellationToken ct) {using(var t=CancellationTokenSource.CreateLinkedTokenSource(ct)){t.CancelAfter(8000);await socket.ConnectAsync(new Uri(url),t.Token);}ReceiveTask=Task.Run((Func<Task>)ReceiveLoop);}
        async Task ReceiveLoop() {
            var buf=new byte[65536];
            try {while(Open&&!life.IsCancellationRequested)using(var memory=new MemoryStream()) {
                WebSocketReceiveResult r;
                do {r=await socket.ReceiveAsync(new ArraySegment<byte>(buf),life.Token);if(r.MessageType==WebSocketMessageType.Close)throw new IOException("浏览器连接已关闭。");memory.Write(buf,0,r.Count);if(memory.Length>24*1024*1024)throw new IOException("浏览器响应过大。");}while(!r.EndOfMessage);
                var msg=Json.Read(Encoding.UTF8.GetString(memory.ToArray()));object id;
                if(msg.TryGetValue("id",out id)) {TaskCompletionSource<Dictionary<string,object>> waiter;if(pending.TryRemove(Convert.ToInt32(id),out waiter)) {
                    if(msg.ContainsKey("error"))waiter.TrySetException(new Exception(Json.Str(Json.Obj(msg["error"]),"message","浏览器命令失败。")));
                    else waiter.TrySetResult(Json.Obj(msg.ContainsKey("result")?msg["result"]:null));
                }}else if(Event!=null)Event(msg);
            }}catch(Exception e){foreach(var item in pending.Values)item.TrySetException(new IOException("浏览器连接已断开。",e));pending.Clear();}
        }
        public async Task<Dictionary<string,object>> Call(string method,object args,CancellationToken ct,string session=null,int timeout=15000) {
            ct.ThrowIfCancellationRequested();int id=Interlocked.Increment(ref sequence);
            var tcs=new TaskCompletionSource<Dictionary<string,object>>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=tcs;
            var payload=Json.D("id",id,"method",method,"params",args??new object());if(session!=null)payload["sessionId"]=session;
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,life.Token)) {
                linked.CancelAfter(timeout);
                using(linked.Token.Register(()=>tcs.TrySetCanceled()))try {
                    var bytes=Encoding.UTF8.GetBytes(Json.Write(payload));await sendGate.WaitAsync(linked.Token);
                    try {await socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,linked.Token);}finally{sendGate.Release();}
                    return await tcs.Task;
                }finally{TaskCompletionSource<Dictionary<string,object>> unused;pending.TryRemove(id,out unused);}
            }
        }
        public void Dispose() {life.Cancel();socket.Dispose();foreach(var t in pending.Values)t.TrySetCanceled();pending.Clear();}
    }
    public sealed class BrowserService : IDisposable {
        readonly Paths paths;readonly Settings settings;readonly ProxyService proxy;readonly int endpoint;
        Cdp cdp;Process chrome;int port;string timezone="",userAgent="";
        readonly ConcurrentDictionary<string,string> sessions=new ConcurrentDictionary<string,string>();
        readonly ConcurrentDictionary<string,Task> prepared=new ConcurrentDictionary<string,Task>();
        readonly ConcurrentDictionary<string,bool> marked=new ConcurrentDictionary<string,bool>();
        readonly ConcurrentDictionary<string,string> cleanRequests=new ConcurrentDictionary<string,string>();
        readonly ConcurrentDictionary<string,bool> cleanSelfRequests=new ConcurrentDictionary<string,bool>();
        readonly object cleanGate=new object();Dictionary<string,object> cleanLatest;string cleanSession;bool cleanIsSelf;
        public event Action<IpQualitySnapshot> CleanIpObserved;
        readonly Dictionary<string,string> siteTargets=new Dictionary<string,string>();
        public string Timezone {get{return timezone;}}
        public string NodeLabel {get;set;}
        public bool Headless {get;set;}
        public bool Running {get{return cdp!=null&&cdp.Open;}}
        public BrowserService(Paths p,Settings s,ProxyService core,int proxyPort=0){paths=p;settings=s;proxy=core;endpoint=proxyPort;}
        string ChromeExe() {
            foreach(string p in new[]{settings.ChromePath,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"Google\Chrome\Application\chrome.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"Google\Chrome\Application\chrome.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Google\Chrome\Application\chrome.exe")})if(!String.IsNullOrWhiteSpace(p)&&File.Exists(p))return p;
            throw new FileNotFoundException("没有找到 Chrome，请在设置里选择 chrome.exe。");
        }
        public async Task Ensure(CancellationToken ct) {
            if(Running)return;
            if(cdp!=null)cdp.Dispose();sessions.Clear();prepared.Clear();marked.Clear();cleanRequests.Clear();cleanSelfRequests.Clear();lock(cleanGate)cleanLatest=null;siteTargets.Clear();
            Directory.CreateDirectory(paths.Profile);port=ProxyService.FreePort();
            // No shared profile, extension injection, system proxy or Windows timezone changes.
            var args="--user-data-dir=\""+paths.Profile+"\" --no-first-run --no-default-browser-check --disable-sync --disable-quic --disable-background-networking --disable-session-crashed-bubble --hide-crash-restore-bubble --proxy-server=http://127.0.0.1:"+(endpoint==0?proxy.Port:endpoint)+" --remote-debugging-address=127.0.0.1 --remote-debugging-port="+port+" --webrtc-ip-handling-policy=disable_non_proxied_udp --force-webrtc-ip-handling-policy=disable_non_proxied_udp --lang="+settings.Locale+" --accept-lang="+settings.Locale+" about:blank";
            if(Headless)args+=" --headless=new --window-size=1400,1000";
            chrome=Process.Start(new ProcessStartInfo(ChromeExe(),args){UseShellExecute=false,WorkingDirectory=paths.Install});
            using(var client=new HttpClient(new HttpClientHandler {UseProxy=false}){Timeout=TimeSpan.FromSeconds(2)}) {
                for(int i=0;i<70;i++) {
                    ct.ThrowIfCancellationRequested();
                    try {var v=Json.Read(await client.GetStringAsync("http://127.0.0.1:"+port+"/json/version"));
                        cdp=new Cdp();await cdp.Connect(Json.Str(v,"webSocketDebuggerUrl"),ct);
                        var version=await cdp.Call("Browser.getVersion",null,ct);userAgent=Json.Str(version,"userAgent");
                        cdp.Event+=OnEvent;
                        await cdp.Call("Target.setAutoAttach",Json.D("autoAttach",true,"waitForDebuggerOnStart",true,"flatten",true,"filter",new[]{Json.D("type","page","exclude",false),Json.D("exclude",true)}),ct);
                        paths.Log("browser-start","isolated profile ready");return;
                    }catch(HttpRequestException){}catch(TaskCanceledException){ct.ThrowIfCancellationRequested();}
                    if(chrome.HasExited)throw new Exception("Chrome 未能打开独立环境；请先关闭此工具的旧浏览器窗口。");
                    await Task.Delay(200,ct);
                }
            }
            throw new TimeoutException("Chrome 启动超时，请检查浏览器路径。");
        }
        void OnEvent(Dictionary<string,object> message) {
            string method=Json.Str(message,"method");
            if(method=="Network.responseReceived"){
                var p=Json.Obj(message["params"]);string url=Json.Str(Json.Obj(p["response"]),"url");Uri uri;
                System.Net.IPAddress apiIP;
                if(Uri.TryCreate(url,UriKind.Absolute,out uri)&&uri.Host=="cleanip.io"&&uri.AbsolutePath.StartsWith("/api/v2/",StringComparison.Ordinal)&&(uri.AbsolutePath=="/api/v2/me"||System.Net.IPAddress.TryParse(Uri.UnescapeDataString(uri.AbsolutePath.Substring(8)),out apiIP))){string request=Json.Str(p,"requestId");cleanRequests[request]=Json.Str(message,"sessionId");cleanSelfRequests[request]=uri.AbsolutePath=="/api/v2/me";}
            }else if(method=="Network.loadingFinished"){
                var p=Json.Obj(message["params"]);string sid;bool isSelf;if(cleanRequests.TryRemove(Json.Str(p,"requestId"),out sid)){cleanSelfRequests.TryRemove(Json.Str(p,"requestId"),out isSelf);CaptureCleanIp(Json.Str(p,"requestId"),sid,isSelf);}
            }
            if(Json.Str(message,"method")=="Target.attachedToTarget") {
                var p=Json.Obj(message["params"]);string id=Json.Str(Json.Obj(p["targetInfo"]),"targetId"),sid=Json.Str(p,"sessionId");sessions[id]=sid;
                prepared[id]=Prepare(sid);
            }else if(Json.Str(message,"method")=="Target.detachedFromTarget") {
                var p=Json.Obj(message["params"]);string id=Json.Str(p,"targetId"),unused;sessions.TryRemove(id,out unused);
            }
        }
        async Task<IpQualitySnapshot> WithPageChecks(IpQualitySnapshot snapshot,string sid,CancellationToken ct){
            if(!snapshot.Success||String.IsNullOrEmpty(sid))return snapshot;
            try{string script=File.ReadAllText(Path.Combine(paths.Install,"assets","CleanIpPage.js"),Encoding.UTF8);var result=await cdp.Call("Runtime.evaluate",Json.D("expression","JSON.stringify("+script+ ")","returnByValue",true),ct,sid,4000);var page=Json.Read(Json.Str(Json.Obj(result["result"]),"value","{}"));object checks;if(page.TryGetValue("checks",out checks)&&Json.Arr(checks).Any())CleanIpResult.ApplyChecks(snapshot,Json.Arr(checks));}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}catch{}return snapshot;
        }
        async void CaptureCleanIp(string request,string sid,bool isSelf){try{var r=await cdp.Call("Network.getResponseBody",Json.D("requestId",request),CancellationToken.None,sid,10000);string text=Json.Str(r,"body");if(Json.Bool(r,"base64Encoded"))text=Encoding.UTF8.GetString(Convert.FromBase64String(text));if(text.Length>2000000)return;var body=Json.Read(text);if(body.ContainsKey("purity")||body.ContainsKey("risk")||body.ContainsKey("error")){lock(cleanGate){cleanLatest=body;cleanSession=sid;cleanIsSelf=isSelf;}if(CleanIpObserved!=null){await Task.Delay(500);var snapshot=await WithPageChecks(CleanIpResult.Parse(body,""),sid,CancellationToken.None);snapshot.ObservedAsCurrentExit=isSelf;lock(cleanGate){if(!Object.ReferenceEquals(cleanLatest,body))return;}CleanIpObserved(snapshot);}}}catch{}}
        public async Task<IpQualitySnapshot> ReadCleanIp(string expectedIP,CancellationToken ct,bool useWebsiteExit=false){var clock=Stopwatch.StartNew();IpQualitySnapshot last=null;while(clock.ElapsedMilliseconds<30000){ct.ThrowIfCancellationRequested();Dictionary<string,object> data;string sid;bool self;lock(cleanGate){data=cleanLatest;sid=cleanSession;self=cleanIsSelf;}if(data!=null){last=CleanIpResult.ParseObservation(data,expectedIP,self,useWebsiteExit);if(last.Success){for(int attempt=0;attempt<6;attempt++){await Task.Delay(500,ct);last=await WithPageChecks(last,sid,ct);if(last.ChecksTotal>0)break;}return last;}if(last.State=="需要网站验证"||last.State=="网站限流"||last.State=="需要登录"||last.State.StartsWith("出口 IP 不一致"))return last;}await Task.Delay(300,ct);}return last??new IpQualitySnapshot {State="页面尚未返回评分，可能需要验证"};}
        async Task Prepare(string sid) {
            try {await Apply(sid,CancellationToken.None);await InstallMarker(sid,CancellationToken.None,false);}catch(Exception){paths.Log("page-override","无法设置新标签页环境");}
            try{await cdp.Call("Runtime.runIfWaitingForDebugger",null,CancellationToken.None,sid);}catch{}
        }
        async Task InstallMarker(string sid,CancellationToken ct,bool evaluate=true){if(String.IsNullOrEmpty(NodeLabel))return;string script=File.ReadAllText(Path.Combine(paths.Install,"assets","NodeBadge.js"),Encoding.UTF8).Replace("__NODE_LABEL_JSON__",Json.Write(NodeLabel));if(marked.TryAdd(sid,true))await cdp.Call("Page.addScriptToEvaluateOnNewDocument",Json.D("source",script),ct,sid);if(evaluate){var r=await cdp.Call("Runtime.evaluate",Json.D("expression",script),ct,sid);if(r.ContainsKey("exceptionDetails"))throw new Exception("节点标识脚本未能应用。");}}
        async Task Apply(string sid,CancellationToken ct) {
            await cdp.Call("Emulation.setTimezoneOverride",Json.D("timezoneId",timezone),ct,sid);
            await cdp.Call("Emulation.setLocaleOverride",Json.D("locale",settings.Locale),ct,sid);
            if(!String.IsNullOrEmpty(userAgent))await cdp.Call("Network.setUserAgentOverride",Json.D("userAgent",userAgent,"acceptLanguage",settings.Locale),ct,sid);
        }
        public async Task SetTimezone(string zone,CancellationToken ct) {
            timezone=settings.FollowTimezone?zone??"":"";
            if(Running)foreach(string sid in sessions.Values.ToArray())try {await Apply(sid,ct);}catch(OperationCanceledException){throw;}catch(Exception){paths.Log("page-override","已有页面无法设置时区");}
        }
        async Task<string> Session(string id,CancellationToken ct) {
            for(int i=0;i<45;i++){ct.ThrowIfCancellationRequested();string sid;if(sessions.TryGetValue(id,out sid)){Task task;if(prepared.TryGetValue(id,out task))await task;return sid;}await Task.Delay(100,ct);}
            throw new Exception("没有连接上新标签页。");
        }
        public async Task<string> Open(string url,CancellationToken ct,bool wait) {
            if(!Settings.ValidUrl(url))throw new ArgumentException("网址必须是完整的 http 或 https 地址。");
            await Ensure(ct);string id;
            var targets=await cdp.Call("Target.getTargets",null,ct);var ids=new HashSet<string>(Json.Arr(targets["targetInfos"]).Select(x=>Json.Str(Json.Obj(x),"targetId")));
            if(!siteTargets.TryGetValue(url,out id)||!ids.Contains(id)) {
                var target=await cdp.Call("Target.createTarget",Json.D("url","about:blank"),ct);id=Json.Str(target,"targetId");siteTargets[url]=id;
            }
            string sid=await Session(id,ct);await Apply(sid,ct);
            if(new Uri(url).Host=="cleanip.io"){lock(cleanGate)cleanLatest=null;}
            await cdp.Call("Network.enable",null,ct,sid);await cdp.Call("Network.setCacheDisabled",Json.D("cacheDisabled",true),ct,sid);
            var nav=await cdp.Call("Page.navigate",Json.D("url",url),ct,sid);string err=Json.Str(nav,"errorText");if(err!="")throw new Exception("页面未打开："+err);
            if(!Headless)await cdp.Call("Target.activateTarget",Json.D("targetId",id),ct);
            if(wait) {
                var clock=Stopwatch.StartNew();bool ready=false;
                while(clock.ElapsedMilliseconds<18000){ct.ThrowIfCancellationRequested();try{var r=await cdp.Call("Runtime.evaluate",Json.D("expression","document.readyState","returnByValue",true),ct,sid,4000);if(Json.Str(Json.Obj(r["result"]),"value")=="complete"){ready=true;break;}}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();}catch(Exception){}await Task.Delay(250,ct);}
                if(!ready)throw new TimeoutException("页面 18 秒内未加载完成，未将截图标记为成功。");
                await Task.Delay(settings.SettleMs,ct);
            }
            await InstallMarker(sid,ct);return sid;
        }
        public async Task Screenshot(string sid,string file,CancellationToken ct) {
            var r=await cdp.Call("Page.captureScreenshot",Json.D("format","png","captureBeyondViewport",false),ct,sid,20000);
            Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllBytes(file,Convert.FromBase64String(Json.Str(r,"data")));
        }
        public async Task<Dictionary<string,object>> InspectPage(string sid,CancellationToken ct) {
            var r=await cdp.Call("Runtime.evaluate",Json.D("expression","JSON.stringify({tz:Intl.DateTimeFormat().resolvedOptions().timeZone,lang:navigator.language,url:location.href,title:document.title,marker:document.getElementById('__cleanNetworkNodeMarker')?.textContent||''})","returnByValue",true),ct,sid);
            return Json.Read(Json.Str(Json.Obj(r["result"]),"value"));
        }
        public async Task<Dictionary<string,object>> ReadVisiblePage(string sid,CancellationToken ct){
            var r=await cdp.Call("Runtime.evaluate",Json.D("expression","JSON.stringify({title:document.title,url:location.href,text:(document.body?.innerText||'').slice(0,18000),challenge:!!document.querySelector('iframe[src*=\"challenges.cloudflare.com\"],input[name=\"cf-turnstile-response\"]')})","returnByValue",true),ct,sid);
            return Json.Read(Json.Str(Json.Obj(r["result"]),"value"));
        }
        public async Task Close() {
            if(cdp!=null){try{await cdp.Call("Browser.close",null,CancellationToken.None,null,3000);}catch{}cdp.Dispose();cdp=null;}
            sessions.Clear();prepared.Clear();marked.Clear();siteTargets.Clear();
            if(chrome!=null) {try{if(!chrome.HasExited&&!chrome.WaitForExit(2500))chrome.Kill();}catch{}chrome.Dispose();chrome=null;}
        }
        public void Dispose(){if(cdp!=null)cdp.Dispose();}
    }
}
