import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {workflowPins,replacePins,classifyWorkflowSources,verifyWorkflowPins,refreshWorkflowPins,verifyCandidatePinBindings,verifyCandidateWorkflowPins,createLocalSourceReader} from './adapter-pins.mjs';
const sha=n=>String(n).repeat(40),core='cwru-non-academic/WSSCore',parent='cwru-non-academic/Python_WSS_Integration_Lib',repository='cwru-non-academic/CLI_Python_WSS_Application';
const profiles={wrapper:{role:'wrapper',family:'python',wrapper:'wrapper',target:{repository:parent}},app:{role:'cli',family:'python',wrapper:'wrapper',target:{repository}}};
const lock={repository,source_repository:core,source_commit:sha(1),origins:{integration:{repository:parent,commit:sha(2)}}};
const checkout=(repo,ref,path='tools')=>`      - uses: actions/checkout@v4\n        with:\n          repository: ${repo}\n          ref: ${ref}\n          path: ${path}\n`;
const workflow=body=>`name: Synthetic source contracts\non: push\njobs:\n  check:\n    runs-on: ubuntu-latest\n    steps:\n${body}`;
const guard=ref=>`          if ($lock.source_repository -cne '${core}' -or $lock.source_commit -cne '${ref}' -or $lock.source_ref -cne "refs/heads/ci/tooling/$($lock.source_commit)") { throw 'Unexpected reviewed package verifier lock.' }\n`;
const register=(files,role,owner=repository)=>({runtime_fixtures:[],source_catalogue:Object.entries(files).map(([file,text])=>({target:owner,workflow:file,sources:workflowPins(text,[],{repository:owner}).map((p,i)=>{const {ref,sha,start,end,...identity}=p,r=role(p,i);return {...identity,role:r,...(['core','integration','engine','expected-core','expected-engine','expected-validator'].includes(r)?{}:{ref})};})}))});
const files={'ci.yml':workflow(checkout(parent,sha(3))+checkout(core,sha(1),'core'))};
const roles=register(files,p=>p.repository===core?'core':'integration');
const input={repository,channel:'dev',profiles,lock,files,roles};
const nativeLock={...lock,repository:parent};
const g={head:(_r,b)=>b==='dev'?sha(4):sha(5),object:()=>({sha:sha(6)}),get:path=>path.includes('/contents/')?{encoding:'base64',content:Buffer.from(JSON.stringify(nativeLock)).toString('base64')}:{sha:path.split('/').at(-1),tree:{sha:sha(6)}}};
const changed=files=>({...input,files});
const refresh=data=>refreshWorkflowPins({...input,adapter:sha(8),adapterLock:nativeLock,...data});

test('every literal checkout is verified against live deployed parents and copied Core authority',()=>{
 assert.equal(verifyWorkflowPins(input,g).references,2);
 assert.throws(()=>verifyWorkflowPins(input,{...g,object:(_r,ref,path)=>({sha:ref===sha(3)&&path==='.github/scripts'?sha(9):sha(6)})}),/Pinned integration tooling/);
 assert.throws(()=>verifyWorkflowPins(changed({'ci.yml':files['ci.yml'].replace(sha(1),sha(9))}),g),/Core workflow pin/);
});
test('additional quoted/commented checkouts and YAML aliases cannot hide outside the inventory',()=>{
 for(const body of [checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4',"uses: 'actions/checkout@v4'"),checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4','uses: actions/checkout@v4 # source'),checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4','uses: actions/Checkout@v4'),checkout(parent,sha(9),'hidden').replace('- uses:','-  uses:'),checkout(parent,sha(9),'hidden').replace('uses:',"'uses':"),checkout(parent,sha(9),'hidden').replace('repository:',"'repository':"),checkout(parent,sha(9),'hidden').replace('repository:','repository :'),checkout(parent,sha(9),'hidden').replace(parent,'>-\n            '+parent),'      - uses: *hidden\n',checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4','uses: >-\n          actions/checkout@v4'),checkout(parent,sha(9),'hidden').replace('repository: '+parent,'repository:\n            '+parent),'  hidden: &source\n    steps: *checkout\n','      - {uses: actions/checkout@v4}\n',`  hidden: {uses: ${parent}/.github/workflows/_worker.yml@${sha(9)}}\n`])assert.throws(()=>verifyWorkflowPins(changed({'ci.yml':files['ci.yml']+body}),g));
});
test('trailing uses comments preserve action and first-party source classification',()=>{
 const action='      - uses: actions/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349 # v2\n';
 const f={'ci.yml':files['ci.yml']+action};assert.equal(verifyWorkflowPins(changed(f),g).references,2);
 assert.equal(verifyWorkflowPins(changed({'ci.yml':files['ci.yml']+action.replace('# v2','# actions/checkout and cwru-non-academic/comment-only')}),g).references,2,'Comment text is never an executable source');
 const commented=files['ci.yml'].replaceAll('uses: actions/checkout@v4','uses: actions/checkout@v4 # reviewed checkout');
 assert.equal(verifyWorkflowPins(changed({'ci.yml':commented}),g).references,2);
 assert.equal(refresh({files:{'ci.yml':commented}})['ci.yml'],commented.replace(sha(3),sha(8)),'Refresh retains comments and replaces only the actual registered ref');
 const callee=`  reusable:\n    uses: ${parent}/.github/workflows/_python-validation.yml@${sha(3)} # reviewed callee\n`,pair={'ci.yml':files['ci.yml']+callee},r=register(pair,p=>p.repository===core?'core':'integration');
 assert.equal(verifyWorkflowPins({...input,files:pair,roles:r},g).references,3);
 assert.throws(()=>verifyWorkflowPins(changed({'ci.yml':files['ci.yml']+callee}),g),/Unclassified/,'A commented reusable call cannot hide outside the inventory');
 for(const text of [files['ci.yml']+checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4','uses: actions/checkout@v4 # extra'),files['ci.yml']+checkout(parent,sha(9),'hidden').replace('uses: actions/checkout@v4',"uses: 'actions/checkout@v4' # quoted"),files['ci.yml'].replace(`ref: ${sha(3)}`,`ref: ${sha(3)} # ref remains unsupported`)])assert.throws(()=>verifyWorkflowPins(changed({'ci.yml':text}),g));
});
test('parent lock, family execution configuration and both live branch objects remain required',()=>{
 for(const path of ['.github/scripts','.github/python-ci.json','.github/dev-propagation.json','.github/main-propagation.json'])assert.throws(()=>verifyWorkflowPins(input,{...g,object:(_r,ref,p)=>({sha:ref===sha(5)&&p===path?sha(9):sha(6)})}),/Pinned integration tooling/);
 assert.throws(()=>verifyWorkflowPins(input,{...g,get:()=>({encoding:'base64',content:Buffer.from(JSON.stringify({...nativeLock,source_commit:sha(9)})).toString('base64')})}),/another canonical source/);
 assert.throws(()=>refresh({adapterLock:{...nativeLock,source_commit:sha(9)}}),/matching integration source/);
});
test('stale, missing, duplicate, case-insensitive and substituted exact Core assertions are rejected',()=>{
 const f={'wss-compatibility.yml':workflow(guard(sha(1)))},r=register(f,()=> 'expected-core'),data={...input,files:f,roles:r};
 assert.equal(verifyWorkflowPins(data,g).references,1);
 for(const body of [guard(sha(9)),'',guard(sha(1))+guard(sha(1)),guard(sha(1)).replace('source_commit -cne','source_commit -ne'),guard(sha(1)).replace(`'${sha(1)}'`,'$env:SOURCE')])assert.throws(()=>verifyWorkflowPins({...data,files:{'wss-compatibility.yml':workflow(body)}},g));
 const stale={...data,files:{'wss-compatibility.yml':workflow(guard(sha(9)))}};
 const result=refreshWorkflowPins(stale)['wss-compatibility.yml'];assert.equal(result,f['wss-compatibility.yml']);assert.match(result,/source_commit -cne/);assert.match(result,/throw 'Unexpected reviewed package verifier lock/);
});
test('a missed repeated matrix-job checkout fails and refresh changes all occurrences',()=>{
 const f={'matrix.yml':files['ci.yml']+'  other:\n    steps:\n'+checkout(parent,sha(3))},r=register(f,p=>p.repository===core?'core':'integration'),data={...input,files:f,roles:r};
 assert.equal(verifyWorkflowPins(data,g).references,3);
 const result=refreshWorkflowPins({...data,adapter:sha(8),adapterLock:nativeLock});assert.equal((result['matrix.yml'].match(new RegExp(sha(8),'g'))||[]).length,2);
 const missed=result['matrix.yml'].replace(sha(8),sha(3));assert.throws(()=>verifyWorkflowPins({...data,files:{'matrix.yml':missed}},g),/Caller\/callee/);
 assert.throws(()=>verifyWorkflowPins({...data,files:{'matrix.yml':f['matrix.yml'].replace(checkout(parent,sha(3)),'')}},g),/Missing registered/);
});
test('caller, callee and expected-validator sources cannot disagree even with equivalent blobs',()=>{
 const f={'ci.yml':files['ci.yml']+`  reusable:\n    uses: ${parent}/.github/workflows/_python-validation.yml@${sha(3)}\n`},r=register(f,p=>p.repository===core?'core':'integration'),data={...input,files:f,roles:r};
 assert.equal(verifyWorkflowPins(data,g).references,3);
 assert.throws(()=>verifyWorkflowPins({...data,files:{'ci.yml':f['ci.yml'].replace(`yml@${sha(3)}`,`yml@${sha(8)}`)}},g),/Caller\/callee/);
 for(const path of ['.github/workflows','.github/workflows/_python-validation.yml'])assert.throws(()=>verifyWorkflowPins(data,{...g,object:(_r,ref,p)=>({sha:ref===sha(3)&&p===path?sha(9):sha(6)})}),/Pinned integration tooling/);
});
test('unauthorized expression, repository, path, reusable and inline substitutions fail closed',()=>{
 for(const text of [files['ci.yml'].replace(sha(3),'${{ inputs.tools_sha }}'),files['ci.yml'].replace(parent,'cwru-non-academic/Unknown'),files['ci.yml'].replace('path: tools','path: mystery'),files['ci.yml']+`  extra:\n    uses: ${parent}/.github/workflows/_python-validation.yml@${sha(3)}\n`,workflow('      - uses: actions/checkout@v4\n        with: {repository: '+parent+', ref: '+sha(3)+'}\n')])assert.throws(()=>verifyWorkflowPins(changed({'ci.yml':text}),g));
 assert.throws(()=>workflowPins(`jobs:\n  check:\n    uses: ${parent}/.github/workflows/_python-validation.yml@main\n`,[]),/immutable/);
 assert.throws(()=>workflowPins(`jobs:\n  check:\n    steps:\n      - uses: ${parent}/action@${sha(3)}\n`,[]),/Unclassified/);
});
test('authenticated inputs, exact forwarding, product/gitlink expressions and fixtures stay separate',()=>{
 const f={'_worker.yml':workflow(checkout(parent,'${{ inputs.tools_sha }}')+checkout(parent,'${{ inputs.wrapper_sha }}','product')+checkout(core,sha(7),'fixture'))+'  nested:\n    uses: ./.github/workflows/_child.yml\n    with:\n      tools_sha: ${{ inputs.tools_sha }}\n','_child.yml':workflow(checkout(parent,'${{ inputs.tools_sha }}'))};
 const r=register(f,p=>p.kind==='local-workflow'?'local-workflow':p.kind==='source-field'?'forwarded-source':p.checkoutPath==='fixture'?'fixture':p.checkoutPath==='product'?'product':'authenticated-tooling'),data={...input,files:f,roles:r};
 assert.equal(verifyWorkflowPins(data,g).references,6);assert.deepEqual(refreshWorkflowPins(data),f);
 assert.throws(()=>verifyWorkflowPins({...data,files:{...f,'_worker.yml':f['_worker.yml'].replace('tools_sha: ${{ inputs.tools_sha }}','tools_sha: ${{ inputs.wrapper_sha }}')}},g),/source changed/);
 assert.throws(()=>verifyWorkflowPins({...data,files:{...f,'_child.yml':f['_child.yml'].replace('inputs.tools_sha','inputs.wrapper_sha')}},g),/source changed/);
});
test('generated receipt engine, Core and validator expected fields agree with their checkouts',()=>{
 const f={'release-receipt.yml':`jobs:\n  receipt:\n    env:\n      WSS_RECEIPT_ENGINE_SHA: ${sha(2)}\n      WSS_RECEIPT_CORE_SHA: ${sha(1)}\n      WSS_RECEIPT_VALIDATOR_SHA: ${sha(3)}\n    steps:\n${checkout(parent,sha(2),'bootstrap/Python_WSS_Integration_Lib')}${checkout(core,sha(1),'core')}${checkout(parent,sha(3),'validator')}`};
 const r=register(f,p=>p.field==='WSS_RECEIPT_ENGINE_SHA'?'expected-engine':p.field==='WSS_RECEIPT_CORE_SHA'?'expected-core':p.field==='WSS_RECEIPT_VALIDATOR_SHA'?'expected-validator':p.checkoutPath==='core'?'core':p.checkoutPath==='validator'?'integration':'engine'),data={...input,files:f,roles:r};
 assert.equal(verifyWorkflowPins(data,g).references,6);assert.deepEqual(refreshWorkflowPins({...data,adapter:sha(8),adapterLock:nativeLock}),f,'Receipt is generated only by its existing renderer');
 for(const key of ['ENGINE','CORE','VALIDATOR'])assert.throws(()=>verifyWorkflowPins({...data,files:{'release-receipt.yml':f['release-receipt.yml'].replace(new RegExp(`(WSS_RECEIPT_${key}_SHA: )[a-f0-9]{40}`),`$1${sha(9)}`)}},g));
});
test('historical branch fixture remains frozen while separate resolver tools advance on both channels',()=>{
 for(const channel of ['dev','main']){
  const fixture=channel==='dev'?sha(6):sha(7),f={'fixtures.yml':workflow(checkout(parent,fixture,'integration-source')+checkout(parent,sha(3),'integration-tools'))},r=register(f,p=>p.checkoutPath==='integration-source'?'fixture':'integration');
  const entry=r.source_catalogue[0].sources[0];delete entry.ref;entry.refs={dev:sha(6),main:sha(7)};
  const data={...input,channel,files:f,roles:r},result=refreshWorkflowPins({...data,adapter:sha(8),adapterLock:nativeLock});assert.match(result['fixtures.yml'],new RegExp(`ref: ${fixture}`));assert.match(result['fixtures.yml'],new RegExp(`ref: ${sha(8)}`));
  assert.throws(()=>refreshWorkflowPins({...data,channel:'preview',adapter:sha(8),adapterLock:nativeLock}),/Unsupported/);
 }
});
test('registered Core fixture is immutable and duplicate fixture registrations are rejected',()=>{
 const f={'fixtures.yml':workflow(checkout(core,sha(7),'.macos-ble-fixture-core'))},r=register(f,()=> 'fixture');r.runtime_fixtures=[{target:repository,workflow:'fixtures.yml',checkout_path:'.macos-ble-fixture-core',repository:core,commit:sha(7)}];const data={...input,files:f,roles:r};assert.deepEqual(refreshWorkflowPins(data),f);
 assert.throws(()=>verifyWorkflowPins({...data,files:{'fixtures.yml':f['fixtures.yml'].replace(sha(7),sha(1))}},g),/source changed/);
 assert.throws(()=>verifyWorkflowPins({...data,roles:{...r,runtime_fixtures:[...r.runtime_fixtures,...r.runtime_fixtures]}},g),/Ambiguous/);
 const actual=JSON.parse(readFileSync(new URL('./executable-pin-roles.json',import.meta.url)));assert.equal(actual.runtime_fixtures[0].commit,'0ac18df22a1371e5ff94d5485c6d3fa4b3e6538f');
});
test('refresh can repair an explicitly registered floating ROS promotion tool, never accept it in verification',()=>{
 const f={'promotion.yml':workflow(checkout(parent,'dev','promotion-tools'))},r=register(f,()=> 'integration'),data={...input,files:f,roles:r};assert.throws(()=>verifyWorkflowPins(data,g),/Floating executable/);assert.match(refreshWorkflowPins({...data,adapter:sha(8),adapterLock:nativeLock})['promotion.yml'],new RegExp(sha(8)));
});
test('source replacement preserves CRLF and surrounding product constants exactly',()=>{
 const text=files['ci.yml'].replace(/\n/g,'\r\n'),pins=workflowPins(text,[],{repository}),result=replacePins(text,pins,{[parent]:sha(8),[core]:sha(9)});assert.equal(result,text.replace(sha(3),sha(8)).replace(sha(1),sha(9)));assert.equal(result.split('\r\n').length,text.split('\r\n').length);
});
test('candidate future-owner mode uses exact snapshots without deployed-head authorization',()=>{
 const sources=[{repository,channel:'dev',commit:sha(7),tree:sha(6)},{repository,channel:'main',commit:sha(8),tree:sha(6)},{repository:parent,channel:'dev',commit:sha(3),tree:sha(6)},{repository:parent,channel:'main',commit:sha(4),tree:sha(6)}];
 const reader={...g,head:()=>{throw Error('No deployed head reads allowed');}};
 for(const channel of ['dev','main']){const f={'ci.yml':files['ci.yml'].replace(sha(3),channel==='dev'?sha(3):sha(4))},data={...input,channel,files:f,sources},report=verifyCandidatePinBindings(data,reader);assert.equal(report.deployment_authorized,false);assert.equal(report.mode,'immutable-local-candidate');assert.throws(()=>verifyWorkflowPins(data,g),/not a deployed/);}
 assert.throws(()=>verifyCandidatePinBindings({...input,sources:sources.slice(0,-1)},reader),/Explicit unique/);
 assert.throws(()=>verifyCandidatePinBindings({...input,sources,channel:'preview'},reader),/supported/);
 assert.throws(()=>verifyCandidatePinBindings({...input,sources,files:{'ci.yml':files['ci.yml'].replace(sha(3),sha(9))}},reader),/selected future/);
 assert.throws(()=>verifyCandidatePinBindings({...input,sources}, {...reader,object:(_r,ref,path)=>({sha:ref===sha(4)&&path==='.github/scripts'?sha(9):sha(6)})}),/future dev\/main/);
 assert.throws(()=>verifyCandidatePinBindings({...input,sources}, {...reader,get:path=>path.includes('/git/commits/')?{sha:path.split('/').at(-1),tree:{sha:sha(9)}}:g.get(path)}),/commit\/tree/);
});
test('local reader binds exact commit/tree, preserves gitlinks and never falls back to branches or network',()=>{
 const temp=mkdtempSync(join(tmpdir(),'wss-pin-synthetic-')),git=(...args)=>execFileSync('git',['-C',temp,...args],{encoding:'utf8'}).trim();
 try{git('init','-q');git('config','user.name','Synthetic CI');git('config','user.email','synthetic@example.invalid');writeFileSync(join(temp,'fixture.txt'),'synthetic only\n');git('add','fixture.txt');git('commit','-qm','synthetic');const commit=git('rev-parse','HEAD');git('update-index','--add','--cacheinfo',`160000,${commit},product`);git('commit','-qm','synthetic gitlink');const head=git('rev-parse','HEAD'),tree=git('rev-parse','HEAD^{tree}'),sources=[{repository,channel:'dev',commit:head,tree,path:temp}],reader=createLocalSourceReader(sources);
 assert.equal(reader.bytes(repository,head,'fixture.txt').toString(),'synthetic only\n');assert.equal(reader.object(repository,head,'product').mode,'160000');assert.throws(()=>reader.head(repository,'dev'),/cannot authorize/);assert.throws(()=>reader.text(repository,'dev','fixture.txt'),/immutable/);assert.throws(()=>reader.text(parent,head,'fixture.txt'),/Unmapped/);assert.throws(()=>createLocalSourceReader([{...sources[0],tree:sha(9)}]),/commit\/tree/);assert.throws(()=>createLocalSourceReader([...sources,...sources]),/Duplicate/);reader.finish();assert.equal(git('ls-tree',head,'--','product'),`160000 commit ${commit}\tproduct`);
 sources[0].tree=sha(9);assert.throws(()=>reader.finish(),/changed after capture/);
 }finally{assert.ok(resolve(temp).startsWith(resolve(tmpdir())+requireSeparator()));rmSync(temp,{recursive:true,force:true});}
});
function requireSeparator(){return process.platform==='win32'?'\\':'/';}
test('full local candidate API requires mapped canonical source identities rather than accepting loose files',()=>{
 assert.throws(()=>verifyCandidateWorkflowPins({repository,channel:'preview',profiles,sources:[]}),/Unsupported/);
 assert.throws(()=>verifyCandidateWorkflowPins({repository,channel:'dev',profiles,sources:[]}),/Explicit immutable/);
});
test('catalogue covers all supported receiver families and retains separate product/fixture/input roles',()=>{
 const actual=JSON.parse(readFileSync(new URL('./executable-pin-roles.json',import.meta.url))),targets=new Set(actual.source_catalogue.map(r=>r.target)),sources=actual.source_catalogue.flatMap(r=>r.sources);assert.equal(targets.size,10);assert.equal(actual.source_catalogue.length,96);assert.ok(sources.length>=500);for(const role of ['fixture','product','lock-core','lock-engine','authenticated-tooling','live-evidence','forwarded-source','expected-core'])assert.ok(sources.some(s=>s.role===role));
});

test('full candidate validates synthetic immutable mappings with noncanonical source directory names',async()=>{
 const temp=mkdtempSync(join(tmpdir(),'wss-pin-mapped-')),coreRoot=join(temp,'canonical-store'),originRoot=join(temp,'origin-store'),targetRoot=join(temp,'receiver-store');
 const git=(root,...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim(),put=(root,path,value)=>{const target=join(root,path);mkdirSync(resolve(target,'..'),{recursive:true});writeFileSync(target,value);};
 const init=root=>{mkdirSync(root);git(root,'init','-q');git(root,'config','user.name','Synthetic CI');git(root,'config','user.email','synthetic@example.invalid');git(root,'config','core.autocrlf','false');};
 const commit=root=>{git(root,'add','.');git(root,'commit','-qm','synthetic');return git(root,'rev-parse','HEAD');};
 try{
  for(const root of [coreRoot,originRoot,targetRoot])init(root);
  put(originRoot,'.github/scripts/source.txt','synthetic origin\n');const originCommit=commit(originRoot);
  const manifest={schema:'wss-shared-ci/v2',repositories:['Python_WSS_Integration_Lib'],origins:{integration:{repository:parent,workspace:'Python_WSS_Integration_Lib',commit:originCommit,ref:`refs/heads/ci/tooling/${originCommit}`}},files:{'.github/scripts/core.txt':{origin:'core',path:'tools/source.txt'},'.github/scripts/origin.txt':{origin:'integration',path:'.github/scripts/source.txt'}}};
  put(coreRoot,'tools/ci/sync-tooling.json',JSON.stringify(manifest));put(coreRoot,'tools/ci/executable-pin-roles.json',JSON.stringify({runtime_fixtures:[],stage_guards:[],source_catalogue:[]}));put(coreRoot,'tools/source.txt','synthetic core\n');
  for(const name of ['adapter-pins.mjs','sync-tooling.mjs'])put(coreRoot,'tools/ci/'+name,readFileSync(new URL(name,import.meta.url)));
  put(coreRoot,'tools/release/entrypoint.mjs','export const isEntrypoint=()=>false;\n');
  put(coreRoot,'tools/release/github.mjs',"export class GitHub {constructor(){throw Error('Synthetic candidate must never create network client');}}\n");
  put(coreRoot,'tools/release/channels.mjs',"export const integrationConfiguration=()=>'.github/python-ci.json';\n");
  put(coreRoot,'tools/release/verify.mjs',`export const CORE=${JSON.stringify(core)};export function requireThat(ok,message){if(!ok)throw Error(message);}export function canonical(value){const normalize=x=>Array.isArray(x)?x.map(normalize):x&&typeof x==='object'?Object.fromEntries(Object.keys(x).sort().map(k=>[k,normalize(x[k])])):x;return JSON.stringify(normalize(value));}\n`);
  const coreCommit=commit(coreRoot),{pathToFileURL}=await import('node:url'),api=await import(pathToFileURL(join(coreRoot,'tools/ci/adapter-pins.mjs')).href),sync=await import(pathToFileURL(join(coreRoot,'tools/ci/sync-tooling.mjs')).href);
  const targetLock=sync.expectedLock('Python_WSS_Integration_Lib',coreCommit,undefined,entry=>readFileSync(join(entry.origin==='core'?coreRoot:originRoot,entry.path)));
  put(targetRoot,'.github/scripts/wss-ci.lock.json',JSON.stringify(targetLock));put(targetRoot,'.github/scripts/core.txt','synthetic core\n');put(targetRoot,'.github/scripts/origin.txt','synthetic origin\n');
  const f={'ci.yml':workflow(checkout(core,coreCommit,'core'))},r=register(f,()=> 'core',parent);put(targetRoot,'.github/workflows/ci.yml',f['ci.yml']);let targetCommit=commit(targetRoot);
  const row=(repository,channel,path,commit)=>({repository,channel,path,commit,tree:git(path,'rev-parse',`${commit}^{tree}`)}),sources=[row(core,'dev',coreRoot,coreCommit),row(parent,'tooling-origin',originRoot,originCommit),row(parent,'dev',targetRoot,targetCommit)],data={repository:parent,channel:'dev',profiles,roles:r,sources};
  assert.equal(api.verifyCandidateWorkflowPins(data).scope,'workflow-pins-and-canonical-managed-bytes');
  assert.throws(()=>api.verifyCandidateWorkflowPins({...data,sources:sources.slice(1)}),/Unmapped/);
  put(targetRoot,'.github/scripts/core.txt','wrong managed bytes\n');targetCommit=commit(targetRoot);sources[2]=row(parent,'dev',targetRoot,targetCommit);
  assert.throws(()=>api.verifyCandidateWorkflowPins(data),/managed source drift/);
 }finally{assert.ok(resolve(temp).startsWith(resolve(tmpdir())+requireSeparator()));rmSync(temp,{recursive:true,force:true});}
});
