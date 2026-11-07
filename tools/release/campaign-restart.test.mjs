import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {CORE,CORE_JOBS,bytesDigest,digest} from './verify.mjs';
import {channelContext} from './channels.mjs';
import {restartRequest,restartPath,prepareRestart,assertRestartEvidence,assertRequestChange,assertRestartPlans,checkRestartRunner,assertRestartSource} from './campaign-restart.mjs';
const sha=n=>String(n).padStart(40,'0');
const rehash=p=>{const {plan_id,...body}=p;return {...body,plan_id:digest(body)};};
const seal=r=>{const {request_id,...body}=r;return {...body,request_id:digest(body)};};
function fixture(channel='dev') {
  const m=JSON.parse(readFileSync(new URL(`targets.${channel}.json`,import.meta.url)));
  const policy=JSON.parse(readFileSync(new URL(`../ci/${channel}-propagation.json`,import.meta.url)));
  const context=channelContext(channel),zip=Buffer.from('immutable archive');
  const c={repository:CORE,commit:sha(1),run_id:'10',artifact_id:'20',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(zip)};
  const a=rehash({schema:context.planSchema,channel,candidate:c,manifest:m,manifest_sha256:digest(m),target_snapshots:Object.fromEntries(m.targets.map(t=>[t.id,{head_sha:sha(2),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(3)]))}]))});
  const s=rehash({schema:context.stagePlanSchema,...(channel==='main'?{channel,acceptance_plan_id:a.plan_id}:{}),candidate:c,policy,policy_sha256:digest(policy),receivers:Object.fromEntries(policy.libraries.map(t=>[t.id,{heads:{dev:sha(2),main:sha(2)},trusted:{dev:{},main:{}}}]))});
  const run={id:10,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',event:'push',head_branch:channel,head_sha:c.commit,status:'completed',conclusion:'success',run_attempt:1};
  const state={core:c.commit,receiver:sha(2),reads:[]};
  const g={head:repo=>repo===CORE?state.core:state.receiver,run:()=>run,
    get:path=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(path.includes('propagation.json')?policy:m)).toString('base64')}),
    jobs:()=>CORE_JOBS.map(name=>({name,status:'completed',conclusion:'success'})),artifact:()=>({id:20,name:'universal-release-assets',expired:false,workflow_run:{id:10,head_sha:c.commit}}),download:(_repo,_run,name)=>{state.reads.push(name);if(name==='wss-propagation-plan')return {'plan.json':Buffer.from(JSON.stringify(a))};if(name===context.stageArtifact)return {'plan.json':Buffer.from(JSON.stringify(s))};if(name==='universal-release-assets')return {[c.archive]:zip,'SHA256SUMS.txt':Buffer.from(`${c.sha256}  ${c.archive}\n`)};throw Error('unexpected download');}};
  const prepare=()=>prepareRestart(g,{channel,runId:c.run_id,expectedHead:c.commit,reason:'Receiver tooling changed after the old freeze'});
  return {g,a,s,c,run,state,prepare};
}
for(const channel of ['dev','main'])test(`${channel}: deterministic request authenticates and preserves predecessor bytes`,()=>{
  const f=fixture(channel),before=JSON.stringify([f.a,f.s]),r=f.prepare();
  assert.deepEqual(f.prepare(),r);assert.equal(r.expected_head,f.c.commit);assert.equal(Object.keys(r.receivers).length,9);
  assert.equal(restartPath(r),`tools/ci/campaign-restarts/${channel}/${r.request_id}.json`);
  assertRestartEvidence(f.g,r);assert.equal(JSON.stringify([f.a,f.s]),before);
  assert.ok(f.state.reads.includes('universal-release-assets'));
  assertRequestChange(r,{channel,base:r.expected_head,changes:[{status:'A',path:restartPath(r)}]});
});
test('request schema rejects unknown fields, bad identity, malformed reason and source substitution',()=>{
  const r=fixture().prepare();
  for(const change of [x=>x.extra=true,x=>x.expected_head='invalid',x=>x.reason=' ',x=>x.reason='A reason\nwith injection',x=>x.reason='x'.repeat(1001),x=>x.predecessor.extra=true,x=>x.receivers[Object.keys(x.receivers)[0]].extra=sha(1),x=>x.channel='release']) {
    const bad=structuredClone(r);change(bad);assert.throws(()=>restartRequest(seal(bad)));
  }
  assert.throws(()=>restartRequest({...r,request_id:'f'.repeat(64)}),/digest/);
});
test('only one newly added record on the exact parent is restart authority',()=>{
  const r=fixture().prepare(),ok={channel:'dev',base:r.expected_head,changes:[{status:'A',path:restartPath(r)}]};
  for(const bad of [{...ok,channel:'main'},{...ok,base:sha(8)},{...ok,existing:true},{...ok,changes:[]},{...ok,changes:[{status:'M',path:restartPath(r)}]},{...ok,changes:[...ok.changes,{status:'M',path:'.github/workflows/artifacts.yml'}]},{...ok,changes:[{status:'A',path:restartPath(r)+'-replay'}]}])assert.throws(()=>assertRequestChange(r,bad));
});
for(const [name,mutate] of Object.entries({
  'failed predecessor':f=>f.run.conclusion='failure',
  'unfinished predecessor':f=>f.run.status='in_progress',
  'dispatch producer':f=>f.run.event='workflow_dispatch',
  'foreign producer':f=>f.run.repository.full_name='foreign/core',
  'fork producer':f=>f.run.head_repository.full_name='foreign/core',
  'wrong workflow':f=>f.run.path='.github/workflows/manual.yml',
  'wrong source':f=>f.run.head_sha=sha(8),
  'wrong channel':f=>f.run.head_branch='main',
  'Core moved':f=>f.state.core=sha(8),
  'missing evidence':f=>f.g.download=()=>{throw Error('missing artifact');},
  'denied policy read':f=>f.g.get=()=>{throw Error('403');},
  'different stage candidate':f=>{f.s.candidate={...f.s.candidate,run_id:'11'};Object.assign(f.s,rehash(f.s));},
  'wrong archive':f=>f.g.artifact=()=>({id:21})
}))test(`prepare rejects ${name} without creating or dispatching anything`,()=>{const f=fixture();mutate(f);assert.throws(f.prepare);});
test('receiver drift and predecessor identity changes invalidate prepared request',()=>{
  const f=fixture(),r=f.prepare();f.state.receiver=sha(8);assert.throws(()=>assertRestartEvidence(f.g,r),/Receiver heads/);
  f.state.receiver=sha(2);const changed=seal({...r,predecessor:{...r.predecessor,stage_plan_id:'f'.repeat(64)}});assert.throws(()=>assertRestartEvidence(f.g,changed),/evidence changed/);
});
test('race during preparation fails closed',()=>{
  const f=fixture(),head=f.g.head;let calls=0;f.g.head=(repo,...args)=>repo===CORE&&++calls>1?sha(8):head(repo,...args);assert.throws(f.prepare,/moved/);
});
for(const channel of ['dev','main'])test(`${channel}: newly frozen plans bind all requested receivers and unique identities`,()=>{
  const f=fixture(channel),r=f.prepare(),c={...f.c,commit:sha(9),run_id:'30',artifact_id:'40'};
  let a=rehash({...f.a,candidate:c}),s=rehash({...f.s,candidate:c,...(channel==='main'?{acceptance_plan_id:a.plan_id}:{})});
  assertRestartPlans(r,a,s);
  assert.throws(()=>assertRestartPlans(r,f.a,f.s),/new paired/);
  const wrong=structuredClone(a);wrong.target_snapshots['python-cli'].head_sha=sha(8);assert.throws(()=>assertRestartPlans(r,rehash(wrong),s),/snapshot/);
  const wrongStage=structuredClone(s);wrongStage.receivers.python.heads.main=sha(8);assert.throws(()=>assertRestartPlans(r,a,rehash(wrongStage)),/snapshots/);
});
test('workflow guards run in a required check and before selection/write credentials and plan upload',()=>{
  const workflow=readFileSync(new URL('../../.github/workflows/artifacts.yml',import.meta.url),'utf8');
  const core=workflow.slice(workflow.indexOf('  core-tests:'),workflow.indexOf('  ble-linux-tests:'));
  assert.match(core,/fetch-depth: 0/);assert.match(core,/campaign-restart-cli.mjs check/);
  const producer=workflow.slice(workflow.indexOf('  propagate-universal:'));
  assert.ok(producer.indexOf('campaign-restart-cli.mjs check')<producer.indexOf('permission-variables: read'));
  assert.ok(producer.indexOf('campaign-restart-cli.mjs check-frozen')<producer.indexOf('name: ${{ steps.channel.outputs.stage_artifact }}'));
  assert.ok(producer.indexOf('campaign-restart-cli.mjs check-frozen')<producer.indexOf('permission-actions: write'));
  assert.match(producer,/github.event_name == 'push'/);assert.doesNotMatch(producer,/overwrite: true/);
});

function runnerFixture(event='push') {
  const f=fixture(),r=f.prepare(),head=sha(9),base=f.c.commit;
  const env={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:CORE,GITHUB_EVENT_NAME:event,GITHUB_SHA:head,GITHUB_REF_NAME:'dev',GITHUB_WORKFLOW_SHA:head,GITHUB_WORKFLOW_REF:`${CORE}/.github/workflows/artifacts.yml@refs/heads/dev`,WSS_DEV_ACCEPTANCE_ENABLED:'true'};
  const payload={before:base,pull_request:{base:{ref:'dev',sha:base,repo:{full_name:CORE}}}};
  const state={diff:`A\t${restartPath(r)}`,parent:base,record:r};
  f.state.core=event==='push'?head:base;
  const git=(...args)=>{if(args[0]==='diff')return state.diff.replaceAll('\\t','\t');if(args[0]==='ls-tree')return '100644 blob '+sha(7)+'\t'+restartPath(r);if(args[0]==='show')return JSON.stringify(state.record);if(args[0]==='rev-parse')return state.parent;throw Error('Unexpected git '+args);};
  return {...f,r,env,payload,state,git,check:()=>checkRestartRunner(f.g,{env,event:payload,git})};
}
for(const event of ['push','pull_request'])test(`${event}: runner authenticates the exact new record and immutable source`,()=>{
  const f=runnerFixture(event);assert.equal(f.check().request.request_id,f.r.request_id);
});
for(const [name,mutate] of Object.entries({
  'base race':f=>f.payload.before=sha(8),
  'hidden earlier rebase commit':f=>f.state.parent=sha(8),
  'extra source edit':f=>f.state.diff+='\nM\tCoreModule/source.cs',
  'record removal':f=>f.state.diff=`D\t${restartPath(f.r)}`,
  'record replacement':f=>f.state.diff=`M\t${restartPath(f.r)}`,
  'two fresh records':f=>f.state.diff+=`\nA\t${restartPath(f.r)}-other`,
  'wrong workflow authority':f=>f.env.GITHUB_WORKFLOW_SHA=sha(8),
  'wrong workflow ref':f=>f.env.GITHUB_WORKFLOW_REF='other',
  'wrong repository':f=>f.env.GITHUB_REPOSITORY='foreign/core',
  'missing before':f=>delete f.payload.before,
  'branch creation':f=>f.payload.before='0'.repeat(40),
  'disabled paired freeze':f=>f.env.WSS_DEV_ACCEPTANCE_ENABLED='false',
  'moved live head':f=>f.g.head=()=>sha(8)
}))test(`runner rejects ${name}`,()=>{const f=runnerFixture();mutate(f);assert.throws(f.check);});
test('ordinary code pushes and build-only dispatch do not become restarts',()=>{
  const f=runnerFixture();f.state.diff='M\tCoreModule/source.cs';assert.equal(f.check(),null);
  f.env.GITHUB_EVENT_NAME='workflow_dispatch';assert.equal(f.check(),null);
});
test('cross-channel promotion imports only the exact historical origin record',()=>{
  const f=runnerFixture(),foreign=seal({...f.r,channel:'main'});f.state.record=foreign;f.state.diff=`A\t${restartPath(foreign)}`;
  f.g.object=()=>({type:'blob',sha:f.state.parent});assert.equal(f.check(),null);
  f.g.object=()=>({type:'blob',sha:sha(8)});assert.throws(f.check,/origin channel/);
});
test('trusted-base validation is deployed separately from request records',()=>{
  const workflow=readFileSync(new URL('../../.github/workflows/artifacts.yml',import.meta.url),'utf8');
  assert.match(workflow,/git worktree add --detach "\$RUNNER_TEMP\/restart-validator" "\$REQUEST_BASE"/);
  assert.match(workflow,/node "\$RUNNER_TEMP\/restart-validator\/tools\/release\/campaign-restart-cli.mjs" check/);
  const cli=readFileSync(new URL('campaign-restart-cli.mjs',import.meta.url),'utf8');
  assert.doesNotMatch(cli,/git\(['"](?:push|commit)|--method|permission-variables|create-github-app-token/);
});

test('historical success can recover a failed request-only checkpoint without stale code fallback',()=>{
  const f=fixture(),request=f.prepare(),source=f.c.commit,head=sha(8),path=restartPath(request);
  const product={path:'CoreModule/source.cs',mode:'100644',type:'blob',sha:sha(4)};
  const record={path,mode:'100644',type:'blob',sha:sha(5)};
  const state={ancestry:{status:'ahead',base_commit:{sha:source},merge_base_commit:{sha:source}},old:[product],current:[product,record]};
  const get=f.g.get;
  f.g.get=url=>url.includes('/compare/')?state.ancestry:url.includes('/git/trees/')?{truncated:false,tree:url.includes(source)?state.old:state.current}:url.includes(path)?{type:'file',sha:record.sha,encoding:'base64',content:Buffer.from(JSON.stringify(request)).toString('base64')}:get(url);
  assertRestartSource(f.g,source,head);
  f.state.core=head;
  const next=prepareRestart(f.g,{channel:'dev',runId:f.c.run_id,expectedHead:head,reason:'Receiver changed while the prior request was merging'});
  assert.equal(next.expected_head,head);assert.equal(next.predecessor.candidate.commit,source);assert.notEqual(next.request_id,request.request_id);assertRestartEvidence(f.g,next);
  state.current=[{...product,sha:sha(6)},record];assert.throws(()=>assertRestartSource(f.g,source,head),/product/);
  state.current=[{...product,mode:'100755'},record];assert.throws(()=>assertRestartSource(f.g,source,head),/product/);
  state.current=[product,record];state.ancestry.status='diverged';assert.throws(()=>assertRestartSource(f.g,source,head),/ancestor/);
  state.ancestry.status='ahead';state.old=[product,record];state.current=[product,{...record,sha:sha(6)}];assert.throws(()=>assertRestartSource(f.g,source,head),/modified/);
  state.current=[product];assert.throws(()=>assertRestartSource(f.g,source,head),/removed/);
});
