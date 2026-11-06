import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {CORE,digest,plan} from './verify.mjs';
import {channelContext,targetForChannel} from './channels.mjs';
import {assertWorkflows,freezeWorkflows,freezeWorkflowPair,MAIN_SNAPSHOT_POLICY} from '../ci/workflow-freeze.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
const json=x=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(x)).toString('base64')});
const rehash=p=>{if(p.manifest)p.manifest_sha256=digest(p.manifest);else p.policy_sha256=digest(p.policy);const {plan_id,...body}=p;return {...body,plan_id:digest(body)};};
function fixture(){
  const manifest=JSON.parse(readFileSync(new URL('./targets.main.json',import.meta.url)));
  const policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  policy.channel='main';policy.workflow_snapshot_policy=MAIN_SNAPSHOT_POLICY;
  const candidate={repository:CORE,commit:sha(1),run_id:'11',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const heads=new Map(),trees=new Map();
  manifest.targets.forEach((t,i)=>{for(const [branch,n] of [['main',100+i],['dev',200+i]]){heads.set(`${t.repository}:${branch}`,sha(n));trees.set(`${t.repository}:${sha(n)}`,sha(300+i));}});
  const g={head:(repo,branch)=>{assert.ok(['dev','main'].includes(branch),'Every branch read is explicit');return repo===CORE?candidate.commit:heads.get(`${repo}:${branch}`);},
    get:path=>{if(path.includes('targets.main.json'))return json(manifest);if(path.includes('main-propagation.json'))return json(policy);throw Error('Unexpected immutable policy '+path);},
    object:(repo,source,path)=>{assert.equal(path,'.github/workflows');assert.ok(trees.has(`${repo}:${source}`));return {type:'tree',sha:trees.get(`${repo}:${source}`)};}};
  const acceptance=rehash({schema:channelContext('main').planSchema,channel:'main',candidate,manifest:structuredClone(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:g.head(t.repository,'main'),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(9)]))}]))});
  const stage=rehash({schema:'wss-main-propagation-plan/v1',candidate,policy:structuredClone(policy),receivers:Object.fromEntries(policy.libraries.map(t=>[t.id,{heads:{main:g.head(t.repository,'main'),dev:g.head(t.repository,'dev')},trusted:{main:{},dev:{}}}]))});
  return {g,manifest,policy,acceptance,stage,heads,trees};
}
test('main target policy is the exact shared definition with closed channel overlays',()=>{
  const dev=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  const main=JSON.parse(readFileSync(new URL('./targets.main.json',import.meta.url)));
  assert.deepEqual(main,{...dev,schema:'wss-required-targets/v2',channel:'main',release_kind:'stable',stable_enabled:true,workflow_snapshot_policy:MAIN_SNAPSHOT_POLICY,targets:dev.targets.map(t=>targetForChannel(t,'main'))});
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
