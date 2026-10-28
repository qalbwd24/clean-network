using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Web.Script.Serialization;

namespace CleanBrowser {
    public static class Json {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024 }.Serialize(value); }
        public static Dictionary<string,object> Read(string text) { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024 }.Deserialize<Dictionary<string,object>>(text); }
        public static Dictionary<string,object> Obj(object value) { return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
        public static string Str(IDictionary<string,object> o,string key,string fallback="") { object v; return o.TryGetValue(key,out v) && v!=null ? Convert.ToString(v) : fallback; }
        public static bool Bool(IDictionary<string,object> o,string key,bool fallback=false) { object v; bool b; return o.TryGetValue(key,out v) && Boolean.TryParse(Convert.ToString(v),out b) ? b : fallback; }
        public static double Num(IDictionary<string,object> o,string key,double fallback) { object v; double n; return o.TryGetValue(key,out v) && Double.TryParse(Convert.ToString(v),out n) ? n : fallback; }
        public static IEnumerable<object> Arr(object value) { return (value as IEnumerable ?? new object[0]).Cast<object>(); }
        public static Dictionary<string,object> D(params object[] pairs) { var d=new Dictionary<string,object>(); for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d; }
    }
    public class Notify : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;
        public void Changed(string name) { if(PropertyChanged!=null)PropertyChanged(this,new PropertyChangedEventArgs(name)); }
    }
    public class Site : Notify {
        public string Url {get;set;}
        bool enabled;
        public bool Enabled {get{return enabled;}set{enabled=value;Changed("Enabled");}}
        public string Host {get { Uri u;return Uri.TryCreate(Url,UriKind.Absolute,out u)?u.Host:Url; }}
    }
    public class Node : Notify {
        public string Name {get;set;}
        bool favorite;
        public bool IsFavorite {get{return favorite;}set{if(favorite==value)return;favorite=value;Changed("IsFavorite");Changed("FavoriteHint");}}
        public string FavoriteHint {get{return (IsFavorite?"取消收藏：":"收藏节点：")+Name;}}
        bool chosen; string delay="—",speed="—",ip="—",status="未检测",place="",time="";
        public bool Chosen {get{return chosen;}set{chosen=value;Changed("Chosen");}}
        public string Delay {get{return delay;}set{delay=value;Changed("Delay");}}
        public string Speed {get{return speed;}set{speed=value;Changed("Speed");}}
        public string IP {get{return ip;}set{ip=value;if(!String.IsNullOrEmpty(CleanIpSourceIP)&&value!=CleanIpSourceIP){CleanIpLatestSucceeded=false;CleanIpState="出口已变化，显示历史";NotifyQuality();}Changed("IP");NotifySpeed();}}
        public string Status {get{return status;}set{status=value;Changed("Status");}}
        public string Place {get{return place;}set{place=value;Changed("Place");}}
        public string TestedAt {get{return time;}set{time=value;Changed("TestedAt");}}
        public double DelayValue {get;set;}
        public double SpeedValue {get;set;}
        public string Error {get;set;}
        public int? CleanIpPurity {get;set;} public int? CleanIpRisk {get;set;}
        public string CleanIpGrade {get;set;} public string CleanIpRiskLabel {get;set;} public string CleanIpType {get;set;}
        public string CleanIpFlags {get;set;} public string CleanIpSourceIP {get;set;} public string CleanIpCheckedAt {get;set;} public string CleanIpAttemptAt {get;set;}
        public string CleanIpState {get;set;} public bool CleanIpLatestSucceeded {get;set;}
        public List<IpQualitySnapshot> QualityHistory {get;set;}
        public double? HumanPercent {get;set;} public double? BotPercent {get;set;}
        public int ChecksTotal {get;set;} public int ChecksPassed {get;set;} public int ChecksFailed {get;set;} public int ChecksUnknown {get;set;}
        public double? DownloadMbps {get;set;} public double? UploadMbps {get;set;}
        public string SpeedCheckedAt {get;set;} public string SpeedSourceIP {get;set;}
        public string DownloadState {get;set;} public string UploadState {get;set;}
        public string SpeedAttemptAt {get;set;}
        string History {get{return CleanIpLatestSucceeded?"":" · 历史";}}
        public string HumanDisplay {get{return HumanPercent.HasValue?HumanPercent.Value.ToString("0.0")+"%"+History:"未获取";}}
        public string BotDisplay {get{return BotPercent.HasValue?BotPercent.Value.ToString("0.0")+"%"+History:"未获取";}}
        public string CheckDisplay {get{if(!String.IsNullOrEmpty(CleanIpFlags)||ChecksFailed>0)return "有风险提示"+History;if(ChecksTotal==0)return "未确认";return (ChecksUnknown==0&&ChecksPassed==ChecksTotal?"全过":"数据不完整")+" ("+ChecksPassed+"/"+ChecksTotal+")"+History;}}
        public string NetworkRiskDisplay {get{return ChecksFailed>0||!String.IsNullOrEmpty(CleanIpFlags)?"存在风险 / 网络特征"+History:ChecksTotal>0&&ChecksPassed==ChecksTotal&&ChecksUnknown==0?"已检 IP 风险项未命中"+History:"风险项尚未完整确认";}}
        public string RiskDetails {get{return !String.IsNullOrEmpty(CleanIpFlags)?CleanIpFlags:"已通过 "+ChecksPassed+" / "+ChecksTotal+" 项，待确认 "+ChecksUnknown+" 项。";}}
        string SpeedHistory {get{return !String.IsNullOrEmpty(SpeedSourceIP)&&SpeedSourceIP!="—"&&SpeedSourceIP!=IP?" · 历史":"";}}
        public string DownloadDisplay {get{return DownloadMbps.HasValue?DownloadMbps.Value.ToString("0.00")+" Mbps"+(DownloadState=="测速中"?" · 测速中":SpeedHistory):DownloadState??"未测速";}}
        public string UploadDisplay {get{return UploadMbps.HasValue?UploadMbps.Value.ToString("0.00")+" Mbps"+(UploadState=="测速中"?" · 测速中":SpeedHistory):UploadState??"未测速";}}
        public string QualityTimeDisplay {get{return "评分："+LocalTime(CleanIpCheckedAt)+" · "+(CleanIpState??"未查询")+"\n测速："+LocalTime(SpeedCheckedAt)+" · 本次平均值";}}
        static string LocalTime(string value){DateTimeOffset date;return DateTimeOffset.TryParse(value,out date)?date.ToLocalTime().ToString("MM-dd HH:mm:ss"):"未查询";}
        public void NotifyQuality(){foreach(string field in new[]{"PurityDisplay","RiskDisplay","CleanIpState","CleanIpCheckedAt","CleanIpFlags","HumanDisplay","BotDisplay","CheckDisplay","NetworkRiskDisplay","RiskDetails","QualityTimeDisplay"})Changed(field);}
        public void NotifySpeed(){Changed("DownloadDisplay");Changed("UploadDisplay");Changed("QualityTimeDisplay");}
        public string PurityDisplay {get{return CleanIpPurity.HasValue?CleanIpPurity+" "+CleanIpGrade+(CleanIpLatestSucceeded?"":" · 历史"):"—";}}
        public string RiskDisplay {get{return CleanIpRisk.HasValue?CleanIpRisk.ToString()+(CleanIpLatestSucceeded?"":" · 历史"):"—";}}
    }
    public class Settings {
        public int Version=2;
        public double FontSize=12;
        public string Theme="system",LastNode="",NodeSource="",ChromePath="",Locale="en-US",LatencyUrl="https://www.gstatic.com/generate_204";
        public bool FollowTimezone=true;
        public bool ReportDownload=true;
        public int SettleMs=3500;
        public double Width=1180,Height=780;
        public List<Site> Sites=new List<Site>();
        public List<string> FavoriteNodes=new List<string>();
        public bool FavoritesOnly=false;
        public static Settings Load(string data,string install) {
            var s=new Settings();
            string p=Path.Combine(data,"settings.json");bool modern=true;
            if(File.Exists(p)) {
                // A broken file is an explicit startup error; never silently replace user settings.
                var o=Json.Read(File.ReadAllText(p,Encoding.UTF8));
                s.FontSize=Json.Num(o,modern?"FontSize":"fontSize",12);
                s.Theme=Json.Str(o,modern?"Theme":"theme","system");
                s.LastNode=Json.Str(o,modern?"LastNode":"lastNode");
                s.NodeSource=Json.Str(o,"NodeSource");
                s.ChromePath=Json.Str(o,modern?"ChromePath":"chromePath");
                s.Locale=Json.Str(o,modern?"Locale":"locale","en-US");
                s.LatencyUrl=Json.Str(o,"LatencyUrl",s.LatencyUrl);
                s.FollowTimezone=Json.Bool(o,modern?"FollowTimezone":"followNodeTimezone",true);
                s.ReportDownload=Json.Bool(o,"ReportDownload",true);
                s.FavoritesOnly=Json.Bool(o,"FavoritesOnly");
                object favorites;
                if(o.TryGetValue("FavoriteNodes",out favorites))s.FavoriteNodes=Json.Arr(favorites).OfType<string>().Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
                s.SettleMs=(int)Json.Num(o,modern?"SettleMs":"batchSettleMs",3500);
                s.Width=modern?Json.Num(o,"Width",1180):1180;
                s.Height=modern?Json.Num(o,"Height",780):780;
                object sites;
                if(o.TryGetValue(modern?"Sites":"homeSites",out sites)) foreach(var item in Json.Arr(sites)) {
                    var v=Json.Obj(item);var url=Json.Str(v,modern?"Url":"url");
                    if(ValidUrl(url))s.Sites.Add(new Site {Url=url,Enabled=Json.Bool(v,modern?"Enabled":"enabled",true)});
                }
            }
            if(s.Sites.Count==0)foreach(var url in new[]{"https://cleanip.io/","https://gemini.google.com/","https://iplark.com/","https://ippure.com/"})s.Sites.Add(new Site {Url=url,Enabled=true});
            s.FontSize=Math.Max(9,Math.Min(22,s.FontSize));s.SettleMs=Math.Max(500,Math.Min(15000,s.SettleMs));
            return s;
        }
        public static bool ValidUrl(string url) {Uri u;return Uri.TryCreate(url,UriKind.Absolute,out u)&&(u.Scheme=="https"||u.Scheme=="http")&&!String.IsNullOrEmpty(u.Host)&&String.IsNullOrEmpty(u.UserInfo);}
        public void Save(string data) { Atomic.Write(Path.Combine(data,"settings.json"),Json.Write(this)); }
    }
    public static class Atomic {
        public static void Write(string file,string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp=file+".writing";File.WriteAllText(temp,text,new UTF8Encoding(false));
            if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
        }
    }
    public class ExitInfo { public string IP="",Country="",City="",Timezone="",ISP=""; }
    public class Paths {
        public string Install,Data,Profile,Logs,Reports;
        public Paths(string install,string data,bool isolated) {
            Install=Path.GetFullPath(install);Data=Path.GetFullPath(data);
            Profile=Path.Combine(Data,"chrome-profile");
            Logs=isolated?Path.Combine(Data,"logs"):Path.Combine(Install,"logs");Reports=isolated?Path.Combine(Data,"reports"):Path.Combine(Install,"outputs","reports");
            foreach(var dir in new[]{Data,Logs,Reports,Path.Combine(Data,"core")})Directory.CreateDirectory(dir);
        }
        public void Log(string eventName,string detail) {
            // Never log source YAML, HTTP headers, API bodies or command lines.
            try {File.AppendAllText(Path.Combine(Logs,"app-"+DateTime.Now.ToString("yyyyMMdd")+".log"),DateTimeOffset.Now.ToString("o")+" "+eventName+" "+detail.Replace("\r"," ").Replace("\n"," ")+Environment.NewLine,Encoding.UTF8);}catch{}
        }
    }
}
