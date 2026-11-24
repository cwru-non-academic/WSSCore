import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {action as coreAction,execute,journal,journalName,prepare,validateJournal} from './dispatch.mjs';
import {findNormalRuns} from './normal-ci.mjs';
import {bytesDigest,CORE,CORE_JOBS,digest} from './verify.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
const MERGED_AT='2026-10-29T00:00:00Z';
const action=(p,t,workflow,head,inputs)=>coreAction(p,t,workflow,head,inputs,MERGED_AT);
const normalRun=(t,r)=>({repository:{full_name:t.repository},head_repository:{full_name:t.repository},path:t.workflow,
  head_sha:sha(10),head_branch:'dev',event:'workflow_dispatch',created_at:'2026-10-29T00:01:00Z',run_attempt:1,...r});
function fixture({normal=[],receipts=[],prior=[],expired=false,moved=false,targetId='csharp-cli',executed=false}={}) {
  const manifest=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  delete manifest.workflow_snapshot_policy; // Existing journal cases model historical frozen plans.
  if(!executed)delete manifest.workflow_authority;
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(Buffer.from('zip'))};
  const body={schema:'wss-propagation-plan/v1',channel:'dev',candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:sha(2),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(3)]))}]))};
  const p={...body,plan_id:digest(body)},t=manifest.targets.find(t=>t.id===targetId),requests=[];
  const old=journal(p,'90',1,prior.length?prior:[action(p,t,t.workflow,sha(10),{})]);
  const g={
    get:path=>{assert.equal(path,`repos/${CORE}/contents/tools/release/targets.dev.json?ref=${candidate.commit}`);return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(manifest)).toString('base64')};},
    run:(_repo,id)=>({id:Number(id),repository:{full_name:CORE},head_repository:{full_name:CORE},path:id==='1'?'.github/workflows/artifacts.yml':'.github/workflows/dev-release.yml',head_sha:sha(1),head_branch:id==='1'?'dev':'main',event:'push',status:'completed',conclusion:'success',run_attempt:1,created_at:'2026-10-28T23:00:00Z'}),
    head:repo=>repo===CORE?sha(1):repo===t.repository?(moved?sha(99):sha(10)):sha(2),
    jobs:()=>CORE_JOBS.map(name=>({name,status:'completed',conclusion:'success'})),
    artifact:()=>({id:2,name:'universal-release-assets',expired:false,workflow_run:{id:1,head_sha:sha(1)}}),
    download:(repo,run,name)=>name==='universal-release-assets'?{'WSS-Universal-ci.zip':Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${candidate.sha256}  WSS-Universal-ci.zip\n`)}:{'intent.json':Buffer.from(JSON.stringify(old))},
    pages:path=>{
      if(path===`repos/${CORE}/actions/artifacts`||path.startsWith(`repos/${CORE}/actions/artifacts?name=`))return prior.length?[{name:journalName(p,'90',1),expired,workflow_run:{id:90,head_sha:sha(1)}}]:[];
      if(path.includes('/commits/'))return[{merged_at:'2026-10-29T00:00:00Z',merge_commit_sha:sha(10),base:{ref:'dev',repo:{full_name:t.repository}},head:{repo:{full_name:t.repository}},user:{type:'Bot'}}];
      if(path.includes('/release-receipt.yml/')) {
        assert.ok(path.endsWith('&created=%3E%3D2026-10-28T23%3A00%3A00Z'));
        return receipts.map(r=>({...r,display_title:`WSS acceptance ${t.id} ${p.plan_id}`}));
      }
      return normal.map(r=>normalRun(t,r));
    },
    command:args=>requests.push(args)
  };
  return {p,t,g,old,requests};
}
test('planning new postmerge CI is read-only and yields an exact registered intent',()=>{
  const f=fixture(),{intent:j,pending}=prepare(f.g,f.p,'100',1);
  assert.ok(pending.includes(f.t.id));
  assert.equal(f.requests.length,0);assert.equal(j.actions.length,1);assert.equal(j.actions[0].workflow,f.t.workflow);assert.deepEqual(j.actions[0].inputs,{});
});
test('planning waits for pending postmerge CI without requesting receipts',()=>{
  const f=fixture({normal:[{id:10,status:'in_progress'}]});assert.equal(prepare(f.g,f.p,'100',1).intent.actions.length,0);
});
test('successful exact normal CI requests a receipt with only immutable plan inputs',()=>{
  const f=fixture({normal:[{id:10,status:'completed',conclusion:'success'}]}),{intent:j}=prepare(f.g,f.p,'100',1);
  assert.equal(j.actions[0].workflow,'.github/workflows/release-receipt.yml');assert.deepEqual(j.actions[0].inputs,{producer_run_id:'1',plan_id:f.p.plan_id});assert.equal(f.requests.length,0);
});
test('existing pending receipt is reused',()=>{
  const f=fixture({normal:[{id:10,status:'completed',conclusion:'success'}],receipts:[{status:'in_progress'}]});
  const {intent,pending}=prepare(f.g,f.p,'100',1);
  assert.equal(intent.actions.length,0);
  assert.ok(pending.includes(f.t.id));
});
test('only successfully completed receipt runs leave the pending set',()=>{
  const f=fixture({normal:[{id:10,status:'completed',conclusion:'success'}],receipts:[{status:'completed',conclusion:'success'}]});
  const {intent,pending}=prepare(f.g,f.p,'100',1);
  assert.equal(intent.actions.length,0);
  assert.ok(!pending.includes(f.t.id));
  assert.equal(pending.length,8,'Reconciliation is still waiting for the other eight receipts');
});
for(const [name,options] of [
  ['failed postmerge CI',{normal:[{id:10,status:'completed',conclusion:'failure'}]}],
  ['ambiguous postmerge normal CI',{normal:[{id:10,status:'completed',conclusion:'failure'},{id:11,status:'completed',conclusion:'success'}]}],
  ['failed receipt',{normal:[{id:10,status:'completed',conclusion:'success'}],receipts:[{status:'completed',conclusion:'failure'}]}],
  ['ambiguous receipt',{normal:[{id:10,status:'completed',conclusion:'success'}],receipts:[{},{}]}]
]) test(`${name} cannot trigger automatic recovery dispatch`,()=>{
  const f=fixture(options);assert.throws(()=>prepare(f.g,f.p,'100',1));assert.equal(f.requests.length,0);
});
test('durable prior intent with no visible downstream run blocks duplicate automatic dispatch',()=>{
  let f=fixture();f=fixture({prior:[action(f.p,f.t,f.t.workflow,sha(10),{})]});
  assert.throws(()=>prepare(f.g,f.p,'100',1),/Uncertain prior dispatch/);assert.equal(f.requests.length,0);
});
test('expired journal blocks recovery rather than forgetting a possible dispatch',()=>{
  let f=fixture();f=fixture({prior:[action(f.p,f.t,f.t.workflow,sha(10),{})],expired:true});
  assert.throws(()=>prepare(f.g,f.p,'100',1),/expired/);assert.equal(f.requests.length,0);
});
test('execution requires the exact already-uploaded intent before the first mutation',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={get:f.g.get,download:()=>{throw new Error('upload missing');},head:f.g.head};
  assert.throws(()=>execute(f.g,reader,f.p,j),/upload missing/);assert.equal(f.requests.length,0);
});
test('execution rejects changed branch before issuing any requests',()=>{
  const f=fixture({moved:true}),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={get:f.g.get,download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[]};
  assert.throws(()=>execute(f.g,reader,f.p,j),/moved/);assert.equal(f.requests.length,0);
});
test('a saved exact intent dispatches only the registered workflow on dev',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={get:f.g.get,download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[]};
  execute(f.g,reader,f.p,j);assert.equal(f.requests.length,1);assert.deepEqual(f.requests[0].slice(-2),['-f','ref=dev']);
});
test('execution rechecks downstream after journaling and reuses a concurrent pending run',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={get:f.g.get,download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[normalRun(f.t,{id:200,status:'queued',event:'push'})]};
  execute(f.g,reader,f.p,j);assert.equal(f.requests.length,0);
});
test('execution rejects multiple normal runs instead of superseding a failed run',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={get:f.g.get,download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[normalRun(f.t,{id:201,status:'queued',event:'push'}),normalRun(f.t,{id:200,status:'completed',conclusion:'failure'})]};
  assert.throws(()=>execute(f.g,reader,f.p,j),/Ambiguous/);assert.equal(f.requests.length,0);
});
test('execution checks branch again immediately before its POST',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  let targetReads=0;
  const reader={get:f.g.get,download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:repo=>repo===CORE?sha(1):++targetReads===1?sha(10):sha(99),pages:()=>[]};
  assert.throws(()=>execute(f.g,reader,f.p,j),/moved/);assert.equal(f.requests.length,0);
});
for(const [name,mutate]of [
  ['unknown target',j=>j.actions[0].target_id='unknown-target'],
  ['promotion workflow',j=>j.actions[0].workflow='.github/workflows/promote-wss.yml'],
  ['main destination',j=>j.actions[0].ref='main'],
  ['injected input',j=>j.actions[0].inputs={unexpected:'value'}],
  ['missing merge time',j=>delete j.actions[0].merged_at],
  ['changed key',j=>j.actions[0].key='0'.repeat(64)]
])test(`journal rejects ${name}`,()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);mutate(j);assert.throws(()=>validateJournal(f.p,j));
});

test('acceptance never requests integration normal CI owned by the stage coordinator',()=>{
  const f=fixture({targetId:'csharp-wrapper'});
  assert.deepEqual(prepare(f.g,f.p,'100',1).intent.actions,[]);
  const forbidden=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  assert.throws(()=>validateJournal(f.p,forbidden),/staged coordinator/);
  assert.equal(f.requests.length,0);
});
test('app-only branch movement cannot hold up library receipt dispatch',()=>{
  const f=fixture({targetId:'csharp-wrapper',executed:true,normal:[{id:10,status:'completed',conclusion:'success'}]});
  const head=f.g.head;
  f.g.head=repo=>{
    assert.ok(!f.p.manifest.targets.some(t=>t.repository===repo&&t.kind==='gitlink'),'Inspect apps only after library receipts');
    return head(repo);
  };
  const {intent,pending}=prepare(f.g,f.p,'100',1);
  assert.equal(intent.actions[0].target_id,'csharp-wrapper');
  assert.equal(intent.actions[0].workflow,'.github/workflows/release-receipt.yml');
  assert.equal(pending.length,9);
});

for(const blockedBy of ['failed CI','uncertain dispatch','uncertain dispatch before branch moved'])test(`a library with ${blockedBy} does not block an independent receipt`,()=>{
  const targetId='csharp-wrapper';
  const options={targetId,executed:true,normal:[{id:10,status:'completed',conclusion:blockedBy==='failed CI'?'failure':'success'}]};
  const seed=fixture(options);
  const receiptWorkflow='.github/workflows/release-receipt.yml';
  const inputs={producer_run_id:'1',plan_id:seed.p.plan_id};
  const priorHead=blockedBy==='uncertain dispatch before branch moved'?sha(9):sha(10);
  const prior=blockedBy==='failed CI'?[]:[action(seed.p,seed.t,receiptWorkflow,priorHead,inputs)];
  const f=fixture({...options,prior}),next=f.p.manifest.targets.find(t=>t.id==='unity-wrapper');
  const head=f.g.head,pages=f.g.pages;
  f.g.head=repo=>repo===next.repository?sha(10):head(repo);
  f.g.pages=path=>{
    if(!path.includes(`repos/${next.repository}/`))return pages(path);
    if(path.includes('/commits/'))return [{merged_at:MERGED_AT,merge_commit_sha:sha(10),base:{ref:next.branch,repo:{full_name:next.repository}},head:{repo:{full_name:next.repository}},user:{type:'Bot'}}];
    if(path.includes('/release-receipt.yml/'))return [];
    return [normalRun(next,{id:11,status:'completed',conclusion:'success'})];
  };
  const {intent,pending,blocked}=prepare(f.g,f.p,'100',1);
  assert.deepEqual(intent.actions,[action(f.p,next,receiptWorkflow,sha(10),inputs)]);
  assert.deepEqual(Object.keys(blocked),[targetId]);
  assert.match(blocked[targetId],blockedBy==='failed CI'?/Postmerge CI failed/:/Uncertain prior dispatch/);
  assert.ok(pending.includes(targetId));
  assert.equal(f.requests.length,0);
});

test('a moved library does not prevent dispatch of another ready library receipt',()=>{
  const f=fixture({targetId:'csharp-wrapper',executed:true,moved:true});
  const next=f.p.manifest.targets.find(t=>t.id==='unity-wrapper');
  const workflow='.github/workflows/release-receipt.yml',inputs={producer_run_id:'1',plan_id:f.p.plan_id};
  const intent=journal(f.p,'100',1,[action(f.p,f.t,workflow,sha(10),inputs),action(f.p,next,workflow,sha(10),inputs)]);
  const reader={
    get:f.g.get,
    download:()=>({'intent.json':Buffer.from(JSON.stringify(intent))}),
    head:repo=>repo===next.repository?sha(10):f.g.head(repo),
    pages:()=>[]
  };
  assert.throws(()=>execute(f.g,reader,f.p,intent),/csharp-wrapper: Receiver moved/);
  assert.equal(f.requests.length,1);
  assert.equal(f.requests[0][3],`repos/${next.repository}/actions/workflows/release-receipt.yml/dispatches`);
});

test('acceptance reuses a successful push run and requests only its receipt',()=>{
  const f=fixture({normal:[{id:10,event:'push',status:'completed',conclusion:'success'}]});
  const {intent:j}=prepare(f.g,f.p,'100',1);
  assert.equal(j.actions.length,1);assert.equal(j.actions[0].workflow,'.github/workflows/release-receipt.yml');
});

test('a pending push waits without a second normal dispatch',()=>{
  const f=fixture({normal:[{id:10,event:'push',status:'in_progress'}]});
  assert.deepEqual(prepare(f.g,f.p,'100',1).intent.actions,[]);assert.equal(f.requests.length,0);
});

test('a failed push stops acceptance without a replacement manual dispatch',()=>{
  const f=fixture({normal:[{id:10,event:'push',status:'completed',conclusion:'failure'}]});
  assert.throws(()=>prepare(f.g,f.p,'100',1),/Postmerge CI failed/);assert.equal(f.requests.length,0);
});

for(const [name,change] of [
  ['fork',r=>r.head_repository.full_name='fork/repo'],['wrong source',r=>r.head_sha=sha(99)],
  ['wrong workflow',r=>r.path='.github/workflows/other.yml'],['wrong branch',r=>r.head_branch='main'],
  ['invalid time',r=>r.created_at='invalid'],['invalid attempt',r=>r.run_attempt=0]
])test(`shared normal CI discovery rejects ${name}`,()=>{
  const f=fixture(),r=normalRun(f.t,{id:10,status:'completed',conclusion:'success'});change(r);
  assert.throws(()=>findNormalRuns({pages:()=>[r]},f.t,sha(10),MERGED_AT));
});

test('shared normal CI discovery excludes premerge and synthetic PR evidence',()=>{
  const f=fixture(),runs=[normalRun(f.t,{id:10,event:'pull_request'}),normalRun(f.t,{id:11,created_at:'2026-10-28T23:59:00Z'})];
  assert.deepEqual(findNormalRuns({pages:()=>runs},f.t,sha(10),MERGED_AT),[]);
});
