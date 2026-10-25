using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CleanBrowser {
    public static class AutoStart {
        const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";const string Name="CleanNetworkCheck";
        public static bool Enabled {get{using(var key=Registry.CurrentUser.OpenSubKey(Key))return key!=null&&key.GetValue(Name)!=null;}}
        public static void Set(bool enabled,string install){using(var key=Registry.CurrentUser.CreateSubKey(Key)){if(enabled)key.SetValue(Name,"\""+Path.Combine(install,"CleanNetwork.exe")+"\" --startup",RegistryValueKind.String);else key.DeleteValue(Name,false);}}
        public static async Task<int> WaitForClash(CancellationToken ct) {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromMinutes(3)){
                ct.ThrowIfCancellationRequested();int port=0;
                string source=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"io.github.clash-verge-rev.clash-verge-rev\clash-verge.yaml");
                try{if(File.Exists(source)){string yaml=File.ReadAllText(source,Encoding.UTF8);var m=Regex.Match(yaml,@"(?m)^mixed-port:\s*(\d+)");if(m.Success)Int32.TryParse(m.Groups[1].Value,out port);}}catch(IOException){}
                if(port>0&&await Probe(port,ct))return port;
                await Task.Delay(2500,ct);
            }
            throw new TimeoutException("等待 Clash 连接超过 3 分钟。请检查 Clash，之后点击刷新节点。");
        }
        public static async Task<bool> Probe(int port,CancellationToken ct) {
            using(var c=new HttpClient(new HttpClientHandler {UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:"+port)}){Timeout=TimeSpan.FromSeconds(5)}){
                try{using(var r=await c.GetAsync("https://cp.cloudflare.com/generate_204",ct))return r.StatusCode==HttpStatusCode.NoContent;}catch(OperationCanceledException){ct.ThrowIfCancellationRequested();return false;}catch(HttpRequestException){return false;}
            }
        }
    }
}
