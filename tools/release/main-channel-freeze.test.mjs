import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,mkdtempSync,mkdirSync,writeFileSync,rmSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {tmpdir} from 'node:os';
import {resolve,join} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {CORE,CORE_JOBS,digest,plan} from './verify.mjs';
import {channelContext,targetForChannel,integrationConfiguration} from './channels.mjs';
import {assertWorkflows,freezeWorkflows,freezeWorkflowPair,assertWorkflowPair,initialProducerPlan,initialRequest,guardedWriter,receiverAuthorization,MAIN_SNAPSHOT_POLICY} from '../ci/workflow-freeze.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
const json=x=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(x)).toString('base64')});
const rehash=p=>{if(p.manifest)p.manifest_sha256=digest(p.manifest);else p.policy_sha256=digest(p.policy);const {plan_id,...body}=p;return {...body,plan_id:digest(body)};};
function fixture(){
  const manifest=JSON.parse(readFileSync(new URL('./targets.main.json',import.meta.url)));
  const policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  policy.schema=channelContext('main').stageSchema;policy.channel='main';policy.workflow_snapshot_policy=MAIN_SNAPSHOT_POLICY;
  const candidate={repository:CORE,commit:sha(1),run_id:'11',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const heads=new Map(),trees=new Map();
  manifest.targets.forEach((t,i)=>{for(const [branch,n] of [['main',100+i],['dev',200+i]]){heads.set(`${t.repository}:${branch}`,sha(n));trees.set(`${t.repository}:${sha(n)}`,sha(300+i));}});
  const g={head:(repo,branch)=>{assert.ok(['dev','main'].includes(branch),'Every branch read is explicit');return repo===CORE?candidate.commit:heads.get(`${repo}:${branch}`);},
    get:path=>{if(path.includes('targets.main.json'))return json(manifest);if(path.includes('main-propagation.json'))return json(policy);throw Error('Unexpected immutable policy '+path);},
    object:(repo,source,path)=>{assert.equal(path,'.github/workflows');assert.ok(trees.has(`${repo}:${source}`));return {type:'tree',sha:trees.get(`${repo}:${source}`)};}};
  const acceptance=rehash({schema:channelContext('main').planSchema,channel:'main',candidate,manifest:structuredClone(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:g.head(t.repository,'main'),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(9)]))}]))});
  const stage=rehash({schema:'wss-main-propagation-plan/v1',channel:'main',candidate,policy:structuredClone(policy),receivers:Object.fromEntries(policy.libraries.map(t=>[t.id,{heads:{main:g.head(t.repository,'main'),dev:g.head(t.repository,'dev')},trusted:{main:{},dev:{}}}]))});
  return {g,manifest,policy,acceptance,stage,heads,trees};
}
test('main target policy is the exact shared definition with closed channel overlays',()=>{
  const dev=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  const main=JSON.parse(readFileSync(new URL('./targets.main.json',import.meta.url)));
  assert.deepEqual(main,{...dev,schema:'wss-required-targets/v2',channel:'main',release_kind:'stable',stable_enabled:true,workflow_snapshot_policy:MAIN_SNAPSHOT_POLICY,targets:dev.targets.map(t=>targetForChannel(t,'main'))});
});
test('integration configuration roles use the actual immutable family paths',()=>{
  assert.equal(integrationConfiguration('csharp'),'.github/universal-bootstrap.json');
  for(const family of ['unity','python','matlab'])assert.equal(integrationConfiguration(family),`.github/${family}-ci.json`);
  assert.throws(()=>integrationConfiguration('foreign'));
  const dev=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  const main=JSON.parse(readFileSync(new URL('../ci/main-propagation.json',import.meta.url)));
  assert.deepEqual(main,{...dev,schema:channelContext('main').stageSchema,channel:'main',workflow_snapshot_policy:MAIN_SNAPSHOT_POLICY});
});
test('main keeps both same-branch roles and one physical source without overwriting',()=>{
  const f=fixture(),pair=freezeWorkflowPair(f.g,f.acceptance,f.stage);
  plan(pair.acceptance);
  assert.equal(Object.keys(pair.stage.workflow_snapshots.targets).length,12);
  assert.equal(Object.keys(pair.acceptance.workflow_snapshots.targets).length,17);
  for(const p of [pair.acceptance,pair.stage]){
    const rows=Object.values(p.workflow_snapshots.targets);
    assert.ok(rows.every(x=>/^[a-f0-9]{40}$/.test(x.source_sha)));
    for(const row of rows.filter(x=>x.role==='execution-authority')){
      const accepted=rows.find(x=>x.repository===row.repository&&x.role==='accepted-baseline');
      assert.equal(row.source_sha,accepted.source_sha);assert.equal(row.tree_sha,accepted.tree_sha);
    }
  }
});
test('main rejects omitted, weakened, null, missing and conflicting roles after rehashing',()=>{
  for(const mutate of [p=>delete p.workflow_snapshots,p=>p.manifest.workflow_snapshot_policy='wss-workflow-snapshots/v1',
    p=>delete p.workflow_snapshots.targets['csharp-wrapper:execution-authority'],
    p=>p.workflow_snapshots.targets['csharp-wrapper:execution-authority'].source_sha=null,
    p=>p.workflow_snapshots.targets['csharp-wrapper:execution-authority'].source_sha=sha(900),
    p=>p.workflow_snapshots.targets['csharp-wrapper:accepted-baseline'].role='counterpart']){
    const f=fixture(),p=freezeWorkflows(f.g,f.acceptance,'acceptance');mutate(p);assert.throws(()=>assertWorkflows(f.g,rehash(p),'acceptance'));
  }
});
test('movement during a physical freeze rejects rather than accepting a new observation',()=>{
  const f=fixture(),t=f.manifest.targets[0],object=f.g.object;
  f.g.object=(...args)=>{const result=object(...args);if(args[0]===t.repository)f.heads.set(`${t.repository}:main`,sha(999));return result;};
  assert.throws(()=>freezeWorkflows(f.g,f.acceptance,'acceptance'),/moved/);
});
test('main movement between stage and acceptance construction prevents paired authorization',()=>{
  const f=fixture(),get=f.g.get,t=f.manifest.targets[0];
  f.g.get=path=>{const r=get(path);if(path.includes('main-propagation.json'))f.heads.set(`${t.repository}:main`,sha(999));return r;};
  assert.throws(()=>freezeWorkflowPair(f.g,f.acceptance,f.stage),/moved/);
});
test('family scope checks every authority role and rejects nested main worker drift',()=>{
  const f=fixture(),p=freezeWorkflows(f.g,f.acceptance,'acceptance'),t=f.manifest.targets.find(t=>t.id==='unity-wrapper');
  f.heads.set(`${t.repository}:main`,sha(999));f.trees.set(`${t.repository}:${sha(999)}`,sha(998));
  assertWorkflows(f.g,p,'acceptance',{ids:['python-wrapper']});
  assert.throws(()=>assertWorkflows(f.g,p,'acceptance',{ids:['unity-wrapper']}),/Workflow tree changed/);
});
test('receiver authorization uses the same Core guard and completed canonical producer',()=>{
  const f=fixture(),p=freezeWorkflows(f.g,f.acceptance,'acceptance'),repository=f.manifest.targets[0].repository;
  const run={id:11,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',head_branch:'main',head_sha:p.candidate.commit,event:'push',status:'completed',conclusion:'success'};
  f.g.run=()=>run;f.g.jobs=()=>CORE_JOBS.map(name=>({name,status:'completed',conclusion:'success'}));
  const options={loadFrozenPlan:()=>p};
  assert.equal(receiverAuthorization(f.g,'11',repository,options).target_id,'csharp-wrapper');
  for(const patch of [{head_branch:'dev'},{event:'workflow_dispatch'},{status:'in_progress'},{conclusion:'failure'},{head_repository:{full_name:'fork/Core'}}]){
    f.g.run=()=>({...run,...patch});assert.throws(()=>receiverAuthorization(f.g,'11',repository,options));
  }
});

test('paired main initial C# handoff does not wait on itself and rejects a later equivalent main source',()=>{
  const f=fixture(),pair=freezeWorkflowPair(f.g,f.acceptance,f.stage),p=pair.stage,c=p.candidate;
  assertWorkflowPair(f.g,pair.acceptance,p);
  const env={GITHUB_ACTIONS:'true',GITHUB_EVENT_NAME:'push',GITHUB_WORKFLOW_REF:`${CORE}/.github/workflows/artifacts.yml@refs/heads/main`,GITHUB_REPOSITORY:CORE,GITHUB_REF:'refs/heads/main',GITHUB_SHA:c.commit,GITHUB_RUN_ID:c.run_id};
  f.g.run=()=>assert.fail('Initial dispatch cannot wait for its own producer completion');
  initialProducerPlan(p,'csharp',env);
  const body={ref:'main',inputs:{wss_run_id:c.run_id,wss_commit:c.commit,wss_artifact_id:c.artifact_id,wss_sha256:c.sha256,correlation_id:`wss-${c.run_id}-${c.artifact_id}`}};
  const t=initialRequest(p,'csharp',body),root=mkdtempSync(join(tmpdir(),'wss main initial ')),calls=[];
  f.g.command=args=>calls.push(args);
  try{
    const file=join(root,'request.json');writeFileSync(file,JSON.stringify(body));
    const writer=guardedWriter(f.g,f.g,p,'stage',{ids:['csharp'],purpose:'initial'});
    const args=['api','--method','POST',`repos/${t.repository}/actions/workflows/promote-wss.yml/dispatches`,'--input',file];
    writer.command(args);assert.equal(calls.length,1);
    const prior=f.heads.get(`${t.repository}:main`),later=sha(999);
    f.heads.set(`${t.repository}:main`,later);f.trees.set(`${t.repository}:${later}`,f.trees.get(`${t.repository}:${prior}`));
    assert.throws(()=>writer.command(args),/source moved/);assert.equal(calls.length,1);
    assert.throws(()=>initialRequest(p,'csharp',{...body,ref:'dev'}));
    assert.throws(()=>initialProducerPlan(p,'csharp',{...env,GITHUB_RUN_ID:'99'}));
  }finally{rmSync(root,{recursive:true,force:true});}
});
test('actual PowerShell receiver bridge executes locked Core CLI and rejects missing or invalid proof',()=>{
  const f=fixture(),p=freezeWorkflows(f.g,f.acceptance,'acceptance'),root=mkdtempSync(join(tmpdir(),'wss receiver authorization '));
  const core=resolve(fileURLToPath(new URL('../..',import.meta.url))),source=join(root,'target source');
  const head=execFileSync('git',['-C',core,'rev-parse','HEAD'],{encoding:'utf8'}).trim();
  const repository=p.manifest.targets[0].repository;
  const producer={id:11,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',head_branch:'main',head_sha:p.candidate.commit,event:'push',status:'completed',conclusion:'success',run_attempt:1};
  const preload=join(root,'read only API.mjs'),script=join(root,'actual bridge.ps1');
  try{
    mkdirSync(join(source,'.github/scripts'),{recursive:true});
    writeFileSync(join(source,'.github/scripts/wss-ci.lock.json'),JSON.stringify({source_repository:CORE,source_commit:head}));
    writeFileSync(preload,`import {GitHub} from ${JSON.stringify(pathToFileURL(join(core,'tools/release/github.mjs')).href)};
const p=${JSON.stringify(p)},heads=${JSON.stringify(Object.fromEntries(f.heads))},trees=${JSON.stringify(Object.fromEntries(f.trees))},run=${JSON.stringify(producer)};
if(process.argv[1]?.endsWith('workflow-freeze.mjs')&&process.env.WSS_BRIDGE_FAILURE==='process')process.exit(9);
if(process.argv[1]?.endsWith('workflow-freeze.mjs')&&process.env.WSS_BRIDGE_FAILURE==='json'){process.stdout.write('{');process.exit(0);}
GitHub.prototype.command=()=>{throw Error('Network and mutations forbidden in the bridge contract');};
GitHub.prototype.download=()=>({'plan.json':Buffer.from(JSON.stringify(p))});
GitHub.prototype.get=path=>{if(!path.includes('targets.main.json'))throw Error('Unexpected read');return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(p.manifest)).toString('base64')};};
GitHub.prototype.run=()=>({...run,head_branch:process.env.WSS_BRIDGE_FAILURE==='channel'?'dev':'main'});
GitHub.prototype.jobs=()=>${JSON.stringify(CORE_JOBS)}.map(name=>({name,status:'completed',conclusion:'success'}));
GitHub.prototype.head=(repo,branch)=>repo===${JSON.stringify(CORE)}?p.candidate.commit:heads[repo+':'+branch];
GitHub.prototype.object=(repo,ref,path)=>{if(path!=='.github/workflows')throw Error('Unexpected object');return {type:'tree',sha:trees[repo+':'+ref]};};
`);
    writeFileSync(script,`param([string]$Bridge)\n$ErrorActionPreference='Stop'\n. $Bridge\n$p=Assert-WssCiWorkflowAuthorization '11'\nif($p.target_id -cne 'csharp-wrapper'){throw 'Wrong actual bridge proof'}\n`);
    const env={...process.env,NODE_OPTIONS:`--import=${pathToFileURL(preload).href}`,WSS_CI_CHANNEL:'main',WSS_TARGET_ROOT:source,WSS_CORE_TOOLS_ROOT:core,GITHUB_REPOSITORY:repository,GITHUB_ACTIONS:'true'};
    const args=['-NoProfile','-NonInteractive','-File',script,join(core,'tools/ci/shared/Channel.ps1')];
    execFileSync('pwsh',args,{env,stdio:'pipe'});
    for(const mode of ['process','json','channel'])assert.throws(()=>execFileSync('pwsh',args,{env:{...env,WSS_BRIDGE_FAILURE:mode},stdio:'pipe'}));
    writeFileSync(join(source,'.github/scripts/wss-ci.lock.json'),JSON.stringify({source_repository:CORE,source_commit:sha(999)}));
    assert.throws(()=>execFileSync('pwsh',args,{env,stdio:'pipe'}));
  }finally{rmSync(root,{recursive:true,force:true});}
});
