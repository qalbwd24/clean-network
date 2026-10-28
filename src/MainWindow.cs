using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CleanBrowser {
    public sealed class Controller {
        public Window Window {get;private set;}
        readonly Paths paths;readonly Settings settings;readonly bool demo,boot;
        readonly ObservableCollection<Node> nodes=new ObservableCollection<Node>();
        readonly ObservableCollection<Site> sites=new ObservableCollection<Site>();
        readonly ProxyService proxy;readonly BrowserService browser;
        readonly BrowserSessions windows;
        readonly DataGrid grid;ICollectionView view;
        CancellationTokenSource job;Task activeTask;bool busy,quitting,ready,trayHint;
        System.Windows.Forms.NotifyIcon tray;
        DispatcherTimer subscriptionTimer;string sourceStamp="";bool sourceQueued;
        string latestReport="";
        public Controller(Paths p,Settings s,bool preview,bool startup=false) {
            paths=p;settings=s;demo=preview;boot=startup;proxy=new ProxyService(p,s);browser=new BrowserService(new Paths(p.Install,Path.Combine(p.Data,"batch"),true),s,proxy);windows=new BrowserSessions(p,s,proxy);
            Window=(Window)XamlReader.Load(System.Xml.XmlReader.Create(Path.Combine(p.Install,"assets","Main.xaml")));
            Window.Icon=new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(p.Install,"assets","app.ico")));
            Window.SourceInitialized+=(sender,e)=>TaskbarIdentity.ConfigureWindow(new System.Windows.Interop.WindowInteropHelper(Window).Handle,p.Install,Window.Title);
            Find<Image>("AppLogo").Source=new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(p.Install,"assets","app.png")));
            Window.Width=Math.Max(940,Math.Min(settings.Width,SystemParameters.WorkArea.Width-30));Window.Height=Math.Max(720,Math.Min(settings.Height,SystemParameters.WorkArea.Height-35));
            grid=Find<DataGrid>("Nodes");grid.ItemsSource=nodes;view=CollectionViewSource.GetDefaultView(nodes);
            windows.QualityObserved+=(name,snapshot)=>Window.Dispatcher.BeginInvoke((Action)(()=>{var node=nodes.FirstOrDefault(n=>n.Name==name);if(node==null)return;if(snapshot.Success&&snapshot.ObservedAsCurrentExit)node.IP=snapshot.SourceIP;if(snapshot.Success&&!snapshot.ObservedAsCurrentExit&&snapshot.SourceIP!=node.IP)snapshot=new IpQualitySnapshot {State="出口 IP 不一致，未记分"};CleanIpResult.Apply(node,snapshot);Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));}));
            view.Filter=o=>MatchesNode((Node)o,Find<TextBox>("Search").Text,settings.FavoritesOnly);
            grid.SelectionChanged+=(a,e)=>{SelectionText();NodeDetails();};
            var rowStyle=new Style(typeof(DataGridRow),(Style)Window.FindResource(typeof(DataGridRow)));rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Error")));grid.RowStyle=rowStyle;
            ConfigureSearchInput();
            Find<TextBox>("Search").TextChanged+=(a,e)=>{view.Refresh();Counts();};
            Hook("AllNodes",()=>SetFavoritesOnly(false));Hook("FavoriteNodes",()=>SetFavoritesOnly(true));
            HookTask("QueryFavorites",QueryFavorites);HookTask("QuickQuery",()=>RunQuickQuery(Selected()));
            Hook("SelectVisible",()=>{foreach(Node n in view)n.Chosen=true;SelectionText();});
            Hook("ClearSelection",()=>{foreach(Node n in nodes)n.Chosen=false;grid.UnselectAll();SelectionText();});
            Hook("ZoomIn",()=>Zoom(.75));Hook("ZoomOut",()=>Zoom(-.75));Hook("FontLabel",()=>{settings.FontSize=12;ApplyFont();});
            Hook("Theme",()=>{settings.Theme=settings.Theme=="system"?"light":settings.Theme=="light"?"dark":"system";ApplyTheme();Save();});
            Hook("EditSites",EditSites);Hook("Settings",EditSettings);
            Hook("Reports",ShowReport);Hook("Export",Export);
            Find<CheckBox>("ReportDownload").IsChecked=settings.ReportDownload;Find<CheckBox>("ReportDownload").Click+=(a,e)=>{settings.ReportDownload=Find<CheckBox>("ReportDownload").IsChecked==true;Save();};
            string latest=Path.Combine(paths.Data,"latest-report.txt");if(File.Exists(latest))latestReport=File.ReadAllText(latest,Encoding.UTF8).Trim();
            Hook("Cancel",()=>{if(job!=null){job.Cancel();Status("正在停止，保留已完成的结果…");Find<Button>("Cancel").IsEnabled=false;}});
            HookTask("Reload",Reload);HookTask("Check",()=>RunReport(false));HookTask("CleanIp",()=>RunReport(true));HookTask("Delay",()=>DelaySelected(false));HookTask("DelayAll",()=>DelaySelected(true));HookTask("Speed",()=>Run("speed"));HookTask("Open",OpenSelected);HookTask("CloseBrowsers",CloseSelected);
            Window.PreviewMouseWheel+=(a,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0){Zoom(e.Delta>0?.75:-.75);e.Handled=true;}};
            Window.PreviewKeyDown+=(a,e)=>{
                if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;
                if(e.Key==Key.OemPlus||e.Key==Key.Add){Zoom(.75);e.Handled=true;}
                if(e.Key==Key.OemMinus||e.Key==Key.Subtract){Zoom(-.75);e.Handled=true;}
                if(e.Key==Key.D0||e.Key==Key.NumPad0){settings.FontSize=12;ApplyFont();e.Handled=true;}
                if(e.Key==Key.F){Find<TextBox>("Search").Focus();e.Handled=true;}
            };
            Window.Closing+=(a,e)=>{if(quitting)return;e.Cancel=true;Window.Hide();if(!trayHint&&tray!=null){tray.ShowBalloonTip(3500,"纯净网络检测工具","已收进托盘。双击图标恢复，右键可完全退出。",System.Windows.Forms.ToolTipIcon.Info);trayHint=true;}};
            Window.Loaded+=async(a,e)=>{if(demo)Demo();else if(boot)await Startup();else await Reload();};
            ApplyFont();ApplyTheme();BindSites();CreateTray();
            subscriptionTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(3)};subscriptionTimer.Tick+=async(a,e)=>await WatchSubscription();subscriptionTimer.Start();
        }
        T Find<T>(string name) where T:FrameworkElement {return (T)Window.FindName(name);}
        void Hook(string name,Action action){Find<Button>(name).Click+=(a,e)=>{try{action();}catch(Exception ex){Error(ex);}};}
        void HookTask(string name,Func<Task> action){Find<Button>(name).Click+=async(a,e)=>{if(busy)return;try{activeTask=action();await activeTask;}catch(Exception ex){Error(ex);}};}
        void Status(string message){Find<TextBlock>("Status").Text=message;Find<TextBlock>("Status").ToolTip=message;}
        void Error(Exception e){paths.Log("error",e.GetType().Name);Status(e is OperationCanceledException?"任务已停止。":e.Message);}
        void Save(){settings.Width=Window.ActualWidth>0?Window.ActualWidth:Window.Width;settings.Height=Window.ActualHeight>0?Window.ActualHeight:Window.Height;settings.Save(paths.Data);}
        public static bool MatchesNode(Node node,string search,bool favoritesOnly){return (!favoritesOnly||node.IsFavorite)&&node.Name.IndexOf((search??"").Trim(),StringComparison.CurrentCultureIgnoreCase)>=0;}
        void SetFavoritesOnly(bool value){settings.FavoritesOnly=value;Save();view.Refresh();Counts();}
        void Counts(){int count=view.Cast<Node>().Count();Find<TextBlock>("NodeCount").Text=count+" / "+nodes.Count+" 个";Find<Button>("FavoriteNodes").Content="★ 我的收藏 ("+nodes.Count(n=>n.IsFavorite)+")";foreach(string name in new[]{"AllNodes","FavoriteNodes"}){bool selected=(name=="FavoriteNodes")==settings.FavoritesOnly;var button=Find<Button>(name);button.SetResourceReference(Control.BackgroundProperty,selected?"AccentSoft":"Surface");button.SetResourceReference(Control.ForegroundProperty,selected?"Accent":"Ink");button.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;}var empty=Find<TextBlock>("EmptyNodes");empty.Visibility=count==0?Visibility.Visible:Visibility.Collapsed;empty.Text=settings.FavoritesOnly&&!nodes.Any(n=>n.IsFavorite)?"还没有收藏节点。切换到全部节点，点击名称旁的 ☆ 收藏。":String.IsNullOrWhiteSpace(Find<TextBox>("Search").Text)?"暂无可用节点，请刷新节点或检查配置。":"没有匹配的节点，请修改搜索词。";Find<Button>("QueryFavorites").IsEnabled=!busy&&ready&&nodes.Any(n=>MatchesNode(n,Find<TextBox>("Search").Text,true));SelectionText();}
        void SelectionText(){int n=view.Cast<Node>().Count(x=>x.Chosen);Find<TextBlock>("SelectionInfo").Text=n>0?"当前列表已勾选 "+n+" 个节点 · 可批量检测":"点击 ☆ 收藏 · 单击选一个 · 勾选可批量检测";}
        void SetBusy(bool value){busy=value;foreach(string name in new[]{"Reload","Check","CleanIp","Delay","DelayAll","Speed","Open","CloseBrowsers","Settings","EditSites","SelectVisible","ClearSelection","QueryFavorites","QuickQuery"})Find<Button>(name).IsEnabled=!value&&(ready||name=="Reload"||name=="Settings"||name=="EditSites");Find<Button>("QueryFavorites").IsEnabled=!value&&ready&&nodes.Any(n=>MatchesNode(n,Find<TextBox>("Search").Text,true));grid.IsEnabled=!value;Find<ItemsControl>("Sites").IsEnabled=!value;Find<CheckBox>("ReportDownload").IsEnabled=!value;Find<Button>("Cancel").IsEnabled=value;Find<ProgressBar>("Progress").IsIndeterminate=value;Find<TextBlock>("WindowCount").Text="测试窗口 "+windows.Count+" / 4 · 各自固定出口";}
        void Progress(int done,int total){var p=Find<ProgressBar>("Progress");p.IsIndeterminate=false;p.Value=total==0?0:100.0*done/total;}
        void Zoom(double amount){settings.FontSize=Math.Max(9,Math.Min(22,settings.FontSize+amount));ApplyFont();}
        void ApplyFont(){ApplyTextSize(Window,settings.FontSize);Save();}
        public static void ApplyTextSize(Window window,double size){double dip=size*96/72;window.Resources["BodySize"]=dip;((Button)window.FindName("FontLabel")).Content=size.ToString("0.##")+" pt";var grid=(DataGrid)window.FindName("Nodes");grid.Columns[1].MinWidth=Math.Max(180,dip*10);foreach(var column in grid.Columns.Skip(2)){double width=Math.Max(Convert.ToString(column.Header).Contains("风险")?140:110,dip*6);column.MinWidth=width;column.Width=width;}}
        void ApplyTheme(){bool dark=settings.Theme=="dark";if(settings.Theme=="system")try{dark=Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))==0;}catch{}
            var values=dark?new[]{"#111723","#1A2231","#202B3D","#E8EEF8","#A0AEC2","#344158","#698AFF","#263A64","#29364B","#58C8AE","#FF9A9F"}:new[]{"#F3F5FA","#FFFFFF","#F8FAFC","#172033","#64748B","#DDE3EE","#365FEA","#EAF0FF","#EDF1F8","#168575","#C24F58"};
            var keys=new[]{"Canvas","Surface","Field","Ink","Muted","Line","Accent","AccentSoft","Hover","Good","Danger"};for(int i=0;i<keys.Length;i++)Window.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
            Find<Button>("Theme").Content="外观："+(settings.Theme=="system"?"跟随系统":dark?"深色":"浅色");
        }
        void BindSites(){sites.Clear();foreach(var site in settings.Sites){sites.Add(site);site.PropertyChanged-=SiteChanged;site.PropertyChanged+=SiteChanged;}Find<ItemsControl>("Sites").ItemsSource=sites;}
        void SiteChanged(object sender,PropertyChangedEventArgs e){Save();}
        public void Show(){Window.Show();if(Window.WindowState==WindowState.Minimized)Window.WindowState=WindowState.Normal;Window.Activate();}
        void ConfigureSearchInput(){
            var search=Find<TextBox>("Search");
            InputMethod.SetIsInputMethodEnabled(search,true);
            InputMethod.SetPreferredImeState(search,InputMethodState.On);
            InputMethod.SetPreferredImeConversionMode(search,ImeConversionModeValues.Native);
            var chinese=InputLanguageManager.Current.AvailableInputLanguages.Cast<System.Globalization.CultureInfo>().FirstOrDefault(language=>language.TwoLetterISOLanguageName=="zh");
            if(chinese==null)return;
            InputLanguageManager.SetInputLanguage(search,chinese);
            InputLanguageManager.SetRestoreInputLanguage(search,true);
            search.GotKeyboardFocus+=(sender,args)=>search.Dispatcher.BeginInvoke(DispatcherPriority.Input,(Action)(()=>{
                if(!search.IsKeyboardFocused)return;
                try{if(InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName!="zh")InputLanguageManager.Current.CurrentInputLanguage=chinese;InputMethod.Current.ImeState=InputMethodState.On;InputMethod.Current.ImeConversionMode=ImeConversionModeValues.Native;}
                catch(Exception ex){paths.Log("search-ime",ex.GetType().Name);}
            }));
        }
        void CreateTray(){
            tray=new System.Windows.Forms.NotifyIcon {Text="纯净网络检测工具",Icon=new System.Drawing.Icon(Path.Combine(paths.Install,"assets","app.ico")),Visible=true};
            tray.DoubleClick+=(a,e)=>Window.Dispatcher.BeginInvoke((Action)Show);
            var menu=new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示窗口",null,(a,e)=>Window.Dispatcher.BeginInvoke((Action)Show));
            var startup=new System.Windows.Forms.ToolStripMenuItem("开机自启") {Checked=AutoStart.Enabled,CheckOnClick=false,Enabled=!demo};
            startup.Click+=(a,e)=>Window.Dispatcher.BeginInvoke((Action)(()=>{
                try{AutoStart.Set(!AutoStart.Enabled,paths.Install);startup.Checked=AutoStart.Enabled;Status(startup.Checked?"已开启开机自启：等待 Clash 接通后检查出口。":"已关闭开机自启。");}
                catch(Exception ex){Error(ex);}
            }));
            menu.Opening+=(a,e)=>{try{startup.Checked=AutoStart.Enabled;startup.Enabled=!demo;}catch(Exception ex){startup.Enabled=false;paths.Log("autostart-menu",ex.GetType().Name);}};
            menu.Items.Add(startup);menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出（关闭本工具浏览器）",null,(a,e)=>Window.Dispatcher.BeginInvoke((Action)(async()=>await Quit())));
            tray.ContextMenuStrip=menu;
        }
        public async Task Quit(){if(quitting)return;quitting=true;subscriptionTimer.Stop();if(job!=null)job.Cancel();Status("正在关闭本工具的浏览器与内核…");if(activeTask!=null)await Task.WhenAny(activeTask,Task.Delay(6000));await windows.CloseAll();await browser.Close();proxy.Dispose();Save();tray.Visible=false;tray.Dispose();Window.Close();Application.Current.Shutdown();}
        void Demo(){foreach(string name in new[]{"🇺🇸 美国 01 家宽","🇺🇸 美国 02 家宽","🇭🇰 香港 01","🇯🇵 日本 01","🇨🇦 加拿大 01 家宽","🇸🇬 新加坡 01"}){var node=new Node{Name=name,IsFavorite=settings.FavoriteNodes.Contains(name)};node.PropertyChanged+=NodeChanged;nodes.Add(node);}nodes[0].Delay="286 ms";nodes[0].Speed="24.8";nodes[0].Status="示例数据";nodes[1].Delay="312 ms";nodes[1].Status="示例数据";ready=true;SetBusy(false);Counts();Status("界面预览 · 示例数据，不启动代理或浏览器");Find<TextBlock>("Source").Text="预览模式 · 2.0";}
        async Task Reload(){if(demo){Status("预览模式不读取真实节点。");return;}await windows.Prune();if(windows.Count>0){Status("请先关闭测试窗口再刷新节点，避免中断正在检测的出口。");return;}job=new CancellationTokenSource();SetBusy(true);Status("正在读取 Clash 节点…");try{
                var names=ready?await proxy.Reload(job.Token):await proxy.Start(job.Token);var previous=nodes.ToDictionary(x=>x.Name,x=>x);if(previous.Count==0){string saved=Path.Combine(paths.Data,"results.json");if(File.Exists(saved))try{var stored=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Node[]>(File.ReadAllText(saved,Encoding.UTF8));foreach(var n in stored){n.Chosen=false;if(n.Status=="窗口已打开")n.Status="上次结果";previous[n.Name]=n;}}catch{paths.Log("results-load","saved results could not be read");}}nodes.Clear();
                foreach(string name in names){Node n;if(!previous.TryGetValue(name,out n))n=new Node{Name=name};n.PropertyChanged-=NodeChanged;n.IsFavorite=settings.FavoriteNodes.Contains(name);n.PropertyChanged+=NodeChanged;nodes.Add(n);}ready=true;Counts();
                if(!String.IsNullOrEmpty(settings.LastNode))grid.SelectedItem=nodes.FirstOrDefault(n=>n.Name==settings.LastNode);
                sourceStamp=SourceStamp();sourceQueued=false;Find<TextBlock>("Source").Text=proxy.Source+" · 自动跟随订阅 · "+nodes.Count+" 节点";Status("已就绪。单击或勾选节点，打开窗口或测延迟。");
            }catch(Exception e){Error(e);}finally{SetBusy(false);job.Dispose();job=null;}}
        void NodeChanged(object sender,PropertyChangedEventArgs e){if(e.PropertyName=="IsFavorite"){var node=(Node)sender;var previous=settings.FavoriteNodes.ToList();settings.FavoriteNodes.RemoveAll(name=>name==node.Name);if(node.IsFavorite)settings.FavoriteNodes.Add(node.Name);try{Save();Status(node.IsFavorite?"已收藏："+node.Name+"。首页点击“我的收藏”即可查找。":"已取消收藏："+node.Name);}catch(Exception ex){settings.FavoriteNodes=previous;node.PropertyChanged-=NodeChanged;node.IsFavorite=previous.Contains(node.Name);node.PropertyChanged+=NodeChanged;Error(ex);}Window.Dispatcher.BeginInvoke((Action)(()=>{view.Refresh();Counts();}));}if(e.PropertyName=="Chosen")SelectionText();if(grid.SelectedItem==sender)NodeDetails();}
        void NodeDetails(){var n=grid.SelectedItem as Node;Find<FrameworkElement>("QuickMetrics").DataContext=n;if(n!=null){Find<TextBlock>("CurrentNode").Text=n.Name;Find<TextBlock>("CurrentIP").Text=n.IP;Find<TextBlock>("CurrentZone").Text=String.IsNullOrEmpty(n.Place)?"地区待查询":n.Place;}Find<TextBlock>("QuickNode").Text=n==null?"选择节点查看查询结果":n.Name;Find<TextBlock>("NodeDetails").Text=n==null?"选择节点查看出口、历史评分和风险原因。":"出口 "+n.IP+" · 纯净度（高好） "+n.PurityDisplay+" · 风险（低好） "+n.RiskDisplay+"\n风险提示："+(String.IsNullOrEmpty(n.CleanIpFlags)?"暂无有效风险记录":n.CleanIpFlags)+"\n评分时间："+(n.CleanIpCheckedAt??"未检测")+" · 本次："+(n.CleanIpState??"未检测")+(String.IsNullOrEmpty(n.CleanIpSourceIP)?"":" · 评分来源 IP "+n.CleanIpSourceIP);Find<TextBlock>("NodeDetails").ToolTip=Find<TextBlock>("NodeDetails").Text;Find<TextBlock>("NodeDetails").Text=Find<TextBlock>("NodeDetails").Text.Replace("\n"," · ");}
        string SourceStamp(){if(String.IsNullOrEmpty(proxy.SourcePath)||!File.Exists(proxy.SourcePath))return "";var file=new FileInfo(proxy.SourcePath);var signature=new StringBuilder(file.LastWriteTimeUtc.Ticks+":"+file.Length);string dir=Path.Combine(Path.GetDirectoryName(proxy.SourcePath),"profiles");if(Directory.Exists(dir))foreach(var f in new DirectoryInfo(dir).EnumerateFiles("*.yaml"))signature.Append("|"+f.Name+":"+f.LastWriteTimeUtc.Ticks+":"+f.Length);return signature.ToString();}
        async Task WatchSubscription(){if(demo||!ready||quitting||busy)return;try{await windows.Prune();Find<TextBlock>("WindowCount").Text="测试窗口 "+windows.Count+" / 4 · 各自固定出口";foreach(var n in nodes)if(n.Status=="窗口已打开"&&!windows.IsOpen(n.Name))n.Status="窗口已关闭";string now=SourceStamp();if(now==sourceStamp&&!sourceQueued)return;if(windows.Count>0){sourceQueued=true;Status("检测到 Clash 订阅更新；关闭测试窗口后自动载入新节点。");return;}await Reload();}catch(Exception e){paths.Log("subscription-watch",e.GetType().Name);}}
        List<Node> Selected(){var visible=view.Cast<Node>().ToList();var chosen=visible.Where(x=>x.Chosen).ToList();if(chosen.Count>0)return chosen;return grid.SelectedItems.Cast<Node>().Where(visible.Contains).ToList();}
        async Task QueryFavorites(){var favorites=nodes.Where(n=>MatchesNode(n,Find<TextBox>("Search").Text,true)).ToList();SetFavoritesOnly(true);if(favorites.Count==0){Status("没有可查询的收藏节点。请先收藏节点或修改搜索词。");return;}await RunQuickQuery(favorites);}
        async Task RunQuickQuery(List<Node> chosen){
            if(demo){Status("预览模式不进行真实查询或测速。");return;}
            if(chosen.Count==0){Status("请选择节点，或点击查询收藏。");return;}
            job=new CancellationTokenSource();SetBusy(true);try{
                await new QuickQuery(paths,settings,proxy).Run(chosen,n=>{grid.SelectedItem=n;grid.ScrollIntoView(n);NodeDetails();},Status,Progress,job.Token);
                Status("快捷查询完成。分数、IP 风险项、人机比例和本次平均上传 / 下载已显示在首页。");
            }catch(OperationCanceledException){Status("查询已停止，已取得的结果已保留。");}catch(Exception ex){Error(ex);}
            finally{try{Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));NodeDetails();}finally{SetBusy(false);job.Dispose();job=null;}}
        }
        async Task RunReport(bool cleanOnly,List<Node> requested=null){if(demo){Status("预览模式不生成网络报告。");return;}var chosen=requested??Selected();var urls=cleanOnly?new List<string>{"https://cleanip.io/"}:sites.Where(s=>s.Enabled).Select(s=>s.Url).ToList();bool download=!cleanOnly&&settings.ReportDownload;if(chosen.Count==0){Status("请选择要检测的节点。");return;}if(chosen.Count>12&&download&&MessageBox.Show(Window,"将检测 "+chosen.Count+" 个节点，下载测速最多使用 "+(chosen.Count*10)+" MB。可取消后关闭包含下载测速选项。继续？","批量报告",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            job=new CancellationTokenSource();SetBusy(true);try{var runner=new QualityReport(paths,settings,proxy);latestReport=await runner.Run(chosen,urls,download,Status,Progress,job.Token);Atomic.Write(Path.Combine(paths.Data,"latest-report.txt"),latestReport);Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));NodeDetails();Status(job.IsCancellationRequested?"已停止，部分报告已保存。点击查看报告。":"检测结果已保留。表格显示 CleanIP 与风险分，选中节点查看风险原因和时间。");}catch(Exception e){Error(e);}finally{SetBusy(false);job.Dispose();job=null;}}
        void ShowReport(){if(!String.IsNullOrEmpty(latestReport)&&Path.GetFullPath(latestReport).StartsWith(Path.GetFullPath(paths.Reports)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&latestReport.EndsWith(".html",StringComparison.OrdinalIgnoreCase)&&File.Exists(latestReport)){Process.Start(new ProcessStartInfo(latestReport){UseShellExecute=true});}else Reveal(paths.Reports);}
        async Task CloseSelected(){var selected=Selected();if(selected.Count==0){Status("先选择要关闭窗口的节点。");return;}SetBusy(true);try{foreach(var n in selected){await windows.Close(n.Name);n.Status="窗口已关闭";}}finally{SetBusy(false);}Status("已关闭所选节点的测试窗口。");}
        async Task DelaySelected(bool all){if(demo){Status("预览模式不进行网络测试。");return;}var chosen=all?nodes.ToList():Selected();if(chosen.Count==0){Status("先选择节点，或点击全部测延迟。");return;}job=new CancellationTokenSource();var ct=job.Token;SetBusy(true);int done=0,failed=0;var limit=new SemaphoreSlim(6,6);
            try{await Task.WhenAll(chosen.Select(async n=>{await limit.WaitAsync(ct);try{ct.ThrowIfCancellationRequested();n.Status="测延迟中";n.Error="";try{int ms=await proxy.Delay(n.Name,ct);n.Delay=ms+" ms";n.DelayValue=ms;n.Status="延迟已测";}catch(OperationCanceledException){n.Status="已停止";throw;}catch(Exception e){n.Status="超时 / 不通";n.Delay="超时";n.DelayValue=Double.MaxValue;n.Error=e.Message;failed++;}n.TestedAt=DateTimeOffset.Now.ToString("o");done++;Progress(done,chosen.Count);Status("测延迟 "+done+"/"+chosen.Count+" · 失败 "+failed);}finally{limit.Release();}}));}
            catch(OperationCanceledException){Status("已停止 · 完成 "+done+"/"+chosen.Count);}finally{Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));SetBusy(false);Progress(done,chosen.Count);job.Dispose();job=null;limit.Dispose();}
            if(done==chosen.Count)Status("延迟测试完成 · "+done+" 个节点 · "+failed+" 个超时或不通");}
        async Task OpenSelected(){
            if(demo){Status("预览模式不启动浏览器。");return;}var chosen=Selected();var urls=sites.Where(x=>x.Enabled).Select(x=>x.Url).ToArray();if(chosen.Count==0||urls.Length==0){Status("先选择节点，并勾选要打开的网站。");return;}await windows.Prune();
            if(windows.Names.Union(chosen.Select(n=>n.Name)).Count()>4){Status("同时最多打开 4 个节点。先关闭不用的窗口，或使用批量检测并截图。");return;}
            job=new CancellationTokenSource();var ct=job.Token;SetBusy(true);int done=0,failed=0;bool canceled=false;
            try{await Task.WhenAll(chosen.Select(async n=>{n.Status="启动窗口…";n.Error="";try{
                    var slot=await windows.Get(n.Name,ct);Status("正在打开独立窗口 · "+n.Name);
                    var launch=slot.Browser.Ensure(ct);var lookup=proxy.Exit(ct,proxy.SlotPorts[slot.Index]);ExitInfo exit=null;string warning="";
                    try{exit=await lookup;}catch(OperationCanceledException){throw;}catch(Exception e){warning=e.Message;}
                    await launch;await slot.Browser.SetTimezone(exit==null?"":exit.Timezone,ct);slot.Exit=exit;
                    if(exit!=null){n.IP=exit.IP;n.Place=(exit.Country+" "+exit.City).Trim();}else{n.IP="—";n.Place="";}
                    int errors=0;foreach(string url in urls)try{await slot.Browser.Open(url,ct,false);}catch(OperationCanceledException){throw;}catch(Exception e){errors++;n.Error=e.Message;}
                    n.Status=errors>0?"部分站点失败":"窗口已打开";if(errors>0)failed++;if(warning!="")n.Error=warning;
                    Find<TextBlock>("CurrentNode").Text=n.Name;Find<TextBlock>("CurrentIP").Text=n.IP;Find<TextBlock>("CurrentZone").Text=exit==null?"时区未确认":n.Place+"\n"+(settings.FollowTimezone?exit.Timezone:"跟随系统");settings.LastNode=n.Name;n.TestedAt=DateTimeOffset.Now.ToString("o");
                }catch(OperationCanceledException){n.Status="已停止";throw;}catch(Exception e){n.Status="打开失败";n.Error=e.Message;failed++;}finally{done++;Progress(done,chosen.Count);}}));}
            catch(OperationCanceledException){canceled=true;Status("打开操作已停止，已打开的窗口保留。");}finally{Save();Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));SetBusy(false);job.Dispose();job=null;}
            if(!canceled&&done==chosen.Count)Status("已处理 "+done+" 个节点 · "+windows.Count+" 个独立窗口"+(failed>0?" · "+failed+" 项失败":""));
        }
        async Task Startup(){job=new CancellationTokenSource();SetBusy(true);Window.Hide();int port=0;try{Status("开机检查：等待 Clash 连接…");port=await AutoStart.WaitForClash(job.Token);}catch(Exception e){Error(e);Show();}finally{SetBusy(false);job.Dispose();job=null;}if(port==0)return;await Reload();if(!ready)return;Status("Clash 已接通，检查当前出口…");try{var exit=await proxy.Exit(CancellationToken.None,port);Find<TextBlock>("CurrentNode").Text="Clash 当前出口";Find<TextBlock>("CurrentIP").Text=exit.IP;Find<TextBlock>("CurrentZone").Text=exit.Country+" "+exit.City+"\n"+exit.Timezone;Atomic.Write(Path.Combine(paths.Install,"outputs","checks","startup-latest.json"),Json.Write(Json.D("time",DateTimeOffset.Now.ToString("o"),"ready",true,"ip",exit.IP,"timezone",exit.Timezone)));Status("开机检查完成 · Clash 连通，出口信息已保存。");tray.ShowBalloonTip(4000,"开机节点检查完成",exit.Country+" "+exit.City+" · "+exit.Timezone,System.Windows.Forms.ToolTipIcon.Info);}catch(Exception e){Error(e);Show();}}
        async Task Run(string mode){
            if(demo){Status("这是界面预览，请启动正式程序进行检测。");return;}
            var chosen=Selected();var urls=sites.Where(x=>x.Enabled).Select(x=>x.Url).ToList();
            if(mode!="open"&&chosen.Count==0){Status("请先选择或勾选节点。");return;}
            if((mode=="check"||mode=="batch")&&urls.Count==0){Status("请先勾选至少一个检测站点。");return;}
            if(mode=="open")chosen=new List<Node>{nodes.First(n=>n.Name==proxy.Current)};
            bool batch=mode=="batch";
            string folder=batch?Path.Combine(paths.Reports,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")):null;
            var report=new List<object>();int done=0,failed=0,siteFailures=0;bool canceled=false;
            job=new CancellationTokenSource();CancellationToken ct=job.Token;SetBusy(true);
            ProxyService measure=null;
            try {
                if(mode=="speed"){Status("准备独立测速通道，浏览器出口保持原样…");measure=new ProxyService(new Paths(paths.Install,Path.Combine(paths.Data,"probe"),true),settings);await measure.Start(ct);}
                for(int i=0;i<chosen.Count;i++){
                    ct.ThrowIfCancellationRequested();Node n=chosen[i];n.Error="";n.Status="进行中";Status((i+1)+"/"+chosen.Count+" · "+n.Name);
                    var siteRecords=new List<object>();string issue="";ExitInfo exit=null;bool nodeFailed=false;
                    try {
                        if(mode=="delay"){int ms=await proxy.Delay(n.Name,ct);n.DelayValue=ms;n.Delay=ms+" ms";n.Status="延迟已测";}
                        else if(mode=="speed") {await measure.Select(n.Name,ct);Status("下载测速约 4 秒 · "+n.Name);double speed=await measure.Speed(ct);n.SpeedValue=speed;n.Speed=speed.ToString("0.0");n.Status="下载已测";}
                        else {
                            if(mode=="batch"){await proxy.Select(n.Name,ct);settings.LastNode=n.Name;Save();Find<TextBlock>("CurrentNode").Text=n.Name;Find<TextBlock>("CurrentIP").Text="查询中…";Find<TextBlock>("CurrentZone").Text="正在匹配页面时区";
                                n.IP="—";n.Place="";try{exit=await proxy.Exit(ct);n.IP=exit.IP;n.Place=(exit.Country+" "+exit.City).Trim();}catch(OperationCanceledException){throw;}catch(Exception e){issue=e.Message;}
                                await browser.SetTimezone(exit==null?"":exit.Timezone,ct);
                                Find<TextBlock>("CurrentIP").Text=exit==null?"查询失败":exit.IP;Find<TextBlock>("CurrentZone").Text=exit==null?"时区未确认":n.Place+"\n"+(settings.FollowTimezone?exit.Timezone:"跟随系统");
                            }
                            int bad=0;
                            for(int j=0;j<urls.Count;j++){
                                ct.ThrowIfCancellationRequested();string url=urls[j],shot="",error="";
                                try {Status((i+1)+"/"+chosen.Count+" · 打开 "+new Uri(url).Host);string sid=await browser.Open(url,ct,batch);
                                    if(batch){shot=(i+1).ToString("D3")+"-"+(j+1).ToString("D2")+".png";await browser.Screenshot(sid,Path.Combine(folder,shot),ct);}
                                }catch(OperationCanceledException){throw;}catch(Exception e){error=e.Message;bad++;siteFailures++;n.Error=error;}
                                siteRecords.Add(Json.D("url",url,"status",error==""?(batch?"已截图":"已打开"):"失败","screenshot",shot,"error",error));
                            }
                            if(bad>0){n.Status=bad==urls.Count?"站点打开失败":"部分站点失败";nodeFailed=true;}else n.Status=batch?"已截图":issue==""?"已打开":"已打开 · 时区待查";
                            if(issue!="")n.Error=issue;
                        }
                        n.TestedAt=DateTimeOffset.Now.ToString("o");
                    }catch(OperationCanceledException){n.Status="已停止";throw;}catch(Exception e){n.Status="失败";n.Error=e.Message;nodeFailed=true;}
                    finally {report.Add(Json.D("node",n.Name,"ip",n.IP,"place",n.Place,"timezone",exit==null?"":exit.Timezone,"status",n.Status,"error",n.Error??"","sites",siteRecords));}
                    if(nodeFailed)failed++;done++;Progress(done,chosen.Count);
                }
            }catch(OperationCanceledException){canceled=true;}catch(Exception e){failed++;Error(e);}
            finally {
                if(measure!=null)measure.Dispose();
                if(batch){Directory.CreateDirectory(folder);SaveReport(folder,report,canceled,chosen.Count);}
                Atomic.Write(Path.Combine(paths.Data,"results.json"),Json.Write(nodes.ToArray()));
                string label=canceled?"已停止":"任务结束";Status(label+" · 完成 "+done+"/"+chosen.Count+(failed>0?" · "+failed+" 个节点失败":"")+(siteFailures>0?" · "+siteFailures+" 个站点失败":"")+(batch?" · 报告已保存":""));
                SetBusy(false);Progress(done,chosen.Count);job.Dispose();job=null;
            }
            if(batch)await browser.Close();
        }
        void SaveReport(string folder,List<object> rows,bool canceled,int requested){
            Atomic.Write(Path.Combine(folder,"report.json"),Json.Write(Json.D("generatedAt",DateTimeOffset.Now.ToString("o"),"canceled",canceled,"requestedNodes",requested,"nodes",rows)));
            var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>节点检测报告</title><style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:20px;background:#f3f5fa;color:#172033}section{background:white;border:1px solid #dde3ee;border-radius:12px;padding:20px;margin:18px 0}img{max-width:100%;border:1px solid #dde3ee}a{color:#365fea}small{color:#64748b}</style><h1>节点检测报告</h1><p>"+(canceled?"任务已中止，以下为已完成部分。":"检测任务结束。")+" 网页截图保留网站实际显示，包括验证页和错误页。</p>");
            foreach(var item in rows){var r=Json.Obj(item);html.Append("<section><h2>"+WebUtility.HtmlEncode(Json.Str(r,"node"))+"</h2><p>"+WebUtility.HtmlEncode(Json.Str(r,"status")+" · "+Json.Str(r,"ip")+" · "+Json.Str(r,"place"))+"</p>");foreach(var s in Json.Arr(r["sites"])){var site=Json.Obj(s);html.Append("<p>"+WebUtility.HtmlEncode(Json.Str(site,"url")+" — "+Json.Str(site,"status")+" "+Json.Str(site,"error"))+"</p>");if(Json.Str(site,"screenshot")!="")html.Append("<a href='"+Json.Str(site,"screenshot")+"'><img loading='lazy' src='"+Json.Str(site,"screenshot")+"'></a>");}html.Append("</section>");}
            Atomic.Write(Path.Combine(folder,"index.html"),html.ToString());
        }
        static string Cell(string s){s=s??"";if(s.Length>0&&"=+-@\t\r".IndexOf(s[0])>=0)s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";}
        void Export(){string file=Path.Combine(paths.Reports,"节点结果-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".csv");var lines=new List<string>{"节点,延迟毫秒,下载Mbps,出口IP,地区,状态,检测时间,CleanIP纯净度,风险分,风险原因,评分读取时间,评分来源IP,本次评分状态,错误"};foreach(Node n in nodes)lines.Add(String.Join(",",new[]{n.Name,n.Delay,n.Speed,n.IP,n.Place,n.Status,n.TestedAt,n.PurityDisplay,n.RiskDisplay,n.CleanIpFlags,n.CleanIpCheckedAt,n.CleanIpSourceIP,n.CleanIpState,n.Error}.Select(Cell)));File.WriteAllLines(file,lines,new UTF8Encoding(true));Status("已导出："+file);}
        void Reveal(string path){Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\""){UseShellExecute=true});}
        Window Dialog(string title,int width,int height,out StackPanel panel){var w=new Window {Title=title,Width=width,Height=height,MinWidth=450,MinHeight=350,Owner=Window,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=Window.FontFamily,FontSize=16,Background=(Brush)Window.Resources["Surface"],Foreground=(Brush)Window.Resources["Ink"]};w.Resources.MergedDictionaries.Add(Window.Resources);panel=new StackPanel {Margin=new Thickness(24)};w.Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};return w;}
        void EditSites(){StackPanel panel;var dialog=Dialog("编辑检测站点",640,560,out panel);panel.Children.Add(new TextBlock {Text="每行一个网址",FontSize=22,FontWeight=FontWeights.SemiBold});panel.Children.Add(new TextBlock {Text="使用完整的 https:// 或 http:// 地址。已有站点保留勾选状态。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,16)});
            var text=new TextBox {Text=String.Join(Environment.NewLine,sites.Select(x=>x.Url)),AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=250};panel.Children.Add(text);
            var save=new Button {Content="保存站点",Margin=new Thickness(0,18,0,0)};panel.Children.Add(save);save.Click+=(a,e)=>{var urls=text.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x!="").Distinct().ToList();if(urls.Count==0||urls.Any(x=>!Settings.ValidUrl(x))){MessageBox.Show(dialog,"请至少填写一个有效的 http 或 https 网址。","网址有误");return;}var old=sites.ToDictionary(x=>x.Url,x=>x.Enabled);settings.Sites=urls.Select(x=>new Site {Url=x,Enabled=!old.ContainsKey(x)||old[x]}).ToList();BindSites();Save();dialog.Close();};dialog.ShowDialog();}
        void EditSettings(){StackPanel panel;var dialog=Dialog("设置",650,680,out panel);panel.Children.Add(new TextBlock {Text="工具设置",FontSize=22,FontWeight=FontWeights.SemiBold});
            var source=Field(panel,"节点配置（留空自动读取 Clash Verge）",settings.NodeSource);var browse=new Button {Content="选择 YAML 文件",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,0)};panel.Children.Add(browse);browse.Click+=(a,e)=>{var d=new OpenFileDialog {Filter="Clash 配置|*.yaml;*.yml|所有文件|*.*"};if(d.ShowDialog(dialog)==true)source.Text=d.FileName;};
            var chrome=Field(panel,"Chrome 程序路径（留空自动查找）",settings.ChromePath);
            var locale=Field(panel,"浏览器语言",settings.Locale);
            var latency=Field(panel,"延迟测试地址",settings.LatencyUrl);
            var settle=Field(panel,"批量截图等待时间（毫秒）",settings.SettleMs.ToString());
            var timezone=new CheckBox {Content="页面时区跟随节点出口，不修改 Windows 时区",IsChecked=settings.FollowTimezone,Margin=new Thickness(0,18,0,12)};panel.Children.Add(timezone);
            var startup=new CheckBox {Content="开机自启：等待 Clash 接通后检查当前出口",IsChecked=AutoStart.Enabled,Margin=new Thickness(0,3,0,14)};panel.Children.Add(startup);
            panel.Children.Add(new TextBlock {Text="路径与语言在下次打开浏览器时使用。更换配置后点击刷新节点。",TextWrapping=TextWrapping.Wrap,FontSize=13,Foreground=(Brush)Window.Resources["Muted"]});
            var save=new Button {Content="保存设置",Margin=new Thickness(0,18,0,0)};panel.Children.Add(save);save.Click+=(a,e)=>{int ms;if(!Int32.TryParse(settle.Text,out ms)||ms<500||ms>15000||!Settings.ValidUrl(latency.Text.Trim())||!System.Text.RegularExpressions.Regex.IsMatch(locale.Text,@"^[a-zA-Z]{2,8}(-[a-zA-Z0-9]{1,8})*$")||(!String.IsNullOrWhiteSpace(source.Text)&&!File.Exists(source.Text))||(!String.IsNullOrWhiteSpace(chrome.Text)&&!File.Exists(chrome.Text))){MessageBox.Show(dialog,"请检查路径、测试网址、语言，以及等待时间（500–15000）。","设置有误");return;}settings.NodeSource=source.Text.Trim();settings.ChromePath=chrome.Text.Trim();settings.Locale=locale.Text.Trim();settings.LatencyUrl=latency.Text.Trim();settings.SettleMs=ms;settings.FollowTimezone=timezone.IsChecked==true;AutoStart.Set(startup.IsChecked==true,paths.Install);Save();dialog.Close();Status("设置已保存。更换节点配置后请点击刷新节点。");};dialog.ShowDialog();}
        TextBox Field(StackPanel panel,string label,string value){panel.Children.Add(new TextBlock {Text=label,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,18,0,7),FontSize=14});var box=new TextBox {Text=value};panel.Children.Add(box);return box;}
    }
}
