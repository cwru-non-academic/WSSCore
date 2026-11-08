// Source-copy locks and executable workflow pins are separate trust boundaries.
// Refresh pins only after committing/verifying their owning integration tools.
import {execFileSync} from 'node:child_process';
import {readFileSync,readdirSync,writeFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {GitHub} from '../release/github.mjs';
import {CORE,canonical,requireThat} from '../release/verify.mjs';
import {sourceBytes} from './sync-tooling.mjs';
import {integrationConfiguration} from '../release/channels.mjs';
const fullSha=value=>typeof value==='string'&&/^[a-f0-9]{40}$/.test(value);
const escape=value=>value.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
const git=(root,...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim();
const pinRoles=JSON.parse(readFileSync(new URL('./executable-pin-roles.json',import.meta.url),'utf8'));

export function workflowPins(text,repositories){
  const result=[],known=new Set(repositories);let offset=0;
  const lines=text.split(/(?<=\n)/);
  for(let i=0;i<lines.length;i++){
    const line=lines[i],repo=/^\s*repository:\s*([A-Za-z0-9_/-]+)\s*$/.exec(line.trimEnd())?.[1];
    if(known.has(repo)){
      const following=lines[i+1]||'',match=/^(\s*ref:\s*)([^\r\n]+?)(\s*)$/.exec(following.trimEnd());
      requireThat(match,'Registered checkout must place its explicit ref immediately after repository');
      const ref=match[2];
      let checkoutPath;
      const indentation=line.match(/^\s*/)[0].length;
      for(let j=i+2;j<lines.length;j++){
        if(lines[j].trim()&&lines[j].match(/^\s*/)[0].length<indentation)break;
        const path=/^\s*path:\s*([A-Za-z0-9_./-]+)\s*$/.exec(lines[j].trimEnd());
        if(path){checkoutPath=path[1];break;}
      }
      if(fullSha(ref))result.push({repository:repo,sha:ref,kind:'checkout',checkoutPath,start:offset+line.length+match[1].length,end:offset+line.length+match[1].length+ref.length});
      else requireThat(['dev','main'].includes(ref)||/^\$\{\{[^\r\n]+\}\}$/.test(ref),'Registered checkout requires a full immutable SHA or an explicitly validated branch/source expression');
    }
    for(const repository of known){
      const pattern=new RegExp(`uses:\\s*${escape(repository)}/(\\.github/workflows/[A-Za-z0-9_-]+\\.yml)@([^\\s]+)`,'g');
      for(const match of line.matchAll(pattern)){
        requireThat(fullSha(match[2]),'External first-party reusable workflows require immutable full SHAs');
        const start=offset+match.index+match[0].lastIndexOf('@')+1;
        result.push({repository,sha:match[2],workflow:match[1],kind:'workflow',start,end:start+match[2].length});
      }
    }
    offset+=line.length;
  }
  return result;
}
export function replacePins(text,pins,replacements){
  for(const pin of [...pins].sort((a,b)=>b.start-a.start)){
    const sha=pin.replacement||replacements[pin.repository];requireThat(fullSha(sha),'Replacement requires an immutable source revision');
    text=text.slice(0,pin.start)+sha+text.slice(pin.end);
  }
  return text;
}
function runtimeFixture(repository,file,pin,roles){
  requireThat(Array.isArray(roles?.runtime_fixtures),'Runtime fixture roles must be explicit');
  const matches=roles.runtime_fixtures.filter(r=>r.target===repository&&r.workflow===file&&r.checkout_path===pin.checkoutPath&&r.repository===pin.repository);
  requireThat(matches.length<=1,'Ambiguous executable fixture role');
  if(matches.length){requireThat(fullSha(matches[0].commit),'Runtime fixture requires an immutable source');return matches[0];}
  return null;
}
function descriptor(repository,profiles,lock){
  const matches=Object.values(profiles).filter(p=>p.target.repository===repository);
  requireThat(matches.length===1&&lock.repository===repository&&lock.source_repository===CORE&&fullSha(lock.source_commit),'Unknown target or canonical source lock');
  const profile=matches[0],wrapper=profiles[profile.wrapper];
  requireThat(wrapper?.role==='wrapper'&&wrapper.family===profile.family,'Invalid integration owner');
  return {profile,parent:profile.role==='wrapper'?null:wrapper.target.repository};
}
export function verifyWorkflowPins({repository,profiles,lock,files,roles=pinRoles},g){
  const {profile,parent}=descriptor(repository,profiles,lock),integration=lock.origins?.integration,registered=[...new Set([CORE,integration?.repository,...(parent?[parent]:[])].filter(Boolean))],all=[];
  for(const [file,text] of Object.entries(files))for(const pin of workflowPins(text,registered))all.push({...pin,file});
  if(parent&&['python','matlab'].includes(profile.family))requireThat(all.some(p=>p.repository===parent&&p.file==='release-receipt.yml'&&p.kind==='checkout'),'Receipt must pin its integration validator explicitly');
  if(parent&&profile.family==='unity')requireThat(all.some(p=>p.repository===parent&&p.kind==='workflow'),'Unity worker must pin its reusable validation workflow');
  const cache=new Map(),object=(repo,ref,path)=>{
    const key=JSON.stringify([repo,ref,path]);if(!cache.has(key))cache.set(key,g.object(repo,ref,path).sha);return cache.get(key);
  };
  let heads;
  for(const pin of all){
    const fixture=runtimeFixture(repository,pin.file,pin,roles);
    if(fixture){requireThat(pin.sha===fixture.commit,`Reviewed runtime fixture pin changed: ${pin.file} / ${pin.checkoutPath}`);continue;}
    if(pin.file==='release-receipt.yml'&&pin.checkoutPath==='bootstrap/Python_WSS_Integration_Lib'){requireThat(pin.repository===integration?.repository&&pin.sha===integration.commit,'Receipt bootstrap differs from immutable integration origin');continue;}
    if(pin.file==='release-receipt.yml'&&pin.checkoutPath==='validator'){
      requireThat(pin.repository===parent,'Receipt native validator must belong to registered wrapper');
      const record=g.get(`repos/${parent}/contents/.github/scripts/wss-ci.lock.json?ref=${pin.sha}`);
      requireThat(record?.encoding==='base64','Pinned native validator lock unavailable');
      const nativeLock=JSON.parse(Buffer.from(record.content,'base64'));
      requireThat(nativeLock.repository===parent&&nativeLock.source_commit===lock.source_commit&&canonical(nativeLock.origins)===canonical(lock.origins),'Pinned native validator uses another canonical source lock');
      continue;
    }
    if(pin.repository===CORE){requireThat(pin.sha===lock.source_commit,`Core workflow pin differs from copied canonical source: ${pin.file}`);continue;}
    heads??=['dev','main'].map(branch=>g.head(parent,branch));
    for(const head of heads){
      requireThat(fullSha(head),'Invalid current integration source');
      for(const path of ['.github/scripts',integrationConfiguration(profile.family),'.github/dev-propagation.json','.github/main-propagation.json',...(pin.workflow?['.github/workflows',pin.workflow]:[])])
        requireThat(object(parent,pin.sha,path)===object(parent,head,path),`Pinned integration tooling differs from current verified dev/main: ${pin.file} / ${path}`);
    }
  }
  if(all.some(p=>p.file==='release-receipt.yml'&&p.checkoutPath==='bootstrap/Python_WSS_Integration_Lib')){
    requireThat(all.filter(p=>p.file==='release-receipt.yml'&&p.checkoutPath==='bootstrap/Python_WSS_Integration_Lib').length===1&&all.filter(p=>p.file==='release-receipt.yml'&&p.repository===CORE&&p.checkoutPath==='core').length===1,'Receipt needs exact distinct engine/Core pin roles');
    requireThat(!parent||all.filter(p=>p.file==='release-receipt.yml'&&p.checkoutPath==='validator'&&p.repository===parent).length===1,'Receipt application needs exact native validator pin');
  }
  return {references:all.length,owner:parent};
}
export function refreshWorkflowPins({repository,profiles,lock,files,adapter,adapterLock,roles=pinRoles}){
  const {parent}=descriptor(repository,profiles,lock),registered=[CORE,...(parent?[parent]:[])];
  const needsAdapter=Object.values(files).some(text=>workflowPins(text,registered).some(p=>p.repository===parent));
  if(needsAdapter)requireThat(fullSha(adapter)&&adapterLock?.repository===parent&&adapterLock.source_commit===lock.source_commit&&canonical(adapterLock.origins)===canonical(lock.origins),'Commit and verify matching integration source/lock before refreshing application pins');
  const replacements={[CORE]:lock.source_commit,...(needsAdapter?{[parent]:adapter}:{})};
  return Object.fromEntries(Object.entries(files).map(([file,text])=>{
    const pins=workflowPins(text,registered).map(pin=>{
      const fixture=runtimeFixture(repository,file,pin,roles);
      return fixture?{...pin,replacement:fixture.commit}:pin;
    });
    if(file==='release-receipt.yml'&&text.includes('bootstrap/Python_WSS_Integration_Lib'))return [file,text]; // Generated receipt is regenerated separately, never regex-repinned.
    return [file,replacePins(text,pins,replacements)];
  }));
}

if(isEntrypoint(import.meta.url)){
  const [operation,name,target,workspace,adapter,originsWorkspace]=process.argv.slice(2);
  requireThat(['check','refresh'].includes(operation)&&/^[A-Za-z0-9_]+$/.test(name||'')&&target,'Usage: adapter-pins.mjs check|refresh <repository> <checkout> [workspace] [verified-adapter-sha] [origins-workspace]');
  const profiles=JSON.parse(sourceBytes({origin:'integration',path:'.github/scripts/wss-ci/receipt-targets.json'},originsWorkspace?resolve(originsWorkspace):workspace?resolve(workspace):undefined));
  const root=resolve(target),repository=`cwru-non-academic/${name}`,lock=JSON.parse(readFileSync(join(root,'.github/scripts/wss-ci.lock.json'),'utf8'));
  const directory=join(root,'.github/workflows'),files=Object.fromEntries(readdirSync(directory).filter(p=>/\.ya?ml$/.test(p)).map(p=>[p,readFileSync(join(directory,p),'utf8')]));
  if(operation==='check')console.log(JSON.stringify(verifyWorkflowPins({repository,profiles,lock,files},new GitHub())));
  else{
    const {parent}=descriptor(repository,profiles,lock);let adapterLock;
    if(adapter){
      requireThat(parent&&workspace&&fullSha(adapter),'A registered owner/workspace is required for adapter source');
      const ownerRoot=join(resolve(workspace),parent.split('/')[1]);
      requireThat(git(ownerRoot,'rev-parse','HEAD')===adapter&&git(ownerRoot,'status','--porcelain','--untracked-files=no')==='','Integration source must be the exact clean committed verified revision');
      adapterLock=JSON.parse(git(ownerRoot,'show',`${adapter}:.github/scripts/wss-ci.lock.json`));
    }
    const updated=refreshWorkflowPins({repository,profiles,lock,files,adapter,adapterLock});
    for(const [file,text] of Object.entries(updated))if(text!==files[file])writeFileSync(join(directory,file),text,'utf8');
    console.log(`Refreshed registered executable workflow pins: ${repository}; product sources and permissions unchanged`);
  }
}
