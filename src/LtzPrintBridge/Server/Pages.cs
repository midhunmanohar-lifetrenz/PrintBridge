namespace LtzPrintBridge.Server;

public static class Pages
{
    const string Css = "body{font:15px/1.5 system-ui,Segoe UI,sans-serif;margin:0;background:#eef2f3;color:#16202a}main{max-width:720px;margin:32px auto;padding:0 16px}h1{font-size:22px;margin:0 0 4px}.card{background:#fff;border:1px solid #d9e0e3;border-radius:10px;padding:18px;margin-top:16px}button,select{font:inherit;padding:8px 14px;border-radius:8px;border:1px solid #c5ced3;background:#fff}button.p{background:#0b7a75;border-color:#0b7a75;color:#fff;cursor:pointer}textarea{width:100%;min-height:150px;font:13px Consolas,monospace;border:1px solid #c5ced3;border-radius:8px;padding:8px;box-sizing:border-box}.ok{color:#17803d}.bad{color:#b42318}code{background:#eef2f3;padding:1px 6px;border-radius:5px}";

    public static string Test() => $@"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>PrintBridge test page</title><style>{Css}</style>
<main><h1>PrintBridge</h1><div style='color:#5d6c78'>Local test page. This page talks to the bridge running on your PC.</div>
<div class=card><b>Bridge</b><div id=st>Checking...</div></div>
<div class=card><b>Printer</b><p><select id=pr style='min-width:280px'></select> <button id=rf>Refresh</button></p>
<textarea id=data spellcheck=false>^XA
^PW400
^LL200
^FO20,20^A0N,36,36^FDTEST LABEL^FS
^FO20,70^BCN,60,Y^FD123456789^FS
^XZ</textarea>
<p><button class=p id=go>Send to printer</button> <span id=res></span></p>
<small>Tip: replace the text above with the command language of the selected printer (ZPL, TSPL, EPL...).</small></div></main>
<script>
const $=i=>document.getElementById(i);let list=[];
async function load(){{try{{const r=await fetch('/api/v1/status');const s=await r.json();$('st').innerHTML='<span class=ok>Running</span> &middot; version '+s.version;
const p=await (await fetch('/api/v1/printers')).json();list=p;$('pr').replaceChildren(...(p.length?p:[{{id:'',name:'No printers found'}}]).map(x=>{{const o=document.createElement('option');o.value=x.id;o.textContent=x.id?`${{x.name}} (${{x.language}})${{x.isDefault?' - default':''}}`:x.name;return o;}}));}}catch(e){{$('st').innerHTML='<span class=bad>Not reachable</span>';}}}}
$('rf').onclick=load;
$('go').onclick=async()=>{{$('res').textContent='Sending...';try{{const r=await fetch('/api/v1/print',{{method:'POST',headers:{{'Content-Type':'application/json'}},body:JSON.stringify({{printerId:$('pr').value,data:$('data').value}})}});const j=await r.json();say(j.ok,j.ok?'Sent ('+j.jobId+')':(j.message||j.error));}}catch(e){{say(false,String(e));}}}};
function say(ok,t){{const e=document.createElement('span');e.className=ok?'ok':'bad';e.textContent=t;$('res').replaceChildren(e);}}
load();
</script>";

    public static string Ssl(bool trusted) => $@"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>PrintBridge certificate</title><style>{Css}</style>
<main><h1>Secure connection</h1><div style='color:#5d6c78'>PrintBridge certificate check</div>
<div class=card><p class={(trusted ? "ok" : "bad")}><b>{(trusted ? "Certificate is installed and trusted on this computer." : "Certificate is not installed in the system trust store yet.")}</b></p>
<p>You are seeing this page, so your browser can already reach the bridge over HTTPS. Websites can now print to your label printers.</p>
<p>If you had to click through a browser warning to get here, open the PrintBridge app, go to <b>Diagnostics</b> and press <b>Install certificate</b>. The warning will not appear again in Chrome and Edge.</p>
<p>Firefox keeps its own certificate list. Download <a href='/ca.cer'>the certificate</a> and import it under Settings, Privacy and Security, View Certificates, Authorities.</p></div></main>";
}
