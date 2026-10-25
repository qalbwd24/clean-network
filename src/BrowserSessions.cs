using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CleanBrowser {
    public class BrowserSlot {public int Index;public string Node;public BrowserService Browser;public ExitInfo Exit;}
    public class BrowserSessions {
        readonly Paths paths;readonly Settings settings;readonly ProxyService proxy;
        readonly Dictionary<string,BrowserSlot> active=new Dictionary<string,BrowserSlot>();
        public event Action<string,IpQualitySnapshot> QualityObserved;
        public int Count {get{return active.Values.Count(x=>x.Browser.Running);}}
        public IEnumerable<string> Names {get{return active.Keys.ToArray();}}
        public bool IsOpen(string node){BrowserSlot slot;return active.TryGetValue(node,out slot)&&slot.Browser.Running;}
        public BrowserSessions(Paths p,Settings s,ProxyService core){paths=p;settings=s;proxy=core;}
        public async Task Prune(){foreach(var item in active.Values.ToArray())if(!item.Browser.Running){await item.Browser.Close();active.Remove(item.Node);}}
        public async Task<BrowserSlot> Get(string node,CancellationToken ct){BrowserSlot slot;if(active.TryGetValue(node,out slot))return slot;
            int index=Enumerable.Range(0,4).FirstOrDefault(i=>!active.Values.Any(x=>x.Index==i));
            if(active.Count>=4)throw new Exception("最多同时打开 4 个节点窗口。请先关闭不用的窗口，或使用批量截图检测。");
            string key;using(var hash=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(node))).Replace("-","").Substring(0,20);
            var local=new Paths(paths.Install,Path.Combine(paths.Data,"browsers",key),true);
            slot=new BrowserSlot {Index=index,Node=node,Browser=new BrowserService(local,settings,proxy,proxy.SlotPorts[index]){NodeLabel=node}};active[node]=slot;
            slot.Browser.CleanIpObserved+=snapshot=>{if(QualityObserved!=null)QualityObserved(node,snapshot);};
            try{await proxy.SelectSlot(index,node,ct);return slot;}catch{active.Remove(node);throw;}
        }
        public async Task Close(string node){BrowserSlot slot;if(active.TryGetValue(node,out slot)){await slot.Browser.Close();await proxy.ClearConnections("CB_SLOT_"+slot.Index,CancellationToken.None);active.Remove(node);}}
        public async Task CloseAll(){foreach(string node in active.Keys.ToArray())try{await Close(node);}catch{}active.Clear();}
    }
}
