using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace CleanBrowser {
    public static class Program {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        static string Arg(string[] args,string name,string fallback) {int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        [STAThread] public static int Main(string[] args) {
            try {
                try{SetProcessDPIAware();}catch{}
                System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;
                string install=Arg(args,"--install-root",AppDomain.CurrentDomain.BaseDirectory);
                bool demo=args.Contains("--demo"),self=args.Contains("--self-test"),integration=args.Contains("--integration-test");
                bool isolated=args.Contains("--data-root")||demo||self||integration;
                string data=Arg(args,"--data-root",Path.Combine(install,isolated?"tmp\\preview":"data"));
                var paths=new Paths(install,data,isolated);
                var settings=Settings.Load(data,install);
                if(args.Contains("--enable-startup")){AutoStart.Set(true,install);Atomic.Write(Path.Combine(install,"outputs","checks","autostart.json"),Json.Write(Json.D("enabled",AutoStart.Enabled,"waitForClash",true,"time",DateTimeOffset.Now.ToString("o"))));return 0;}
                if(args.Contains("--boot-readiness-test"))return BootReadiness(paths).GetAwaiter().GetResult();
                if(self)return SelfTest(paths);
                if(integration)return Integration(paths,settings,Arg(args,"--node",""),Arg(args,"--second-node","")).GetAwaiter().GetResult();
                if(args.Contains("--report-test"))return TestReport(paths,settings,Arg(args,"--node",""),Arg(args,"--sites","https://example.com/")).GetAwaiter().GetResult();
                string identity;using(var h=System.Security.Cryptography.SHA256.Create())identity=BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(paths.Data.ToLowerInvariant()))).Replace("-","").Substring(0,20);
                bool created;
                using(var single=new Mutex(true,"Local\\CleanNetwork-"+identity,out created))
                using(var wake=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\CleanNetwork-Wake-"+identity))
                using(var exit=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\CleanNetwork-Exit-"+identity)) {
                    if(!created){if(args.Contains("--quit"))exit.Set();else wake.Set();return 0;}
                    if(args.Contains("--quit")){single.ReleaseMutex();return 0;}
                    var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
                    Controller controller=null;
                    app.DispatcherUnhandledException+=(s,e)=>{paths.Log("ui-error",e.Exception.GetType().Name);MessageBox.Show("操作未完成："+e.Exception.Message,"纯净网络检测工具");e.Handled=true;};
                    controller=new Controller(paths,settings,demo,args.Contains("--startup"));
                    var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};timer.Tick+=async(s,e)=>{if(exit.WaitOne(0))await controller.Quit();else if(wake.WaitOne(0))controller.Show();};timer.Start();
                    app.Run(controller.Window);timer.Stop();single.ReleaseMutex();return 0;
                }
            }catch(Exception e){string text=e.GetType().Name+": "+e.Message;try{string root=AppDomain.CurrentDomain.BaseDirectory;Directory.CreateDirectory(Path.Combine(root,"logs"));File.AppendAllText(Path.Combine(root,"logs","startup.log"),DateTimeOffset.Now.ToString("o")+" "+text+Environment.NewLine,Encoding.UTF8);}catch{}if(!args.Contains("--self-test")&&!args.Contains("--integration-test"))MessageBox.Show(text,"纯净网络检测工具 — 启动失败");return 1;}
        }
        static async Task<int> BootReadiness(Paths p){bool rejected=!await AutoStart.Probe(ProxyService.FreePort(),CancellationToken.None);var clock=System.Diagnostics.Stopwatch.StartNew();int port=await AutoStart.WaitForClash(CancellationToken.None);bool pass=rejected&&port>0;Atomic.Write(Path.Combine(p.Install,"outputs","checks","boot-readiness.json"),Json.Write(Json.D("pass",pass,"closedPortRejected",rejected,"liveClashAccepted",port>0,"readyMs",clock.ElapsedMilliseconds,"time",DateTimeOffset.Now.ToString("o"))));return pass?0:4;}
        static int SelfTest(Paths p) {
            var tests=new List<object>();
            Action<string,Action> test=(name,body)=>{try{body();tests.Add(Json.D("test",name,"pass",true));}catch(Exception e){tests.Add(Json.D("test",name,"pass",false,"error",e.Message));}};
            Action<bool,string> check=(ok,msg)=>{if(!ok)throw new Exception(msg);};
            test("仅提取节点，不继承系统端口、TUN、规则",()=>{string yaml="mixed-port: 7897\ntun:\n  enable: true\nproxies:\n- name: '日本: 01'\n  type: ss\n  server: example.com\n  password: test-only\nproxy-groups:\n- name: shared\nrules:\n- MATCH,DIRECT\n";string extracted=ProxyService.ExtractNodes(yaml);check(extracted.Contains("日本: 01")&&!extracted.Contains("7897")&&!extracted.Contains("tun:")&&!extracted.Contains("shared"),"提取边界有误");});
            test("支持代理提供者与内联 YAML",()=>{string extracted=ProxyService.ExtractNodes("proxies: [{name: demo, type: direct}]\nproxy-providers:\n  sample:\n    path: ./nodes.yaml\nlisteners:\n- name: shared\n");check(extracted.Contains("proxy-providers:")&&!extracted.Contains("listeners"),"提供者边界有误");});
            test("缺失节点明确报错",()=>{bool failed=false;try{ProxyService.ExtractNodes("mixed-port: 7897\n");}catch(InvalidDataException){failed=true;}check(failed,"误接受空配置");});
            test("网址限制及本地验证",()=>{check(Settings.ValidUrl("https://example.com/a?q=1")&&Settings.ValidUrl("http://127.0.0.1:8888/")&&!Settings.ValidUrl("javascript:alert(1)")&&!Settings.ValidUrl("file:///C:/test")&&!Settings.ValidUrl("https://user:password@example.com/"),"网址检查失败");});
            test("设置往返与勾选状态持久化",()=>{string dir=Path.Combine(p.Data,"settings-test");var s=new Settings {FontSize=17.25,Theme="dark",LastNode="香港 01"};s.Sites.Add(new Site {Url="https://example.com/",Enabled=false});s.Save(dir);var restored=Settings.Load(dir,p.Install);check(restored.FontSize==17.25&&restored.Theme=="dark"&&!restored.Sites[0].Enabled&&restored.LastNode==s.LastNode,"设置丢失");});
            test("损坏设置不被静默覆盖",()=>{string dir=Path.Combine(p.Data,"bad-settings-test");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"settings.json"),"{BROKEN",Encoding.UTF8);bool failed=false;try{Settings.Load(dir,p.Install);}catch{failed=true;}check(failed,"损坏配置被忽略");});
            test("原子更新 UTF8 中文文件",()=>{string file=Path.Combine(p.Data,"atomic-test.txt");Atomic.Write(file,"初稿");Atomic.Write(file,"新版 ✓");check(File.ReadAllText(file,Encoding.UTF8)=="新版 ✓"&&!File.Exists(file+".writing"),"原子替换失败");});
            test("新旧独立端口",()=>{int a=ProxyService.FreePort();check(a>1024&&a<65536,"空闲端口无效");});
            test("网站评分未知不编造",()=>{var unknown=QualityReport.Interpret("https://example.com/",Json.D("title","Example","text","A normal page","challenge",false));var challenge=QualityReport.Interpret("https://example.com/",Json.D("title","Just a moment...","text","Risk score 10%","challenge",true));check(unknown.Evidence==""&&unknown.State.Contains("未确认")&&challenge.Evidence==""&&challenge.State=="需要人工验证","未知或验证页被误判评分");});
            test("报告摘录保留原始标签",()=>{var observed=QualityReport.Interpret("https://example.com/",Json.D("title","Report","text","风险系数：4%\nHuman 82.28%","challenge",false));check(observed.Evidence.Contains("4%")&&observed.Evidence.Contains("82.28%"),"评分摘录丢失");});
            test("报告 HTML 防止节点名称注入",()=>{string dir=Path.Combine(p.Data,"report-escaping");QualityReport.Save(dir,new[]{new QualityRow {Node="<script>alert(1)</script>",Verdict="未确认"}},false,1);string html=File.ReadAllText(Path.Combine(dir,"index.html"),Encoding.UTF8);check(html.Contains("&lt;script&gt;")&&!html.Contains("<script>alert(1)</script>"),"节点名称未转义");});
            test("图标可读取",()=>{using(var icon=new System.Drawing.Icon(Path.Combine(p.Install,"assets","app.ico"),32,32))check(icon.Width==32,"图标未包含可用尺寸");});
            test("CleanIP 两类评分独立读取",()=>{var result=CleanIpResult.Parse(Json.D("ok",true,"ip","203.0.113.10","purity",Json.D("score",88,"grade","A","risk_factors",new[]{"示例风险原因"}),"risk",Json.D("risk_score",15,"risk_label","Clean")),"203.0.113.10");check(result.Success&&result.Purity==88&&result.Risk==15&&result.RiskFlags.Contains("示例风险原因"),"分数方向或风险原因错误");});
            test("CleanIP 空数据不当零分且校验出口",()=>{var empty=CleanIpResult.Parse(Json.D("ok",true,"ip","203.0.113.10"),"203.0.113.10");var wrong=CleanIpResult.Parse(Json.D("ok",true,"ip","203.0.113.11","purity",Json.D("score",99)),"203.0.113.10");check(!empty.Success&&!empty.Purity.HasValue&&!wrong.Success,"空数据或错误出口记入分数");});
            test("评分失败保留历史并持久化",()=>{var node=new Node{Name="Example"};var good=new IpQualitySnapshot {Success=true,Purity=88,Risk=15,CheckedAt="2026-01-01T00:00:00Z",SourceIP="203.0.113.10",State="已取得网站评分"};CleanIpResult.Apply(node,good);CleanIpResult.Apply(node,new IpQualitySnapshot {State="需要网站验证"});string json=Json.Write(node);var restored=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Node>(json);check(restored.CleanIpPurity==88&&restored.QualityHistory.Count==2&&restored.PurityDisplay.Contains("历史")&&restored.CleanIpCheckedAt==good.CheckedAt,"历史评分被覆盖或丢失");});
            test("订阅切换与节点增删",()=>SubscriptionTest(p).GetAwaiter().GetResult());
            test("大字号布局及 XAML 装载",()=>{var w=(Window)XamlReader.Load(System.Xml.XmlReader.Create(Path.Combine(p.Install,"assets","Main.xaml")));((System.Windows.Controls.Image)w.FindName("AppLogo")).Source=new BitmapImage(new Uri(Path.Combine(p.Install,"assets","app.png")));var root=(FrameworkElement)w.Content;var rows=new[]{new Node{Name="示例节点 A",Delay="286 ms",Speed="24.8",Status="窗口已打开"},new Node{Name="示例节点 B",Delay="312 ms",Status="延迟已测"},new Node{Name="示例节点 C",Status="未检测"},new Node{Name="示例节点 D",Status="未检测"}};((DataGrid)w.FindName("Nodes")).ItemsSource=rows;((ItemsControl)w.FindName("Sites")).ItemsSource=new[]{new Site {Url="https://cleanip.io/",Enabled=true},new Site {Url="https://gemini.google.com/",Enabled=true},new Site {Url="https://iplark.com/",Enabled=true},new Site {Url="https://ippure.com/",Enabled=true}};
                foreach(double font in new[]{12.0,16.0,22.0}){Controller.ApplyTextSize(w,font);root.Measure(new Size(1130,730));root.Arrange(new Rect(0,0,1130,730));root.UpdateLayout();var button=(Button)w.FindName("Check");check(button.ActualHeight>=30&&button.ActualWidth>=150,"主操作被裁切");Render(root,Path.Combine(p.Install,"outputs","checks","ui-"+font+"pt.png"),1130,730);}
                var keys=new[]{"Canvas","Surface","Field","Ink","Muted","Line","Accent","AccentSoft","Hover"};var colors=new[]{"#111723","#1A2231","#202B3D","#E8EEF8","#A0AEC2","#344158","#698AFF","#263A64","#29364B"};for(int i=0;i<keys.Length;i++)w.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));Controller.ApplyTextSize(w,12);root.Measure(new Size(1130,730));root.Arrange(new Rect(0,0,1130,730));root.UpdateLayout();Render(root,Path.Combine(p.Install,"outputs","checks","ui-dark.png"),1130,730);w.Close();});
            bool pass=tests.All(t=>Json.Bool(Json.Obj(t),"pass"));string dest=Path.Combine(p.Install,"outputs","checks","self-test.json");Atomic.Write(dest,Json.Write(Json.D("pass",pass,"tests",tests,"time",DateTimeOffset.Now.ToString("o"))));return pass?0:2;
        }
        static void Render(FrameworkElement root,string file,int width,int height){var b=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle((Brush)root.FindResource("Canvas"),null,new Rect(0,0,width,height));b.Render(background);b.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(b));Directory.CreateDirectory(Path.GetDirectoryName(file));using(var stream=File.Create(file))encoder.Save(stream);}
        static async Task SubscriptionTest(Paths p){string data=Path.Combine(p.Data,"subscription-test");Directory.CreateDirectory(data);string source=Path.Combine(data,"fixture.yaml");Func<string,string> config=name=>"mixed-port: 7897\ntun:\n  enable: true\nproxies:\n- name: "+name+"\n  type: vmess\n  server: 127.0.0.1\n  port: 9\n  uuid: 00000000-0000-4000-8000-000000000001\n  alterId: 0\n  cipher: auto\n  network: ws\n  ws-opts:\n    path: /keep-this-path\nrules:\n- MATCH,DIRECT\n";File.WriteAllText(source,config("Alpha"),Encoding.UTF8);var settings=new Settings {NodeSource=source};using(var core=new ProxyService(new Paths(p.Install,data,true),settings)){var names=await core.Start(CancellationToken.None);if(names.Count!=1||names[0]!="Alpha")throw new Exception("首份订阅未识别");File.WriteAllText(source,config("Beta"),Encoding.UTF8);var next=await core.Reload(CancellationToken.None);if(next.Count!=1||next[0]!="Beta")throw new Exception("订阅切换后仍是旧节点");string generated=File.ReadAllText(Path.Combine(data,"core","config.yaml"),Encoding.UTF8);if(!generated.Contains("path: /keep-this-path")||generated.Contains("enable: true"))throw new Exception("继承了共享配置或改坏 WebSocket 路径");}}
        static async Task<int> Integration(Paths p,Settings settings,string requested,string requestedOther) {
            var results=new List<object>();var sourceHash="";var ctSource=new CancellationTokenSource(TimeSpan.FromSeconds(180));var ct=ctSource.Token;ProxyService core=null;BrowserService browser=null;BrowserSessions windows=null;
            try {
                var clock=System.Diagnostics.Stopwatch.StartNew();core=new ProxyService(p,settings);var names=await core.Start(ct);results.Add(Json.D("test","read-live-nodes","pass",names.Count>0,"count",names.Count,"source",core.Source,"startupMs",clock.ElapsedMilliseconds));
                using(var h=System.Security.Cryptography.SHA256.Create())sourceHash=BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(core.SourcePath)));
                string node=String.IsNullOrEmpty(requested)?names[0]:requested;
                if(!names.Contains(node))throw new Exception("测试节点不在真实节点表中。");await core.Select(node,ct);
                int delay=await core.Delay(node,ct);results.Add(Json.D("test","node-delay","pass",delay>0,"node",node,"ms",delay));
                ExitInfo exit=await core.Exit(ct);results.Add(Json.D("test","proxy-exit","pass",exit.IP!=""&&exit.Timezone!="","ip",exit.IP,"zone",exit.Timezone));
                browser=new BrowserService(p,settings,core);await browser.SetTimezone(exit.Timezone,ct);string sid=await browser.Open("https://example.com/",ct,true);
                var actual=await browser.InspectPage(sid,ct);results.Add(Json.D("test","page-timezone-language","pass",Json.Str(actual,"tz")==exit.Timezone&&Json.Str(actual,"lang")==settings.Locale,"actual",actual));
                await browser.Screenshot(sid,Path.Combine(p.Install,"outputs","checks","browser-smoke.png"),ct);
                // A second newly created target must inherit timezone before navigation.
                string second=await browser.Open("https://example.com/?clean-network-new-tab",ct,false);var newer=await browser.InspectPage(second,ct);results.Add(Json.D("test","new-tab-inherits-timezone","pass",Json.Str(newer,"tz")==exit.Timezone));
                string before=core.Current;using(var measure=new ProxyService(new Paths(p.Install,Path.Combine(p.Data,"measurement"),true),settings)){await measure.Start(ct);await measure.Select(node,ct);double speed=await measure.Speed(ct);results.Add(Json.D("test","independent-speed-channel","pass",speed>0&&core.Current==before,"mbps",speed));}
                await browser.Close();
                windows=new BrowserSessions(p,settings,core);string other=String.IsNullOrEmpty(requestedOther)?names.First(n=>n!=node):requestedOther;if(!names.Contains(other))throw new Exception("第二个测试节点不在节点表中。");var firstSlot=await windows.Get(node,ct);var secondSlot=await windows.Get(other,ct);
                var exits=await Task.WhenAll(core.Exit(ct,core.SlotPorts[firstSlot.Index]),core.Exit(ct,core.SlotPorts[secondSlot.Index]));
                await firstSlot.Browser.SetTimezone(exits[0].Timezone,ct);await secondSlot.Browser.SetTimezone(exits[1].Timezone,ct);
                var opened=await Task.WhenAll(firstSlot.Browser.Open("https://example.com/",ct,true),secondSlot.Browser.Open("https://example.com/",ct,true));
                var actualSlots=await Task.WhenAll(firstSlot.Browser.InspectPage(opened[0],ct),secondSlot.Browser.InspectPage(opened[1],ct));
                results.Add(Json.D("test","parallel-windows-independent-exits","pass",exits[0].IP!=exits[1].IP&&windows.Count==2&&Json.Str(actualSlots[0],"tz")==exits[0].Timezone&&Json.Str(actualSlots[1],"tz")==exits[1].Timezone,"nodes",new[]{node,other},"ips",exits.Select(x=>x.IP).ToArray(),"zones",actualSlots.Select(x=>Json.Str(x,"tz")).ToArray()));
                results.Add(Json.D("test","window-title-and-page-node-labels","pass",Json.Str(actualSlots[0],"title").Contains(node)&&Json.Str(actualSlots[0],"marker").Contains(node)&&Json.Str(actualSlots[1],"title").Contains(other)&&Json.Str(actualSlots[1],"marker").Contains(other),"actual",actualSlots));
                // Switching the batch route must leave both pinned groups untouched.
                await core.Select(other,ct);var group=await core.Call("GET","/proxies/CB_SLOT_"+firstSlot.Index,null,ct);results.Add(Json.D("test","batch-route-keeps-window-node","pass",Json.Str(group,"now")==node));
                var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;try{await core.Delay(node,canceled.Token);}catch(OperationCanceledException){stopped=true;}canceled.Dispose();results.Add(Json.D("test","cancellation","pass",stopped));
                using(var h=System.Security.Cryptography.SHA256.Create())results.Add(Json.D("test","source-config-unchanged","pass",sourceHash==BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(core.SourcePath)))));
            }catch(Exception e){results.Add(Json.D("test","integration-flow","pass",false,"error",e.GetType().Name+": "+e.Message));}
            if(windows!=null)await windows.CloseAll();if(browser!=null){await browser.Close();browser.Dispose();}if(core!=null)core.Dispose();ctSource.Dispose();
            bool pass=results.All(x=>Json.Bool(Json.Obj(x),"pass"));Atomic.Write(Path.Combine(p.Install,"outputs","checks","integration-test.json"),Json.Write(Json.D("pass",pass,"tests",results,"time",DateTimeOffset.Now.ToString("o"))));return pass?0:3;
        }
        static async Task<int> TestReport(Paths p,Settings settings,string requested,string siteList){bool pass=false;string report="",error="";using(var source=new CancellationTokenSource(TimeSpan.FromMinutes(3)))using(var core=new ProxyService(p,settings)){try{var names=await core.Start(source.Token);string name=String.IsNullOrEmpty(requested)?names[0]:requested;if(!names.Contains(name))throw new Exception("报告测试节点不存在。");var nodes=new[]{new Node {Name=name}};report=await new QualityReport(p,settings,core).Run(nodes,siteList.Split('|').Where(Settings.ValidUrl).ToList(),true,s=>p.Log("report-progress",s),(a,b)=>{},source.Token);var data=Json.Read(File.ReadAllText(Path.Combine(Path.GetDirectoryName(report),"report.json"),Encoding.UTF8));var first=Json.Obj(Json.Arr(data["nodes"]).First());pass=File.Exists(report)&&Json.Num(first,"DelayMs",0)>0&&Json.Str(first,"IP")!=""&&Json.Arr(first["Sites"]).Any();}catch(Exception e){error=e.GetType().Name+": "+e.Message;}}Atomic.Write(Path.Combine(p.Install,"outputs","checks","report-test.json"),Json.Write(Json.D("pass",pass,"report",report,"error",error,"time",DateTimeOffset.Now.ToString("o"))));return pass?0:5;}
    }
}
