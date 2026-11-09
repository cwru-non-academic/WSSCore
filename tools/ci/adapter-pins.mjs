// Source-copy locks and executable workflow pins are separate trust boundaries.
// Refresh pins only after committing/verifying their owning integration tools.
import {execFileSync} from 'node:child_process';
import {parseDocument,isMap,isSeq,isScalar,isAlias} from './vendor/yaml/dist/index.js';
import {readFileSync,readdirSync,writeFileSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {GitHub} from '../release/github.mjs';
import {CORE,canonical,requireThat} from '../release/verify.mjs';
import {sourceBytes,expectedLock} from './sync-tooling.mjs';
import {createHash} from 'node:crypto';
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
import {integrationConfiguration} from '../release/channels.mjs';
const fullSha=value=>typeof value==='string'&&/^[a-f0-9]{40}$/.test(value);
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

// The upstream parser owns YAML syntax and ranges. This inventory owns only the
// workflow source roles; it never reparses run strings as workflow mappings.
export function workflowPins(text,repositories,{repository='@self'}={}){
  const document=parseDocument(text,{schema:'failsafe',keepSourceTokens:true,uniqueKeys:true,strict:true});
  requireThat(document.errors.length===0,`Invalid workflow YAML: ${document.errors.map(e=>e.message).join('; ')}`);
  requireThat(document.warnings.length===0,'Unsupported workflow YAML directive/tag');
  const anchors=new Map(),anchorNodes=new WeakSet();let expansions=0,visited=0;
  const unwrap=node=>{const seen=new Set();while(isAlias(node)){requireThat(!seen.has(node),'Cyclic workflow YAML alias');seen.add(node);node=node.resolve(document);requireThat(node,'Unresolved workflow YAML alias');}return node;};
  const inspect=(node,parents=new Set())=>{
    if(!node)return;requireThat(++visited<=100000&&parents.size<64,'Workflow YAML expansion/depth limit exceeded');
    requireThat(!node.tag,'YAML tags are unsupported source roles');
    if(node.anchor&&!anchorNodes.has(node)){requireThat(!anchors.has(node.anchor),'Duplicate workflow YAML anchor');anchors.set(node.anchor,node);anchorNodes.add(node);}
    requireThat(!parents.has(node),'Cyclic workflow YAML alias');const next=new Set(parents).add(node);
    if(isAlias(node)){requireThat(++expansions<=1000,'Workflow YAML alias expansion limit exceeded');const resolved=node.resolve(document);requireThat(resolved,'Unresolved workflow YAML alias');inspect(resolved,next);}
    else if(isMap(node))for(const pair of node.items){requireThat(isScalar(pair.key)&&typeof pair.key.value==='string'&&pair.key.value!=='<<','Workflow mapping keys must be unique scalar strings, without merges');inspect(pair.key,next);inspect(pair.value,next);}
    else if(isSeq(node))for(const item of node.items)inspect(item,next);
  };
  inspect(document.contents);
  requireThat(isMap(document.contents),'Workflow must be one YAML mapping');
  const get=(map,key)=>{map=unwrap(map);return isMap(map)?map.get(key,true):undefined;};
  const value=node=>{node=unwrap(node);requireThat(isScalar(node)&&typeof node.value==='string'&&node.value.length>0,'Workflow source requires a nonempty string scalar');return node.value;};
  const span=node=>{
    node=unwrap(node);const result=value(node);requireThat(!['BLOCK_LITERAL','BLOCK_FOLDED'].includes(node.type)&&!/[\r\n]/.test(text.slice(node.range[0],node.range[1])),'Workflow source requires a same-line scalar');
    const quoted=['QUOTE_SINGLE','QUOTE_DOUBLE'].includes(node.type);return {value:result,start:node.range[0]+(quoted?1:0),end:node.range[1]-(quoted?1:0)};
  };
  const result=[],jobs=unwrap(get(document.contents,'jobs'));requireThat(isMap(jobs),'Workflow jobs must be an explicit mapping');
  const sourceFields=new Set(['WSS_RECEIPT_CORE_SHA','WSS_RECEIPT_ENGINE_SHA','WSS_RECEIPT_VALIDATOR_SHA','tools_sha','tooling_sha','wrapper_sha','integration_sha','cli_sha','gui_sha','fixture_sha']);
  const add=(job,ref,extra,start,end,position=start)=>result.push({position,job,ref,...(fullSha(ref)?{sha:ref}:{}),...extra,start,end});
  const sourceFieldsIn=(job,node,position)=>{
    if(isAlias(node))position??=node.range[0];node=unwrap(node);
    if(isMap(node))for(const pair of node.items){if(sourceFields.has(pair.key.value)){const field=span(pair.value);add(job,field.value,{kind:'source-field',field:pair.key.value,repository},field.start,field.end,position??field.start);}else sourceFieldsIn(job,pair.value,position);}
    else if(isSeq(node))for(const item of node.items)sourceFieldsIn(job,item,position);
  };
  const inspectUses=(job,map,step,position)=>{
    const node=get(map,'uses');if(!node)return;const uses=span(node),locator=uses.value;
    requireThat(/^(?:[A-Za-z0-9_.-]+\/|\.\/|docker:\/\/)[^\s]+$/.test(locator),'Unsupported uses source scalar');
    const checkout=/^actions\/checkout@[^\s]+$/.test(locator);
    if(/actions\/checkout/i.test(locator))requireThat(checkout,'Unsupported checkout source syntax');
    if(checkout){
      requireThat(step,'Checkout uses must belong to a step mapping');
      const inputs=unwrap(get(map,'with'));requireThat(inputs===undefined||isMap(inputs),'Checkout with must be an explicit mapping');
      const fields=Object.fromEntries(['repository','ref','path'].filter(key=>get(inputs,key)!==undefined).map(key=>[key,span(get(inputs,key))]));
      if(fields.repository)requireThat(/^[A-Za-z0-9_./-]+$/.test(fields.repository.value)||fields.repository.value==='${{ github.repository }}','Unsupported checkout repository scalar');
      const repo=fields.repository?.value||repository,ref=fields.ref?.value||'@event',path=fields.path?.value||'.';
      requireThat(!fields.repository||fields.ref,'Explicit repository checkout requires explicit ref');requireThat(/^[A-Za-z0-9_./-]+$/.test(path),'Checkout source path must be literal');
      add(job,ref,{repository:repo,kind:'checkout',checkoutPath:path},fields.ref?.start??uses.start,fields.ref?.end??uses.start,position??fields.ref?.start??uses.start);
    }
    const use=/^((?:[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/|\.\/)\.github\/workflows\/[A-Za-z0-9_.-]+\.ya?ml)(?:@([^\s]+))?$/.exec(locator),action=/^(\.\/(?:[A-Za-z0-9_.-]+\/)*\.github\/actions\/[A-Za-z0-9_-]+)$/.exec(locator);
    if(action)add(job,'@self',{repository,kind:'local-action',action:action[1]},uses.start,uses.start,position??uses.start);
    if(/cwru-non-academic\/|^\.\//i.test(locator))requireThat(use||action,'Unclassified first-party action or reusable source');
    if(use){const local=use[1].startsWith('./'),workflow=local?use[1].slice(2):use[1].split('/').slice(2).join('/'),repo=local?repository:use[1].split('/').slice(0,2).join('/'),ref=local?'@self':use[2];
      requireThat(!step,'Reusable workflow uses must belong to a job mapping');requireThat(local||fullSha(ref),'External reusable workflows require immutable full SHAs');
      add(job,ref,{repository:repo,kind:local?'local-workflow':'workflow',workflow,...(!local?{valuePrefix:`${repo}/${workflow}@`}:{})},uses.start,uses.end,position??uses.start);
    }
  };
  for(const pair of jobs.items){
    const job=pair.key.value,body=unwrap(pair.value);requireThat(isMap(body),'Workflow job must be an explicit mapping');inspectUses(job,body,false);sourceFieldsIn(job,body);
    const steps=unwrap(get(body,'steps'));if(steps===undefined)continue;requireThat(isSeq(steps),'Workflow steps must be an explicit sequence');
    for(const item of steps.items){const step=unwrap(item),position=isAlias(item)?item.range[0]:undefined;requireThat(isMap(step),'Workflow step must be an explicit mapping');inspectUses(job,step,true,position);

    }
  }
  const occurrences=new Map();return result.sort((a,b)=>a.position-b.position).map(({position,...pin})=>{const key=sourceKey(pin),occurrence=occurrences.get(key)||0;occurrences.set(key,occurrence+1);return {...pin,occurrence};});
}
export function replacePins(text,pins,replacements){
  const edits=new Map();
  for(const pin of pins){const sha=pin.replacement||replacements[pin.repository];requireThat(fullSha(sha),'Replacement requires an immutable source revision');requireThat(pin.end>pin.start,'Cannot replace an implicit source');const key=JSON.stringify([pin.start,pin.end]),replacement=(pin.valuePrefix||'')+sha;requireThat(!edits.has(key)||edits.get(key).replacement===replacement,'Conflicting replacements for shared YAML source span');edits.set(key,{start:pin.start,end:pin.end,replacement});}
  let previous=text.length;for(const edit of [...edits.values()].sort((a,b)=>b.start-a.start)){requireThat(edit.end<=previous,'Overlapping workflow source replacements');text=text.slice(0,edit.start)+edit.replacement+text.slice(edit.end);previous=edit.start;}return text;
}
const sourceKey=p=>canonical({job:p.job,kind:p.kind,repository:p.repository,occurrence:p.occurrence||0,...(p.checkoutPath?{path:p.checkoutPath}:{}),...(p.workflow?{workflow:p.workflow}:{}),...(p.field?{field:p.field}:{}),...(p.action?{action:p.action}:{})});
const toolingRoles=new Set(['core','integration','engine','expected-core','expected-engine','expected-validator']);
const preservedRoles=new Set(['accepted-source','product','fixture','lock-core','lock-engine','manifest-engine','authenticated-tooling','authenticated-product-tooling','live-evidence','local-workflow','forwarded-source']);
export function classifyWorkflowSources({repository,files,roles=pinRoles,channel}){
  requireThat(channel===undefined||guardChannels.includes(channel),'Unsupported workflow channel');
  requireThat(Array.isArray(roles.source_catalogue),'Explicit workflow source catalogue required');
  const registrations=roles.source_catalogue.filter(r=>r.target===repository&&(!r.channels||(channel?r.channels.includes(channel):canonical(r.channels)===canonical(guardChannels)))),all=[];
  requireThat(registrations.length>0,'Unclassified workflow target');
  requireThat(new Set(registrations.map(r=>r.workflow)).size===registrations.length,'Duplicate workflow source registration');
  for(const registration of registrations)requireThat(Object.hasOwn(files,registration.workflow),`Missing registered workflow: ${registration.workflow}`);
  for(const [file,text] of Object.entries(files)){
    const pins=workflowPins(text,[],{repository}),registered=registrations.find(r=>r.workflow===file)?.sources||[];
    const groups=new Map();
    for(const entry of registered){const key=sourceKey(entry);requireThat(!groups.has(key),'Ambiguous workflow source role');groups.set(key,entry);}
    const seen=new Set();
    for(const pin of pins){
      const key=sourceKey(pin),entry=groups.get(key);requireThat(entry&&!seen.has(key),`Unclassified or duplicate workflow source: ${file} / ${pin.job} / ${pin.checkoutPath||pin.workflow||pin.field||pin.kind}`);seen.add(key);
      requireThat(toolingRoles.has(entry.role)||preservedRoles.has(entry.role),'Unknown workflow source role');
      if(preservedRoles.has(entry.role)){
        const expected=entry.refs?entry.refs[channel]:entry.ref;
        requireThat(expected!==undefined&&pin.ref===expected,`Registered ${entry.role} source changed: ${file} / ${pin.job} / ${pin.checkoutPath||pin.field||pin.kind}`);
      }else requireThat(fullSha(pin.ref)||(entry.role==='integration'&&pin.kind==='checkout'&&['dev','main'].includes(pin.ref)),`Executable source must be literal immutable SHA: ${file} / ${pin.job}`);
      if(pin.kind==='local-workflow')requireThat(Object.hasOwn(files,pin.workflow.replace('.github/workflows/','')),'Local reusable callee is absent');
      all.push({...pin,file,role:entry.role});
    }
    requireThat(seen.size===registered.length,`Missing registered workflow source or exact expected guard: ${file}`);
  }
  // A runtime fixture is an immutable product role, not a general tooling waiver.
  requireThat(Array.isArray(roles.runtime_fixtures),'Runtime fixture roles must be explicit');
  const fixtureKeys=roles.runtime_fixtures.map(f=>canonical([f.target,f.workflow,f.checkout_path,f.repository]));
  requireThat(new Set(fixtureKeys).size===fixtureKeys.length,'Ambiguous executable fixture role');
  for(const fixture of roles.runtime_fixtures){
    requireThat(fullSha(fixture.commit),'Runtime fixture requires immutable source');
    const matches=all.filter(p=>repository===fixture.target&&p.file===fixture.workflow&&p.checkoutPath===fixture.checkout_path&&p.repository===fixture.repository);
    requireThat(matches.length<=1,'Ambiguous executable fixture role');
    if(matches.length)requireThat(matches[0].role==='fixture'&&matches[0].ref===fixture.commit,'Reviewed runtime fixture pin changed');
  }
  return all;
}
function descriptor(repository,profiles,lock){
  const matches=Object.values(profiles).filter(p=>p.target.repository===repository);
  requireThat(matches.length===1&&lock.repository===repository&&lock.source_repository===CORE&&fullSha(lock.source_commit),'Unknown target or canonical source lock');
  const profile=matches[0],wrapper=profiles[profile.wrapper];
  requireThat(wrapper?.role==='wrapper'&&wrapper.family===profile.family,'Invalid integration owner');
  return {profile,parent:profile.role==='wrapper'?null:wrapper.target.repository};
}
function verifyPins(input,g,candidate){
  const {repository,profiles,lock,files}=input,{profile,parent}=descriptor(repository,profiles,lock),all=classifyWorkflowSources(input),integration=lock.origins?.integration;
  const cache=new Map(),object=(repo,ref,path)=>{const key=JSON.stringify([repo,ref,path]);if(!cache.has(key)){const value=g.object(repo,ref,path);requireThat(value&&fullSha(value.sha),'Expected immutable source object');cache.set(key,value.sha);}return cache.get(key);};
  const extraBlobs=(input.roles?.parent_executable_blobs??pinRoles.parent_executable_blobs??[]).filter(entry=>entry.target===repository);
  requireThat(new Set(extraBlobs.map(entry=>entry.repository)).size===extraBlobs.length,'Duplicate parent executable blob registration');
  for(const entry of extraBlobs)requireThat(entry.repository===parent&&Array.isArray(entry.paths)&&entry.paths.length>0&&new Set(entry.paths).size===entry.paths.length&&entry.paths.every(path=>typeof path==='string'&&!path.split('/').includes('..')),'Invalid parent executable blob registration');
  const regularObject=(repo,ref,path)=>{const value=g.object(repo,ref,path);requireThat(value?.type==='blob'&&['100644','100755'].includes(value.mode)&&fullSha(value.sha)&&(value.path===undefined||value.path===path),'Expected registered regular executable blob: '+path);return value.sha;};
  let heads;const ownerPins=[];
  for(const pin of all){
    if(!toolingRoles.has(pin.role))continue;
    requireThat(fullSha(pin.ref),`Floating executable source: ${pin.file} / ${pin.job}`);
    if(['core','expected-core'].includes(pin.role)){requireThat(pin.ref===lock.source_commit,`Core workflow pin differs from copied canonical source: ${pin.file}`);continue;}
    if(['engine','expected-engine'].includes(pin.role)){requireThat(pin.ref===integration?.commit&&(['source-field'].includes(pin.kind)||pin.repository===integration.repository),'Receipt bootstrap differs from immutable integration origin');continue;}
    requireThat(parent&&(['source-field'].includes(pin.kind)||pin.repository===parent),'Executable integration owner differs from registered wrapper');
    if(candidate)requireThat(pin.ref===candidate.selected,'Pinned integration differs from selected future channel owner');
    const record=g.get(`repos/${parent}/contents/.github/scripts/wss-ci.lock.json?ref=${pin.ref}`);
    requireThat(record?.encoding==='base64','Pinned native validator lock unavailable');
    const nativeLock=JSON.parse(Buffer.from(record.content,'base64'));
    requireThat(nativeLock.repository===parent&&nativeLock.source_commit===lock.source_commit&&canonical(nativeLock.origins)===canonical(lock.origins),'Pinned native validator uses another canonical source lock');
    ownerPins.push(pin);
    // Preserve the production receipt-specific rule and live parent checks.
    if(!candidate&&pin.file==='release-receipt.yml'&&(pin.checkoutPath==='validator'||pin.role==='expected-validator'))continue;
    heads??=candidate?candidate.heads:['dev','main'].map(branch=>g.head(parent,branch));
    for(const head of heads){
      requireThat(fullSha(head),'Invalid current integration source');
      for(const path of ['.github/scripts',integrationConfiguration(profile.family),'.github/dev-propagation.json','.github/main-propagation.json',...(pin.workflow?['.github/workflows',pin.workflow]:[])])
        requireThat(object(parent,pin.ref,path)===object(parent,head,path),`Pinned integration tooling differs from ${candidate?'future':'current verified'} dev/main: ${pin.file} / ${path}`);
      for(const entry of extraBlobs)for(const path of entry.paths)
        requireThat(regularObject(parent,pin.ref,path)===regularObject(parent,head,path),`Pinned integration executable blob differs from ${candidate?'future':'current verified'} dev/main: ${pin.file} / ${path}`);
    }
  }
  // Every ordinary invocation of an owner in a receiver binds to one source,
  // including repeated matrix jobs, reusable calls and receipt expected fields.
  requireThat(new Set(ownerPins.map(p=>p.ref)).size<=1,'Caller/callee tooling source mismatch');
  return {references:all.length,executable:all.filter(p=>toolingRoles.has(p.role)).length,owner:parent};
}
export function verifyWorkflowPins(input,g){
  requireThat(!('sources' in input)&&!('candidate' in input),'Candidate authority is not a deployed verification input');
  return verifyPins(input,g);
}
export function refreshWorkflowPins(input){
  const {repository,profiles,lock,files,adapter,adapterLock}=input,{parent}=descriptor(repository,profiles,lock),all=classifyWorkflowSources(input);
  const needsAdapter=all.some(p=>['integration','expected-validator'].includes(p.role));
  if(needsAdapter)requireThat(fullSha(adapter)&&adapterLock?.repository===parent&&adapterLock.source_commit===lock.source_commit&&canonical(adapterLock.origins)===canonical(lock.origins),'Commit and verify matching integration source/lock before refreshing application pins');
  const replacements={[CORE]:lock.source_commit,...(needsAdapter?{[parent]:adapter}:{})};
  return Object.fromEntries(Object.entries(files).map(([file,text])=>{
    if(file==='release-receipt.yml')return [file,text]; // Existing canonical renderer alone writes receipts.
    const pins=all.filter(p=>p.file===file&&['core','expected-core','integration'].includes(p.role));
    for(const pin of pins){const replacement=(pin.valuePrefix||'')+replacements[pin.repository];for(const consumer of all.filter(p=>p.file===file&&p.start===pin.start&&p.end===pin.end&&!pins.includes(p)))requireThat(replacement===(consumer.valuePrefix||'')+consumer.ref,'Conflicting preserved consumer of shared YAML source span');}
    return [file,replacePins(text,pins,replacements)];
  }));
}

// Local prepublication inspection only. This reader has no network or mutable
// head implementation and accepts no unlisted repo/commit/path request.
export function createLocalSourceReader(sources){
  requireThat(Array.isArray(sources)&&sources.length>0,'Explicit immutable local source mappings required');
  const labels=new Set(['dev','main','tooling-origin','tooling-source','reviewed-dev','reviewed-main']),entries=new Map(),slots=new Set();
  const captured=canonical(sources);
  for(const s of sources){
    requireThat(exactKeys(s,['repository','channel','commit','tree','path'])&&/^cwru-non-academic\/[A-Za-z0-9_-]+$/.test(s.repository)&&labels.has(s.channel)&&fullSha(s.commit)&&fullSha(s.tree)&&typeof s.path==='string','Invalid immutable local source mapping');
    const slot=JSON.stringify([s.repository,s.channel]);requireThat(!slots.has(slot),'Duplicate local repository/channel source');slots.add(slot);
    requireThat(git(s.path,'rev-parse',`${s.commit}^{commit}`)===s.commit&&git(s.path,'rev-parse',`${s.commit}^{tree}`)===s.tree,'Local commit/tree differs from declared identity');
    const key=JSON.stringify([s.repository,s.commit]);if(entries.has(key))requireThat(entries.get(key).tree===s.tree,'Conflicting local commit tree');else entries.set(key,{...s});
  }
  const entry=(repo,ref)=>{requireThat(fullSha(ref),'Local source reads require immutable commit');const s=entries.get(JSON.stringify([repo,ref]));requireThat(s,`Unmapped immutable local source: ${repo}@${ref}`);return s;};
  const pathOk=path=>requireThat(typeof path==='string'&&/^[A-Za-z0-9_.\/-]+$/.test(path)&&!path.split('/').includes('..')&&!path.startsWith('/')&&!/(?:selected_core_source\.py|Test-MatlabSelectedIntegrity\.ps1)$/.test(path),'Invalid or excluded local source path');
  const reader={
    head(){throw Error('Local candidate reader cannot authorize deployed heads');},
    object(repo,ref,path){pathOk(path);const s=entry(repo,ref),out=git(s.path,'ls-tree',ref,'--',path),match=/^(\d{6}) (blob|tree|commit) ([a-f0-9]{40})\t(.+)$/.exec(out);requireThat(match&&match[4]===path,'Expected unique local source object');return {mode:match[1],type:match[2],sha:match[3],path};},
    bytes(repo,ref,path){pathOk(path);const s=entry(repo,ref),o=reader.object(repo,ref,path);requireThat(o.type==='blob'&&['100644','100755'].includes(o.mode),'Expected regular local source blob');return execFileSync('git',['-C',s.path,'show',`${ref}:${path}`],{maxBuffer:32*1024*1024});},
    text(repo,ref,path){return reader.bytes(repo,ref,path).toString('utf8');},
    get(path){let m=/^repos\/(cwru-non-academic\/[^/]+)\/git\/commits\/([a-f0-9]{40})$/.exec(path);if(m){const s=entry(m[1],m[2]);return {sha:s.commit,tree:{sha:s.tree}};}
      m=/^repos\/(cwru-non-academic\/[^/]+)\/contents\/(.+)\?ref=([a-f0-9]{40})$/.exec(path);requireThat(m,'Unsupported local candidate read');return {encoding:'base64',content:Buffer.from(reader.text(m[1],m[3],m[2])).toString('base64')};},
    workflows(repo,ref){const s=entry(repo,ref),paths=git(s.path,'ls-tree','-r','--name-only',ref,'--','.github/workflows').split(/\r?\n/).filter(p=>/\.ya?ml$/.test(p));return Object.fromEntries(paths.map(p=>[p.replace('.github/workflows/',''),reader.text(repo,ref,p)]));},
    finish(){requireThat(canonical(sources)===captured,'Local source mappings changed after capture');for(const s of entries.values())requireThat(git(s.path,'rev-parse',`${s.commit}^{tree}`)===s.tree,'Local source identity changed after capture');}
  };return reader;
}
// Pure pin binding subcheck for synthetic regression fixtures. Full local fleet
// inspection below additionally verifies every canonical managed blob.
export function verifyCandidatePinBindings(input,g){
  const {repository,channel,sources,profiles,lock}=input;
  requireThat(guardChannels.includes(channel)&&Array.isArray(sources),'Explicit supported candidate channel and source snapshots required');
  const select=(repo,ch)=>{const found=sources.filter(s=>s.repository===repo&&s.channel===ch);requireThat(found.length===1&&fullSha(found[0].commit)&&fullSha(found[0].tree),'Explicit unique immutable future source required');const s=found[0],actual=g.get(`repos/${repo}/git/commits/${s.commit}`);requireThat(actual?.sha===s.commit&&actual.tree?.sha===s.tree,'Candidate commit/tree differs from explicit snapshot');return s;};
  const target=select(repository,channel),{parent}=descriptor(repository,profiles,lock),candidate=parent?{selected:select(parent,channel).commit,heads:guardChannels.map(ch=>select(parent,ch).commit)}:undefined;
  const report=verifyPins(input,g,candidate);
  return {...report,mode:'immutable-local-candidate',scope:'workflow-pins-only',channel,commit:target.commit,tree:target.tree,deployment_authorized:false};
}
export function verifyCandidateWorkflowPins({repository,channel,profiles,sources,roles=pinRoles}){
  requireThat(guardChannels.includes(channel),'Unsupported candidate channel');
  const g=createLocalSourceReader(sources),select=(repo,ch)=>{const found=sources.filter(s=>s.repository===repo&&s.channel===ch);requireThat(found.length===1,`Explicit future ${repo} / ${ch} source required`);return found[0];};
  const target=select(repository,channel),files=g.workflows(repository,target.commit);
  if(repository===CORE){
    const manifest=JSON.parse(g.text(CORE,target.commit,'tools/ci/sync-tooling.json'));
    requireThat(canonical(manifest)===canonical(JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)))),'Candidate Core manifest differs from executing canonical definitions');
    for(const origin of Object.values(manifest.origins)){const mapped=sources.filter(s=>s.repository===origin.repository&&s.commit===origin.commit);requireThat(mapped.length>0&&origin.ref===`refs/heads/ci/tooling/${origin.commit}`,'Unmapped immutable Core manifest origin');}
    const pins=classifyWorkflowSources({repository,channel,files,roles});g.finish();
    return {references:pins.length,mode:'immutable-local-candidate',scope:'core-workflow-source-bindings',channel,commit:target.commit,tree:target.tree,deployment_authorized:false};
  }
  const lock=JSON.parse(g.text(repository,target.commit,'.github/scripts/wss-ci.lock.json')),{parent}=descriptor(repository,profiles,lock);
  // Source locks are claims until their managed bytes match immutable Core/origin
  // definitions. Reuse sync-tooling's expectedLock/sourceBytes, not a new copier.
  const integration=lock.origins?.integration,origin=sources.find(s=>s.repository===integration?.repository&&s.commit===integration?.commit);
  requireThat(origin,'Immutable executable integration origin is not mapped');
  requireThat(canonical(JSON.parse(g.text(CORE,lock.source_commit,'tools/ci/sync-tooling.json')))===canonical(JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)))),'Candidate Core manifest differs from executing canonical definitions');
  const readSource=entry=>{const source=entry.origin==='core'?{repository:CORE,commit:lock.source_commit}:lock.origins[entry.origin];requireThat(source,'Unknown immutable mapped origin');return g.bytes(source.repository,source.commit,entry.path);};
  const expected=expectedLock(repository.split('/')[1],lock.source_commit,undefined,readSource);
  requireThat(canonical(lock)===canonical(expected),'Candidate lock differs from canonical source definitions');
  const verifyManaged=(repo,ref)=>{const l=JSON.parse(g.text(repo,ref,'.github/scripts/wss-ci.lock.json'));requireThat(l.source_commit===lock.source_commit&&canonical(l.origins)===canonical(lock.origins),'Future parent lock differs from candidate authority');
    const e=expectedLock(repo.split('/')[1],lock.source_commit,undefined,readSource);requireThat(canonical(l)===canonical(e),'Future parent canonical lock mismatch');
    for(const [path,value] of Object.entries(e.files)){const bytes=g.bytes(repo,ref,path);requireThat(hash(bytes)===value.sha256,`Candidate managed source drift: ${repo}/${path}`);}
  };
  verifyManaged(repository,target.commit);
  const candidate=parent?{selected:select(parent,channel).commit,heads:guardChannels.map(ch=>select(parent,ch).commit)}:undefined;
  if(parent)for(const ref of candidate.heads)verifyManaged(parent,ref);
  const report=verifyCandidatePinBindings({repository,channel,profiles,lock,files,roles,sources},g);g.finish();
  return {...report,scope:'workflow-pins-and-canonical-managed-bytes',mode:'immutable-local-candidate',channel,commit:target.commit,tree:target.tree,deployment_authorized:false};
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
  if(operation==='check')console.log(JSON.stringify(verifyWorkflowPins({repository,profiles,lock,files,channel:process.env.WSS_CI_CHANNEL||process.env.GITHUB_BASE_REF||process.env.GITHUB_REF_NAME},new GitHub())));
  else{
    const {parent}=descriptor(repository,profiles,lock);let adapterLock;
    if(adapter){
      requireThat(parent&&workspace&&fullSha(adapter),'A registered owner/workspace is required for adapter source');
      const ownerRoot=join(resolve(workspace),parent.split('/')[1]);
      requireThat(git(ownerRoot,'rev-parse','HEAD')===adapter&&git(ownerRoot,'status','--porcelain','--untracked-files=no')==='','Integration source must be the exact clean committed verified revision');
      adapterLock=JSON.parse(git(ownerRoot,'show',`${adapter}:.github/scripts/wss-ci.lock.json`));
    }
    const updated=refreshWorkflowPins({repository,profiles,lock,files,adapter,adapterLock,channel:process.env.WSS_CI_CHANNEL||process.env.GITHUB_BASE_REF||process.env.GITHUB_REF_NAME});
    for(const [file,text] of Object.entries(updated))if(text!==files[file])writeFileSync(join(directory,file),text,'utf8');
    console.log(`Refreshed registered executable workflow pins: ${repository}; product sources and permissions unchanged`);
  }
}

if(isEntrypoint(import.meta.url)){try{main();}catch(error){console.error(error.message);process.exitCode=1;}}
