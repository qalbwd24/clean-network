import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const files=fs.readFileSync(path.join(root,'tmp/public-files.txt'),'utf8').replace(/^\ufeff/,'').split(/\r?\n/).filter(Boolean);
const findings=[];
const rules=[
  ['personal-user-path',/[A-Z]:[\\/]Users[\\/][^\\/\s"']+/i],
  ['personal-directory',/[A-Z]:[\\/](?:个人兴趣研究|deepseek harness|音乐下载|拾页)/i],
  ['private-key',/-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----/],
  ['service-token',/\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,})\b/],
  ['email',/\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i],
  ['phone',/(?<!\d)1[3-9]\d{9}(?!\d)/]
];
const forbidden=/^(?:data|tmp|logs|outputs|backups)\/|(?:^|\/)(?:FILES\.md|AGENTS\.md|\.env(?:\..*)?|.*\.(?:clixml|pem|key|sqlite|db|lnk|log|exe))$/i;
for(const file of files){
  if(forbidden.test(file))findings.push({file,rule:'forbidden-path'});
  const buf=fs.readFileSync(path.join(root,file));
  if(/\.(?:png|ico)$/i.test(file))continue;
  const content=buf.toString('utf8');
  for(const [rule,re] of rules){const input=rule==='email'?content.replace(/\b[A-Za-z0-9._%+-]+@example\.(?:com|org|net)\b/g,'example-fixture'):content;if(re.test(input))findings.push({file,rule});}
}
const result={pass:findings.length===0,fileCount:files.length,bytes:files.reduce((n,f)=>n+fs.statSync(path.join(root,f)).size,0),findings};
fs.writeFileSync(path.join(root,'outputs','checks','public-audit.json'),JSON.stringify(result,null,2));
console.log(JSON.stringify(result));if(!result.pass)process.exitCode=1;
