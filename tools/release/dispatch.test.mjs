import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {action,execute,journal,journalName,prepare,validateJournal} from './dispatch.mjs';
import {bytesDigest,CORE,CORE_JOBS,digest} from './verify.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
function fixture({normal=[],receipts=[],prior=[],expired=false,moved=false}={}) {
  const manifest=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(Buffer.from('zip'))};
  const body={schema:'wss-propagation-plan/v1',channel:'dev',candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:sha(2),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(3)]))}]))};
  const p={...body,plan_id:digest(body)},t=manifest.targets[0],requests=[];
  const old=journal(p,'90',1,prior.length?prior:[action(p,t,t.workflow,sha(10),{})]);
  const g={
    run:()=>({id:1,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',head_sha:sha(1),head_branch:'dev',event:'push',status:'completed',conclusion:'success',run_attempt:1}),
    head:repo=>repo===CORE?sha(1):repo===t.repository?(moved?sha(99):sha(10)):sha(2),
    jobs:()=>CORE_JOBS.map(name=>({name,status:'completed',conclusion:'success'})),
    artifact:()=>({id:2,name:'universal-release-assets',expired:false,workflow_run:{id:1,head_sha:sha(1)}}),
    download:(repo,run,name)=>name==='universal-release-assets'?{'WSS-Universal-ci.zip':Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${candidate.sha256}  WSS-Universal-ci.zip\n`)}:{'intent.json':Buffer.from(JSON.stringify(old))},
    pages:path=>{
      if(path.includes('/dev-release.yml/')) return prior.length?[{id:90}]:[];
      if(path.includes(`repos/${CORE}/actions/runs/90/artifacts`))return[{name:journalName(p,'90',1),expired}];
      if(path.includes('/commits/'))return[{merged_at:'2026-10-29T00:00:00Z',merge_commit_sha:sha(10),base:{ref:'dev',repo:{full_name:t.repository}},head:{repo:{full_name:t.repository}},user:{type:'Bot'}}];
      if(path.includes('/release-receipt.yml/'))return receipts.map(r=>({...r,display_title:`WSS acceptance ${t.id} ${p.plan_id}`}));
      return normal;
    },
    command:args=>requests.push(args)
  };
  return {p,t,g,old,requests};
}
test('planning new postmerge CI is read-only and yields an exact registered intent',()=>{
  const f=fixture(),j=prepare(f.g,f.p,'100',1);
  assert.equal(f.requests.length,0);assert.equal(j.actions.length,1);assert.equal(j.actions[0].workflow,f.t.workflow);assert.deepEqual(j.actions[0].inputs,{});
});
test('planning waits for pending postmerge CI without requesting receipts',()=>{
  const f=fixture({normal:[{id:10,status:'in_progress'}]});assert.equal(prepare(f.g,f.p,'100',1).actions.length,0);
});
test('successful exact normal CI requests a receipt with only immutable plan inputs',()=>{
  const f=fixture({normal:[{id:10,status:'completed',conclusion:'success'}]}),j=prepare(f.g,f.p,'100',1);
  assert.equal(j.actions[0].workflow,'.github/workflows/release-receipt.yml');assert.deepEqual(j.actions[0].inputs,{producer_run_id:'1',plan_id:f.p.plan_id});assert.equal(f.requests.length,0);
});
test('existing pending receipt is reused',()=>{
  const f=fixture({normal:[{id:10,status:'completed',conclusion:'success'}],receipts:[{status:'in_progress'}]});assert.equal(prepare(f.g,f.p,'100',1).actions.length,0);
});
for(const [name,options] of [
  ['failed postmerge CI',{normal:[{id:10,status:'completed',conclusion:'failure'}]}],
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
  const reader={download:()=>{throw new Error('upload missing');},head:f.g.head};
  assert.throws(()=>execute(f.g,reader,f.p,j),/upload missing/);assert.equal(f.requests.length,0);
});
test('execution rejects changed branch before issuing any requests',()=>{
  const f=fixture({moved:true}),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[]};
  assert.throws(()=>execute(f.g,reader,f.p,j),/moved/);assert.equal(f.requests.length,0);
});
test('a saved exact intent dispatches only the registered workflow on dev',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[]};
  execute(f.g,reader,f.p,j);assert.equal(f.requests.length,1);assert.deepEqual(f.requests[0].slice(-2),['-f','ref=dev']);
});
test('execution rechecks downstream after journaling and reuses a concurrent pending run',()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);
  const reader={download:()=>({'intent.json':Buffer.from(JSON.stringify(j))}),head:f.g.head,pages:()=>[{id:200,status:'queued'}]};
  execute(f.g,reader,f.p,j);assert.equal(f.requests.length,0);
});
for(const [name,mutate]of [
  ['MATLAB target',j=>j.actions[0].target_id='matlab-cli'],
  ['promotion workflow',j=>j.actions[0].workflow='.github/workflows/promote-wss.yml'],
  ['main destination',j=>j.actions[0].ref='main'],
  ['injected input',j=>j.actions[0].inputs={unexpected:'value'}],
  ['changed key',j=>j.actions[0].key='0'.repeat(64)]
])test(`journal rejects ${name}`,()=>{
  const f=fixture(),j=journal(f.p,'100',1,[action(f.p,f.t,f.t.workflow,sha(10),{})]);mutate(j);assert.throws(()=>validateJournal(f.p,j));
});
