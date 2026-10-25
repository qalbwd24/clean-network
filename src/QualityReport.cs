using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CleanBrowser {
    public class SiteObservation {
        public string Url="",Title="",State="未检测",Evidence="",Screenshot="",Error="";
        public IpQualitySnapshot CleanIp;
    }
    public class QualityRow {
        public string Node="",IP="",Place="",Timezone="",ISP="",Verdict="",Error="";
        public int DelayMs;public double Mbps;public bool DownloadMeasured;
        public List<SiteObservation> Sites=new List<SiteObservation>();
    }
    public sealed class QualityReport {
        readonly Paths paths;readonly Settings settings;readonly ProxyService core;
        public QualityReport(Paths p,Settings s,ProxyService proxy){paths=p;settings=s;core=proxy;}
        public static SiteObservation Interpret(string url,Dictionary<string,object> page){
            string title=Json.Str(page,"title"),text=Json.Str(page,"text");
            bool blocked=Json.Bool(page,"challenge")||Regex.IsMatch(title,@"just a moment|verify.*human|attention required|安全验证|人机验证",RegexOptions.IgnoreCase);
            var evidence=new List<string>();
            if(!blocked)foreach(Match match in Regex.Matches(text,@"(?:风险系数|风险评分|风险值|纯净度|Risk\s*(?:score)?|Human|Bot|真人比例|机器人比例)[^\r\n\d]{0,24}\d{1,3}(?:\.\d+)?\s*%?",RegexOptions.IgnoreCase)){if(evidence.Count>=5)break;evidence.Add(match.Value.Trim());}
            return new SiteObservation {Url=url,Title=title,State=blocked?"需要人工验证":evidence.Count>0?"已读到评分摘录":"页面已打开，未确认评分",Evidence=String.Join("；",evidence)};
        }
        public static string Verdict(QualityRow row){if(row.DelayMs<=0&&String.IsNullOrEmpty(row.IP))return "连通未确认";if(row.DownloadMeasured&&row.Mbps<3)return "下载偏慢";if(row.DelayMs>1000)return "延迟偏高";if(row.DelayMs<=0)return "出口可用，延迟未确认";return row.DownloadMeasured?"网络可用":"延迟可用，下载未测";}
        public async Task<string> Run(IList<Node> nodes,IList<string> urls,bool download,Action<string> status,Action<int,int> progress,CancellationToken ct){
            string folder=Path.Combine(paths.Reports,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(folder);
            var rows=new List<QualityRow>();bool canceled=false;BrowserService browser=null;
            try{for(int i=0;i<nodes.Count;i++){
                ct.ThrowIfCancellationRequested();Node node=nodes[i];var row=new QualityRow {Node=node.Name};rows.Add(row);node.Status="生成报告中";node.Error="";
                try{
                    status((i+1)+"/"+nodes.Count+" · 检查 "+node.Name);await core.Select(node.Name,ct);
                    try{row.DelayMs=await core.Delay(node.Name,ct);node.Delay=row.DelayMs+" ms";node.DelayValue=row.DelayMs;}catch(OperationCanceledException){throw;}catch(Exception e){row.Error=e.Message;}
                    ExitInfo exit=null;try{exit=await core.Exit(ct);row.IP=exit.IP;row.Place=(exit.Country+" "+exit.City).Trim();row.Timezone=exit.Timezone;row.ISP=exit.ISP;node.IP=row.IP;node.Place=row.Place;}catch(OperationCanceledException){throw;}catch(Exception e){row.Error+=(row.Error==""?"":"；")+e.Message;}
                    if(download){status((i+1)+"/"+nodes.Count+" · 测下载 "+node.Name);try{row.Mbps=await core.Speed(ct,10000000);row.DownloadMeasured=true;node.Speed=row.Mbps.ToString("0.0");node.SpeedValue=row.Mbps;}catch(OperationCanceledException){throw;}catch(Exception e){row.Error+=(row.Error==""?"":"；")+"下载测速："+e.Message;}}
                    if(urls.Count>0){
                        string key;using(var hash=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(node.Name))).Replace("-","").Substring(0,20);
                        var local=new Paths(paths.Install,Path.Combine(paths.Data,"report-browsers",key),true);
                        browser=new BrowserService(local,settings,core){Headless=true,NodeLabel=node.Name};await browser.SetTimezone(exit==null?"":exit.Timezone,ct);
                        foreach(string url in urls){ct.ThrowIfCancellationRequested();var observation=new SiteObservation {Url=url};row.Sites.Add(observation);status((i+1)+"/"+nodes.Count+" · 自动读取 "+new Uri(url).Host);
                            try{string sid=await browser.Open(url,ct,true);var info=await browser.ReadVisiblePage(sid,ct);var parsed=Interpret(url,info);observation.Title=parsed.Title;observation.State=parsed.State;observation.Evidence=parsed.Evidence;
                                if(new Uri(url).Host=="cleanip.io"){observation.CleanIp=await browser.ReadCleanIp(row.IP,ct);CleanIpResult.Apply(node,observation.CleanIp);observation.State=observation.CleanIp.State;if(observation.CleanIp.Success)observation.Evidence="纯净度 "+observation.CleanIp.Purity+" "+observation.CleanIp.Grade+"；风险分 "+observation.CleanIp.Risk+" "+observation.CleanIp.RiskLabel+"；"+observation.CleanIp.RiskFlags;}
                                observation.Screenshot=(i+1).ToString("D3")+"-"+row.Sites.Count.ToString("D2")+".png";await browser.Screenshot(sid,Path.Combine(folder,observation.Screenshot),ct);
                            }catch(OperationCanceledException){throw;}catch(Exception e){observation.State="无法确认";observation.Error=e.Message;if(new Uri(url).Host=="cleanip.io"){observation.CleanIp=new IpQualitySnapshot{State="网站访问失败："+e.Message};CleanIpResult.Apply(node,observation.CleanIp);}}
                        }
                        await browser.Close();browser.Dispose();browser=null;
                    }
                    row.Verdict=Verdict(row);node.Status=row.Verdict;node.Error=row.Error;node.TestedAt=DateTimeOffset.Now.ToString("o");
                }catch(OperationCanceledException){row.Verdict="已停止";node.Status="已停止";throw;}catch(Exception e){row.Verdict="检测失败";row.Error=e.Message;node.Status=row.Verdict;node.Error=e.Message;}
                if(browser!=null){await browser.Close();browser.Dispose();browser=null;}
                progress(i+1,nodes.Count);Save(folder,rows,false,nodes.Count);
            }}catch(OperationCanceledException){canceled=true;}catch(Exception e){rows.Add(new QualityRow {Node="任务",Verdict="任务异常",Error=e.Message});}
            if(browser!=null){await browser.Close();browser.Dispose();}Save(folder,rows,canceled,nodes.Count);return Path.Combine(folder,"index.html");
        }
        public static void Save(string folder,IList<QualityRow> rows,bool canceled,int requested){
            Directory.CreateDirectory(folder);Atomic.Write(Path.Combine(folder,"report.json"),Json.Write(Json.D("generatedAt",DateTimeOffset.Now.ToString("o"),"canceled",canceled,"requestedNodes",requested,"nodes",rows)));
            Func<string,string> h=WebUtility.HtmlEncode;var sorted=rows.OrderBy(r=>r.DelayMs>0?0:1).ThenBy(r=>r.DownloadMeasured&&r.Mbps<3?1:0).ThenBy(r=>r.DelayMs>0?r.DelayMs:Int32.MaxValue).ToList();
            var html=new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>节点质量检测报告</title><style>body{font:15px/1.7 system-ui,sans-serif;background:#f3f5fa;color:#172033;max-width:1280px;margin:32px auto;padding:0 20px}h1{font-size:30px}h2{font-size:20px}table{border-collapse:collapse;width:100%;background:white;border-radius:10px;overflow:hidden}th,td{text-align:left;padding:12px;border-bottom:1px solid #e1e6ef}th{color:#52617a;background:#eaf0ff}section{padding:20px;border:1px solid #dde3ee;border-radius:12px;background:white;margin:20px 0}small,.muted{color:#64748b}a{color:#365fea}details{margin:10px 0}img{max-width:100%;border:1px solid #dde3ee}button{padding:8px 16px;border:1px solid #ccd5e5;background:white;border-radius:8px;cursor:pointer}@media(prefers-color-scheme:dark){body{background:#111723;color:#e8eef8}section,table,button{background:#1a2231;color:#e8eef8}th{background:#263a64;color:#dbe6ff}td,th,section{border-color:#344158}.muted,small{color:#a0aec2}}</style><h1>节点质量检测报告</h1><p>"+DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")+" · "+rows.Count+" / "+requested+" 节点"+(canceled?" · 已停止，保留部分结果":"")+"</p><p class='muted'>网络评价依据本次连通、延迟和下载观测。网站评分只展示实际读到的摘录；未读到或需要验证时单独标记。下载单节点最多 10 MB，最长约 4 秒。</p><p><button onclick='window.print()'>打印或另存 PDF</button></p><div style='overflow:auto'><table><thead><tr><th>节点</th><th>网络评价</th><th>延迟 ms</th><th>下载 Mbps</th><th>出口 IP</th><th>地区</th><th>网站结果</th></tr></thead><tbody>");
            foreach(var row in sorted){string sites=String.Join("；",row.Sites.Select(s=>new Uri(s.Url).Host+"："+s.State));html.Append("<tr><td>"+h(row.Node)+"</td><td>"+h(row.Verdict)+"</td><td>"+(row.DelayMs>0?row.DelayMs.ToString():"未确认")+"</td><td>"+(row.DownloadMeasured?row.Mbps.ToString("0.0"):"未测得")+"</td><td>"+h(row.IP)+"</td><td>"+h(row.Place)+"</td><td>"+h(sites)+"</td></tr>");}
            html.Append("</tbody></table></div>");
            foreach(var row in rows){html.Append("<section><h2>"+h(row.Node)+"</h2><p>"+h(row.Verdict+" · "+row.Place+" · "+row.Timezone)+"</p><p class='muted'>"+h(row.ISP)+"</p>");if(row.Error!="")html.Append("<p>未确认项："+h(row.Error)+"</p>");foreach(var site in row.Sites){html.Append("<details><summary>"+h(new Uri(site.Url).Host+" · "+site.State)+"</summary><p>"+h(site.Title)+"</p>");if(site.Evidence!="")html.Append("<p>网站实际摘录："+h(site.Evidence)+"</p>");if(site.Error!="")html.Append("<p>"+h(site.Error)+"</p>");if(site.Screenshot!="")html.Append("<a href='"+site.Screenshot+"'><img loading='lazy' src='"+site.Screenshot+"' alt='实际网页截图'></a>");html.Append("</details>");}html.Append("</section>");}
            html.Append("</html>");Atomic.Write(Path.Combine(folder,"index.html"),html.ToString());
        }
    }
}
