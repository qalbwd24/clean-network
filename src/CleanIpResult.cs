using System;
using System.Net;
using System.Linq;
using System.Collections.Generic;

namespace CleanBrowser {
    public class IpQualitySnapshot {
        public string CheckedAt=DateTimeOffset.Now.ToString("o"),SourceIP="",Grade="",RiskLabel="",IpType="",RiskFlags="",State="未确认";
        public int? Purity,Risk;
        public bool Success;
    }
    public static class CleanIpResult {
        static int? Score(Dictionary<string,object> obj,string key){object value;if(!obj.TryGetValue(key,out value)||value==null)return null;double number;if(!Double.TryParse(Convert.ToString(value),out number)||number<0||number>100)return null;return (int)Math.Round(number);}
        public static IpQualitySnapshot Parse(Dictionary<string,object> result,string expectedIP){
            var snapshot=new IpQualitySnapshot();
            if(result==null||result.Count==0){snapshot.State="页面尚未返回评分";return snapshot;}
            string error=Json.Str(result,"error"),code=Json.Str(result,"code");
            if(!Json.Bool(result,"ok")){
                string reason=(error+" "+code).ToLowerInvariant();snapshot.State=reason.Contains("bot")||reason.Contains("verify")||reason.Contains("captcha")?"需要网站验证":reason.Contains("limit")||reason.Contains("quota")?"网站限流":reason.Contains("auth")||reason.Contains("login")?"需要登录":"网站未返回有效结果";return snapshot;
            }
            string ip=Json.Str(result,"ip");IPAddress parsed;if(!IPAddress.TryParse(ip,out parsed)){snapshot.State="网站未返回有效出口 IP";return snapshot;}
            snapshot.SourceIP=ip;
            if(!String.IsNullOrEmpty(expectedIP)&&ip!=expectedIP){snapshot.State="出口 IP 不一致，未记分";return snapshot;}
            var purity=Json.Obj(result.ContainsKey("purity")?result["purity"]:null);var risk=Json.Obj(result.ContainsKey("risk")?result["risk"]:null);
            snapshot.Purity=Score(purity,"score");snapshot.Risk=Score(risk,"risk_score");snapshot.Grade=Json.Str(purity,"grade");snapshot.RiskLabel=Json.Str(risk,"risk_label");snapshot.IpType=Json.Str(purity,"ip_type");
            var flags=new List<string>();object factors;if(purity.TryGetValue("risk_factors",out factors))flags.AddRange(Json.Arr(factors).OfType<string>().Take(10));
            foreach(var flag in new[]{new[]{"is_vpn","VPN 标记"},new[]{"is_proxy","代理标记"},new[]{"is_tor","Tor 标记"},new[]{"is_datacenter","数据中心标记"}})if(Json.Bool(risk,flag[0]))flags.Add(flag[1]);
            object checks;if(result.TryGetValue("checks",out checks))foreach(var item in Json.Arr(checks)){var check=Json.Obj(item);string status=Json.Str(check,"status");if(status=="fail"||status=="warn")flags.Add((Json.Str(check,"label",Json.Str(check,"key"))+"："+Json.Str(check,"verdict")).Trim('：'));}
            snapshot.RiskFlags=String.Join("；",flags.Distinct().Take(16));snapshot.Success=snapshot.Purity.HasValue||snapshot.Risk.HasValue;snapshot.State=snapshot.Success?"已取得网站评分":"网站数据不完整，未记分";return snapshot;
        }
        public static void Apply(Node node,IpQualitySnapshot snapshot){
            if(node.QualityHistory==null)node.QualityHistory=new List<IpQualitySnapshot>();node.QualityHistory.Add(snapshot);if(node.QualityHistory.Count>20)node.QualityHistory.RemoveRange(0,node.QualityHistory.Count-20);
            node.CleanIpState=snapshot.State;node.CleanIpAttemptAt=snapshot.CheckedAt;node.CleanIpLatestSucceeded=snapshot.Success;
            if(snapshot.Success){node.CleanIpPurity=snapshot.Purity;node.CleanIpRisk=snapshot.Risk;node.CleanIpGrade=snapshot.Grade;node.CleanIpRiskLabel=snapshot.RiskLabel;node.CleanIpFlags=snapshot.RiskFlags;node.CleanIpSourceIP=snapshot.SourceIP;node.CleanIpCheckedAt=snapshot.CheckedAt;node.CleanIpType=snapshot.IpType;}
            foreach(string field in new[]{"PurityDisplay","RiskDisplay","CleanIpState","CleanIpCheckedAt","CleanIpFlags"})node.Changed(field);
        }
    }
}
