import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync,mkdtempSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {CORE,digest,plan} from './verify.mjs';
import {publish} from './github.mjs';
import {action,execute,journal} from './dispatch.mjs';
import {receiptAuthorization,assertWorkflows,collectWithWorkflows,freezeWorkflows,guardedWriter,initialProducerPlan,initialRequest,SNAPSHOT_POLICY} from '../ci/workflow-freeze.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
const json=value=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(value)).toString('base64')});
const rehash=p=>{if(p.manifest)p.manifest_sha256=digest(p.manifest);else p.policy_sha256=digest(p.policy);const {plan_id,...body}=p;p.plan_id=digest(body);return p;};
function fixture(kind='acceptance',legacy=false) {
  const stage=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  const manifest=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  if(legacy){delete stage.workflow_snapshot_policy;delete manifest.workflow_snapshot_policy;}
  const c={repository:CORE,commit:sha(1),run_id:'11',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const heads=new Map(),trees=new Map(),commands=[];
  manifest.targets.forEach((t,i)=>{for(const [branch,n]of [['dev',100+i],['main',200+i]]){heads.set(`${t.repository}:${branch}`,sha(n));trees.set(`${t.repository}:${sha(n)}`,sha(300+i));}});
  const g={
    head:(repo,branch='dev')=>repo===CORE?c.commit:heads.get(`${repo}:${branch}`),
    get:path=>{if(path===`repos/${CORE}/contents/tools/ci/dev-propagation.json?ref=${c.commit}`)return json(stage);if(path===`repos/${CORE}/contents/tools/release/targets.dev.json?ref=${c.commit}`)return json(manifest);throw Error('Unexpected lookup '+path);},
    object:(repo,ref,path)=>{assert.equal(path,'.github/workflows');const value=trees.get(`${repo}:${ref}`);assert.ok(value,'Unknown immutable workflow source');return {type:'tree',sha:value};},
    command:args=>commands.push(args)
  };
  const p=kind==='acceptance'?{schema:'wss-propagation-plan/v1',channel:'dev',candidate:c,manifest:structuredClone(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:g.head(t.repository),trusted:Object.fromEntries(t.trusted_paths.map(path=>[path,sha(9)]))}]))}:
    {schema:'wss-dev-propagation-plan/v1',candidate:c,policy:structuredClone(stage),receivers:Object.fromEntries(stage.libraries.map(t=>[t.id,{heads:{dev:g.head(t.repository),main:g.head(t.repository,'main')},trusted:{dev:{'.github/scripts':sha(9)},main:{'.github/scripts':sha(9)}}}]))};
  rehash(p);
  return {p:freezeWorkflows(g,p,kind),g,manifest,stage,heads,trees,commands,move(id,branch='dev',changed=true){
    const t=manifest.targets.find(t=>t.id===id)||stage.libraries.find(t=>t.id===id),old=g.head(t.repository,branch),next=sha(900+heads.size+trees.size);
    heads.set(`${t.repository}:${branch}`,next);trees.set(`${t.repository}:${next}`,changed?sha(999):trees.get(`${t.repository}:${old}`));
  }};
}

for(const kind of ['acceptance','stage']) {
  test(`${kind} snapshots cover exact workflow directories without changing receiver targets`,()=>{
    const f=fixture(kind);assert.equal(f.p.workflow_snapshots.schema,SNAPSHOT_POLICY);
    assert.equal(Object.keys(f.p.workflow_snapshots.targets).length,kind==='stage'?8:13);
    assertWorkflows(f.g,f.p,kind);
    if(kind==='acceptance'){plan(f.p);assert.deepEqual(f.p.manifest.targets,f.manifest.targets);}
  });
  test(`${kind} omission cannot downgrade producer-bound policy by rehashing`,()=>{
    for(const mutate of [p=>delete p.workflow_snapshots,p=>delete (p.manifest||p.policy).workflow_snapshot_policy,p=>{delete p.workflow_snapshots;delete (p.manifest||p.policy).workflow_snapshot_policy;}]) {
      const f=fixture(kind);mutate(f.p);rehash(f.p);assert.throws(()=>assertWorkflows(f.g,f.p,kind),/policy differs|required workflow snapshots/i);
    }
  });
  test(`${kind} rejects malformed, incomplete, wrong-source and unknown-version snapshots`,()=>{
    for(const mutate of [p=>p.workflow_snapshots.schema='unknown',p=>delete p.workflow_snapshots.targets[Object.keys(p.workflow_snapshots.targets)[0]],
      p=>Object.values(p.workflow_snapshots.targets)[0].source_sha=sha(999),p=>Object.values(p.workflow_snapshots.targets)[0].repository='fork/repo']) {
      const f=fixture(kind);mutate(f.p);rehash(f.p);assert.throws(()=>assertWorkflows(f.g,f.p,kind));
    }
  });
  test(`${kind} preserves historical policy without silently upgrading its plan`,()=>{
    const f=fixture(kind,true),before=JSON.stringify(f.p);assert.equal(f.p.workflow_snapshots,undefined);assert.equal(assertWorkflows(f.g,f.p,kind),false);assert.equal(JSON.stringify(f.p),before);
    f.p.workflow_snapshots={schema:SNAPSHOT_POLICY,targets:{}};rehash(f.p);assert.throws(()=>assertWorkflows(f.g,f.p,kind),/Unexpected/);
  });
  test(`${kind} payload-only commits pass but nested-worker-only changes fail`,()=>{
    const id=kind==='stage'?'unity':'unity-wrapper';
    const f=fixture(kind);f.move(id,'dev',false);assertWorkflows(f.g,f.p,kind);f.move(id);assert.throws(()=>assertWorkflows(f.g,f.p,kind),/Workflow tree changed/);
  });
}
test('integration default-main workflow drift is checked independently of dev',()=>{
  for(const kind of ['acceptance','stage']){const f=fixture(kind);f.move(kind==='stage'?'unity':'unity-wrapper','main');assert.throws(()=>assertWorkflows(f.g,f.p,kind),/Workflow tree changed/);}
});
test('a different family drifting does not invalidate unchanged family scope',()=>{
  const f=fixture('stage');f.move('csharp','main');assertWorkflows(f.g,f.p,'stage',{ids:['python']});assert.throws(()=>assertWorkflows(f.g,f.p,'stage',{ids:['csharp']}),/Workflow tree changed/);
});
test('final verification checks live workflows both before and after collection',()=>{
  const f=fixture();assert.equal(collectWithWorkflows(f.g,f.p,()=>42),42);
  assert.throws(()=>collectWithWorkflows(f.g,f.p,()=>{f.move('unity-wrapper');return 42;}),/Workflow tree changed/);
});
test('the existing publisher command is blocked at its write boundary after live drift',()=>{
  const f=fixture(),writer=guardedWriter(f.g,f.g,f.p,'acceptance',{purpose:'publication'});
  const args=['release','create',`dev-beta-11-${f.p.plan_id.slice(0,12)}`,'--repo',CORE,'--target',f.p.candidate.commit,'--prerelease','--title','candidate','--notes','verified'];
  writer.command(args);assert.equal(f.commands.length,1);f.move('python-cli');assert.throws(()=>writer.command(args),/Workflow tree changed/);assert.equal(f.commands.length,1);
});
test('adding snapshots does not bypass the real publisher tag-protection gate',()=>{
  const f=fixture(),writer=guardedWriter(f.g,f.g,f.p,'acceptance',{purpose:'publication'});
  assert.throws(()=>publish(writer,f.p,{record:{},files:{}},'.',f.g),/unconfigured/);assert.equal(f.commands.length,0);
});
test('real proof dispatcher rejects a saved intent whose workflows changed after freeze',()=>{
  const f=fixture(),t=f.p.manifest.targets.find(t=>t.kind==='gitlink');f.move(t.id);
  const j=journal(f.p,'90',1,[action(f.p,t,t.workflow,f.g.head(t.repository),{},'2026-01-01T00:00:00Z')]);
  f.g.download=()=>({'intent.json':JSON.stringify(j)});f.g.pages=()=>[];
  assert.throws(()=>execute(f.g,f.g,f.p,j),/Workflow tree changed/);assert.equal(f.commands.length,0);
});
test('guarded writer rejects alternative mutation routes, duplicate repo flags and wrong refs',()=>{
  const f=fixture(),writer=guardedWriter(f.g,f.g,f.p,'acceptance',{purpose:'publication'});
  for(const args of [['api','--method','DELETE','repos/'+CORE+'/git/refs/tags/v1'],['release','create','other','--repo',CORE],
    ['release','create',`dev-beta-11-${f.p.plan_id.slice(0,12)}`,'--repo',CORE,'--target',f.p.candidate.commit,'--prerelease','--title','candidate','--notes','verified','--repo','other/repo']])assert.throws(()=>writer.command(args),/Unsupported/);
  const t=f.p.manifest.targets[0],proof=guardedWriter(f.g,f.g,f.p,'acceptance',{ids:[t.id],purpose:'proof'});
  assert.throws(()=>proof.command(['api','--method','POST',`repos/${t.repository}/actions/workflows/release-receipt.yml/dispatches`,'-f','ref=main']),/inputs differ/);assert.equal(f.commands.length,0);
});
test('initial C# dispatch remains possible while its own producer is in progress',()=>{
  const f=fixture('stage'),env={GITHUB_ACTIONS:'true',GITHUB_EVENT_NAME:'push',GITHUB_WORKFLOW_REF:`${CORE}/.github/workflows/artifacts.yml@refs/heads/dev`,GITHUB_REPOSITORY:CORE,GITHUB_REF:'refs/heads/dev',GITHUB_SHA:f.p.candidate.commit,GITHUB_RUN_ID:f.p.candidate.run_id};
  f.g.run=()=>assert.fail('Initial C# must not wait for the enclosing producer to complete');
  initialProducerPlan(f.p,'csharp',env);assertWorkflows(f.g,f.p,'stage',{ids:['csharp']});
  const c=f.p.candidate,body={ref:'dev',inputs:{wss_run_id:c.run_id,wss_commit:c.commit,wss_artifact_id:c.artifact_id,wss_sha256:c.sha256,correlation_id:`wss-${c.run_id}-${c.artifact_id}`}};
  const dir=mkdtempSync(join(tmpdir(),'wss-initial-'));
  try{const file=join(dir,'request.json');writeFileSync(file,JSON.stringify(body));const t=initialRequest(f.p,'csharp',body),writer=guardedWriter(f.g,f.g,f.p,'stage',{ids:['csharp'],purpose:'initial'}),args=['api','--method','POST',`repos/${t.repository}/actions/workflows/promote-wss.yml/dispatches`,'--input',file];
    writer.command(args);assert.equal(f.commands.length,1);f.move('csharp','main');assert.throws(()=>writer.command(args),/Workflow tree changed/);assert.equal(f.commands.length,1);
  }finally{rmSync(dir,{recursive:true,force:true});}
  for(const patch of [{GITHUB_RUN_ID:'99'},{GITHUB_REF:'refs/heads/main'},{GITHUB_EVENT_NAME:'workflow_dispatch'}])assert.throws(()=>initialProducerPlan(f.p,'csharp',{...env,...patch}));
});
test('all non-C# initial handoffs preserve exact candidate wire data',()=>{
  const f=fixture('stage');for(const family of ['python','unity','matlab']){const body={ref:'dev',inputs:{candidate_json:JSON.stringify(f.p.candidate)}};assert.equal(initialRequest(f.p,family,body).id,family);body.inputs.candidate_json=JSON.stringify({...f.p.candidate,run_id:'99'});assert.throws(()=>initialRequest(f.p,family,body),/differs/);}
});

test('common receipt authority is mandatory on DEV and rejects omitted frozen policy',()=>{
  const f=fixture();
  for(const target of f.p.manifest.targets)assert.equal(receiptAuthorization(f.g,f.p,target.id).target_id,target.id);
  f.move('python-wrapper');assert.throws(()=>receiptAuthorization(f.g,f.p,'python-wrapper'),/Workflow tree changed/);
  const legacy=fixture('acceptance',true);assert.throws(()=>receiptAuthorization(legacy.g,legacy.p,'python-wrapper'),/requires producer-bound/);
  assert.throws(()=>receiptAuthorization(f.g,f.p,'unknown'),/Invalid receipt authority/);
});
