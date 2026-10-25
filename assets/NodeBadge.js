(() => {
  const node = __NODE_LABEL_JSON__;
  const prefix = '[' + node + '] ';
  const title = () => {
    if (!document.title.startsWith(prefix)) document.title = prefix + (document.title || '节点检测');
  };
  const mount = () => {
    if (window.__cleanNetworkMark) return;
    window.__cleanNetworkMark = true;
    title();
    if (!document.getElementById('__cleanNetworkNodeMarker') && document.body) {
      const badge = document.createElement('div');
      badge.id = '__cleanNetworkNodeMarker';
      badge.textContent = '当前节点 · ' + node;
      badge.style.cssText = 'position:fixed;right:12px;top:12px;z-index:2147483647;max-width:75vw;padding:8px 12px;border:1px solid #7188bd;border-radius:9px;background:#142541ed;color:#fff;font:13px/1.5 system-ui,sans-serif;box-shadow:0 3px 14px #0003;pointer-events:none;overflow-wrap:anywhere';
      document.body.appendChild(badge);
    }
    if (document.head) new MutationObserver(title).observe(document.head, {subtree:true,childList:true,characterData:true});
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, {once:true});
  else mount();
})();
