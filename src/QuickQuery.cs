using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CleanBrowser {
    public sealed class QuickQuery {
        readonly Paths paths;readonly Settings settings;readonly ProxyService core;
        public QuickQuery(Paths p,Settings s,ProxyService proxy){paths=p;settings=s;core=proxy;}
        public async Task Run(IList<Node> nodes,Action<Node> focus,Action<string> status,Action<int,int> progress,CancellationToken ct){
            for(int i=0;i<nodes.Count;i++){
                ct.ThrowIfCancellationRequested();var node=nodes[i];focus(node);node.Status="快捷查询中";node.Error="";
                node.CleanIpLatestSucceeded=false;node.CleanIpState="查询中";node.NotifyQuality();
                node.DownloadMbps=null;node.UploadMbps=null;node.DownloadState="等待测速";node.UploadState="等待测速";node.SpeedCheckedAt=null;node.SpeedAttemptAt=DateTimeOffset.Now.ToString("o");node.NotifySpeed();
                BrowserService page=null;bool qualityApplied=false,canceled=false;
                try{
                    await core.Select(node.Name,ct);
                    status((i+1)+"/"+nodes.Count+" · 查询 CleanIP："+node.Name);
                    try{
                        node.IP="—";node.Place="";var exit=await core.Exit(ct);node.IP=exit.IP;node.Place=(exit.Country+" "+exit.City).Trim();
                        string key;using(var hash=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(node.Name))).Replace("-","").Substring(0,20);
                        page=new BrowserService(new Paths(paths.Install,Path.Combine(paths.Data,"report-browsers",key),true),settings,core){Headless=true,NodeLabel=node.Name};
                        await page.SetTimezone(exit.Timezone,ct);await page.Open("https://cleanip.io/",ct,false);
                        var snapshot=await page.ReadCleanIp(exit.IP,ct,true);if(snapshot.Success&&snapshot.ObservedAsCurrentExit){if(snapshot.SourceIP!=exit.IP)snapshot.State="已取得评分 · 网站出口与连通查询不同";node.IP=snapshot.SourceIP;}CleanIpResult.Apply(node,snapshot);qualityApplied=true;
                    }catch(OperationCanceledException){throw;}catch(Exception ex){CleanIpResult.Apply(node,new IpQualitySnapshot {State="查询失败："+ex.Message});qualityApplied=true;node.Error=ex.Message;}
                    if(page!=null){await page.Close();page.Dispose();page=null;}
                    node.SpeedSourceIP=node.IP;
                    for(int direction=0;direction<2;direction++){
                        bool upload=direction==1;string label=upload?"上传":"下载";node.Status="测"+label+"中";status((i+1)+"/"+nodes.Count+" · "+label+"测速："+node.Name);
                        if(upload)node.UploadState="测速中";else node.DownloadState="测速中";node.NotifySpeed();
                        try{
                            double speed=await core.QuickSpeed(upload,value=>{if(upload)node.UploadMbps=value;else node.DownloadMbps=value;node.NotifySpeed();},ct);
                            if(upload){node.UploadMbps=speed;node.UploadState="已测";}else{node.DownloadMbps=speed;node.DownloadState="已测";node.SpeedValue=speed;node.Speed=speed.ToString("0.00");}
                            node.SpeedCheckedAt=DateTimeOffset.Now.ToString("o");
                        }catch(OperationCanceledException){if(upload){node.UploadMbps=null;node.UploadState="已停止";}else{node.DownloadMbps=null;node.DownloadState="已停止";}throw;}
                        catch(Exception ex){if(upload){node.UploadMbps=null;node.UploadState="测速失败";}else{node.DownloadMbps=null;node.DownloadState="测速失败";}node.Error+=(node.Error==""?"":"；")+label+"："+ex.Message;}
                        finally{node.NotifySpeed();}
                    }
                    node.Status=node.CleanIpLatestSucceeded&&node.DownloadMbps.HasValue&&node.UploadMbps.HasValue?"查询完成":"部分结果未取得";node.TestedAt=DateTimeOffset.Now.ToString("o");
                }catch(OperationCanceledException){canceled=true;node.Status="已停止";if(!qualityApplied)CleanIpResult.Apply(node,new IpQualitySnapshot {State="本次查询已停止"});if(node.DownloadState=="等待测速")node.DownloadState="已停止";if(node.UploadState=="等待测速")node.UploadState="已停止";node.NotifySpeed();}
                catch(Exception ex){node.Status="查询失败";node.Error=ex.Message;if(!qualityApplied)CleanIpResult.Apply(node,new IpQualitySnapshot {State="查询失败："+ex.Message});node.DownloadState="未测速";node.UploadState="未测速";node.NotifySpeed();}
                if(page!=null){await page.Close();page.Dispose();}
                if(canceled)throw new OperationCanceledException(ct);
                progress(i+1,nodes.Count);
            }
        }
    }
}
