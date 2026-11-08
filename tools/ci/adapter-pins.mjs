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

// These are executable guard roles, not a second native-job or receipt policy.
// Authority is an explicitly reviewed immutable owner in the existing registry;
// a deployed branch/candidate is only compared with that authority, never adopted.
const guardRepository='cwru-non-academic/Matlab_WSS_Integration_Lib';
const guardPaths=Object.freeze({
  native_evidence_guard:'.github/scripts/Complete-MatlabPreflight.ps1',
  promotion_contract_test:'.github/scripts/tests/Test-MatlabPromotion.ps1',
  merge_evidence_guard:'.github/scripts/MatlabPromotion.ps1'
});
const guardChannels=Object.freeze(['dev','main']);
const exactKeys=(value,keys)=>value&&typeof value==='object'&&!Array.isArray(value)&&canonical(Object.keys(value).sort())===canonical([...keys].sort());
export function stageGuardPolicies(){
  return Object.fromEntries(guardChannels.map(channel=>[channel,JSON.parse(readFileSync(new URL(`./${channel}-propagation.json`,import.meta.url),'utf8'))]));
}

// Read-only validation/projection shared by preparation, adoption and BOTH freezes.
// A projection is returned only from valid, stable reviewed owner objects and
// matching receiver sources. Policy drift is reported together for both channels.
export function inspectStageGuardPins({policies,roles=pinRoles,heads,deployed=true},g){
  const inputIdentity=canonical({policies,roles,heads,deployed});
  const issues=[],sourceIssues=[],captures=new Map(),commits=new Map(),projection={dev:{},main:{}};
  const issue=(message,source=false)=>{issues.push(message);if(source)sourceIssues.push(message);};
  const read=(label,fn)=>{try{return fn();}catch{issue(`${label}: unavailable`,true);return undefined;}};
  const readCommit=(repository,ref)=>{
    const label=`Guard source ${repository}@${ref}`,key=JSON.stringify([repository,ref]);
    if(commits.has(key))return commits.get(key);
    const value=read(label,()=>g.get(`repos/${repository}/git/commits/${ref}`));
    if(!value||value.sha!==ref||!fullSha(value.tree?.sha)){
      issue(`${label}: expected exact immutable commit`,true);
      commits.set(key,null);return null;
    }
    const capture={repository,ref,tree:value.tree.sha};commits.set(key,capture);return capture;
  };
  const readBlob=(repository,ref,path)=>{
    const key=JSON.stringify([repository,ref,path]);
    if(captures.has(key))return captures.get(key);
    const label=`Guard object ${repository}@${ref}/${path}`;
    const value=read(label,()=>g.object(repository,ref,path));
    if(!value||value.type!=='blob'||!['100644','100755'].includes(value.mode)||!fullSha(value.sha)||(value.path!==undefined&&value.path!==path)){
      issue(`${label}: expected regular blob at registered path`,true);
      captures.set(key,null);return null;
    }
    const capture={repository,ref,path,sha:value.sha,type:value.type,mode:value.mode};captures.set(key,capture);return capture;
  };
  if(typeof deployed!=='boolean')issue('Guard validation requires an explicit boolean deployment mode',true);
  const registrations=roles?.stage_guards;
  if(!Array.isArray(registrations))issue('Stage guard roles must be explicit and complete',true);
  const valid=[];
  for(const role of Object.keys(guardPaths)){
    const matches=Array.isArray(registrations)?registrations.filter(entry=>entry?.role===role):[];
    if(matches.length!==1){issue(`Expected exactly one stage guard role: ${role}`,true);continue;}
    const entry=matches[0];
    if(!exactKeys(entry,['role','repository','path','policy_slot','channels','reviewed_sources'])||
      entry.repository!==guardRepository||entry.path!==guardPaths[role]||entry.policy_slot!==role||
      canonical(entry.channels)!==canonical(guardChannels)||!exactKeys(entry.reviewed_sources,guardChannels)||
      !guardChannels.every(channel=>fullSha(entry.reviewed_sources[channel]))){
      issue(`Invalid registered ownership/path/channel/immutable source for stage guard: ${role}`,true);continue;
    }
    valid.push(entry);
  }
  for(const entry of Array.isArray(registrations)?registrations:[])
    if(!Object.hasOwn(guardPaths,entry?.role||''))issue(`Unknown stage guard role: ${entry?.role}`,true);
  if(!exactKeys(policies,guardChannels))issue('Stage guard consistency requires both dev and main policies');
  const targets={};
  for(const channel of guardChannels){
    const p=policies?.[channel],matches=Array.isArray(p?.libraries)?p.libraries.filter(t=>t?.id==='matlab'):[];
    if(p?.channel!==channel||matches?.length!==1||matches[0].repository!==guardRepository){
      issue(`Invalid MATLAB policy ownership on ${channel}`);continue;
    }
    targets[channel]=matches[0];
    for(const role of Object.keys(guardPaths)){
      const pin=matches[0][role];
      if(!exactKeys(pin,['path','sha'])||pin.path!==guardPaths[role]||!fullSha(pin.sha))issue(`Pin the registered ${role} before handoff on ${channel}`);
    }
  }
  const receiverHeads={};
  if(heads!==undefined&&!exactKeys(heads,guardChannels))issue('Guard receiver sources require exact dev/main commits',true);
  if(!deployed&&heads===undefined)issue('Candidate guard validation requires explicitly selected receiver sources',true);
  for(const branch of guardChannels){
    const ref=heads?.[branch]??(deployed?read(`Guard receiver ${branch} head`,()=>g.head(guardRepository,branch)):undefined);
    if(!fullSha(ref)){issue(`Invalid immutable guard receiver source on ${branch}`,true);continue;}
    receiverHeads[branch]=ref;readCommit(guardRepository,ref);
  }
  for(const entry of valid){
    const owners={};
    for(const channel of guardChannels){
      const ref=entry.reviewed_sources[channel];
      if(readCommit(entry.repository,ref))owners[channel]=readBlob(entry.repository,ref,entry.path);
    }
    if(owners.dev&&owners.main&&owners.dev.sha!==owners.main.sha)issue(`Reviewed owner guard bytes differ between channels: ${entry.role}`,true);
    if(owners.dev&&owners.main&&owners.dev.sha===owners.main.sha){
      for(const channel of guardChannels){
        projection[channel][entry.policy_slot]={path:entry.path,sha:owners[channel].sha};
        const pin=targets[channel]?.[entry.policy_slot];
        if(pin&&canonical(pin)!==canonical(projection[channel][entry.policy_slot]))issue(`${channel} ${entry.role} policy is not the reviewed version`);
      }
    }
    for(const branch of guardChannels){
      if(!receiverHeads[branch])continue;
      const receiver=readBlob(entry.repository,receiverHeads[branch],entry.path);
      if(receiver&&owners[branch]&&receiver.sha!==owners[branch].sha)issue(`${entry.role} is not the reviewed version on ${branch}`,true);
    }
  }
  // Detach captured scalars and re-read after collection. Never silently refresh
  // a moved receiver, changed commit/object response or the caller's input.
  for(const capture of commits.values())if(capture){
    const {repository,ref,tree}=capture,value=read(`Guard source recheck ${repository}@${ref}`,()=>g.get(`repos/${repository}/git/commits/${ref}`));
    if(!value||value.sha!==ref||value.tree?.sha!==tree)issue(`Guard source changed after capture: ${repository}@${ref}`,true);
  }
  for(const capture of captures.values())if(capture){
    const {repository,ref,path,sha,type,mode}=capture,value=read(`Guard object recheck ${repository}@${ref}/${path}`,()=>g.object(repository,ref,path));
    if(!value||value.sha!==sha||value.type!==type||value.mode!==mode||(value.path!==undefined&&value.path!==path))issue(`Guard object changed after capture: ${repository}@${ref}/${path}`,true);
  }
  if(deployed)for(const branch of guardChannels){
    const current=read(`Guard receiver ${branch} final head`,()=>g.head(guardRepository,branch));
    if(current!==receiverHeads[branch])issue(`Guard receiver source moved on ${branch}`,true);
  }
  if(canonical({policies,roles,heads,deployed})!==inputIdentity)issue('Guard registration/policy/source input changed after capture',true);
  return {issues,...(sourceIssues.length===0?{projection}:{}),sources:{reviewed:valid.map(e=>({role:e.role,repository:e.repository,commits:{...e.reviewed_sources}})),receivers:receiverHeads},deployed};
}
export function assertStageGuardPins(input,g){
  const report=inspectStageGuardPins(input,g);
  requireThat(report.issues.length===0,`Stage guard consistency failed:\n${report.issues.join('\n')}`);
  return report;
}

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

function main(){
  const [operation,name,target,workspace,adapter,originsWorkspace]=process.argv.slice(2);
  if(['check-guards','check-guard-candidates'].includes(operation)){
    requireThat(operation==='check-guards'?process.argv.length===3:process.argv.length===4,'Usage: adapter-pins.mjs check-guards | check-guard-candidates <receiver-sources.json>');
    const report=inspectStageGuardPins({policies:stageGuardPolicies(),...(operation==='check-guard-candidates'?{heads:JSON.parse(readFileSync(resolve(name),'utf8')),deployed:false}:{})},new GitHub());
    console.log(JSON.stringify(report,null,2));
    requireThat(report.issues.length===0,'Stage guard consistency failed; review every reported mismatch before adoption');return;
  }
  requireThat(['check','refresh'].includes(operation)&&/^[A-Za-z0-9_]+$/.test(name||'')&&target,'Usage: adapter-pins.mjs check|refresh <repository> <checkout> [workspace] [verified-adapter-sha] [origins-workspace]');
  // Library-first/Core-last deployment is checked before any local pin writes.
  assertStageGuardPins({policies:stageGuardPolicies()},new GitHub());
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

if(isEntrypoint(import.meta.url)){try{main();}catch(error){console.error(error.message);process.exitCode=1;}}
