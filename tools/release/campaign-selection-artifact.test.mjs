import {createSnapshotRequest,SNAPSHOT_WORKFLOW} from './local-test-variable-snapshot.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {CORE} from './verify.mjs';
import {CAMPAIGN_TEST_POLICY} from './campaign-test-selection.mjs';
import {freezeCampaign} from './campaign-freeze.mjs';
import {prepareProducerTestSelection,loadProducerTestSelection,TEST_SELECTION_ARTIFACT,assertSnapshotSourcesMatchPlans} from './campaign-selection-artifact.mjs';
const sha=n=>String(n).padStart(40,'0');
function fixture(channel='dev') {
  const m=JSON.parse(readFileSync(new URL(`./targets.${channel}.json`,import.meta.url))),policy=JSON.parse(readFileSync(new URL(`../ci/${channel}-propagation.json`,import.meta.url)));
  const testPolicy={schema:CAMPAIGN_TEST_POLICY,targets:m.targets.map(t=>({id:t.id,repository:t.repository,default_test_repository:'cwru-non-academic/TestBundle',allowed_test_repositories:['cwru-non-academic/TestBundle']}))};
  m.test_selection_policy=testPolicy;policy.test_selection_policy=structuredClone(testPolicy);
  const c={repository:CORE,commit:sha(1),run_id:'10',artifact_id:'20',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const env={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:CORE,GITHUB_EVENT_NAME:'push',GITHUB_REF:`refs/heads/${channel}`,GITHUB_SHA:c.commit,GITHUB_WORKFLOW_SHA:c.commit,GITHUB_WORKFLOW_REF:`${CORE}/.github/workflows/artifacts.yml@refs/heads/${channel}`,GITHUB_RUN_ID:c.run_id,GITHUB_RUN_ATTEMPT:'1'};
  const state={attempt:1,artifacts:[],document:null,heads:0,reads:0};
  const run={id:10,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',head_sha:c.commit,head_branch:channel,event:'push',status:'in_progress',run_attempt:1};
  const artifact={id:50,name:TEST_SELECTION_ARTIFACT,expired:false,workflow_run:{id:10,head_sha:c.commit}};
  const g={run:()=>run,head:repo=>{if(repo===CORE)return c.commit;state.heads++;return sha(9);},get:path=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(path.includes('/targets.')?m:policy)).toString('base64')}),pages:()=>state.artifacts,artifact:()=>artifact,download:()=>({'selection.json':Buffer.from(JSON.stringify(state.document))})};
  const resolveSnapshots=()=>{state.reads++;return Object.fromEntries(m.targets.map(t=>[t.id,{}]));};
  const save=()=>{const result=prepareProducerTestSelection(g,m,policy,c,{env,resolveSnapshots});state.document=result.document;state.artifacts=[artifact];return result;};
  return {g,m,policy,c,env,state,run,artifact,resolveSnapshots,save};
}
for(const channel of ['dev','main'])test(`${channel} producer publishes once and independently reloads the same source across retries`,()=>{
  const f=fixture(channel);assert.throws(()=>loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}),/missing/);
  const prepared=f.save();assert.equal(prepared.created,true);assert.equal(f.state.heads,1);assert.equal(f.state.reads,1);
  assert.deepEqual(loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}),prepared.selection);
  f.env.GITHUB_RUN_ATTEMPT='2';f.run.run_attempt=2;
  const reused=prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>assert.fail('retry variables')});
  assert.equal(reused.created,false);assert.deepEqual(reused.selection,prepared.selection);assert.equal(f.state.heads,1);
  assert.deepEqual(loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}),prepared.selection);
});
test('missing prior lock never resolves new heads on a later producer attempt',()=>{
  const f=fixture();f.env.GITHUB_RUN_ATTEMPT='2';f.run.run_attempt=2;
  assert.throws(()=>prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>assert.fail('must not resolve')}),/Previous producer attempt/);
  assert.equal(f.state.heads,0);
});
for(const [name,mutate] of Object.entries({
  'duplicate artifacts':f=>f.state.artifacts.push({...f.artifact,id:51}),
  'expired artifact':f=>f.artifact.expired=true,
  'foreign artifact run':f=>f.artifact.workflow_run.id=11,
  'foreign artifact source':f=>f.artifact.workflow_run.head_sha=sha(2),
  'wrong creating attempt':f=>f.state.document.producer.attempt=2,
  'wrong envelope workflow':f=>f.state.document.producer.workflow='.github/workflows/other.yml',
  'corrupt selection':f=>f.state.document.selection.targets['python-cli'].tests.commit=sha(2),
  'extra envelope field':f=>f.state.document.extra=true,
  'policy read denied':f=>f.g.get=()=>{throw Error('403 denied');},
  'artifact read denied':f=>f.g.pages=()=>{throw Error('403 denied');},
  'download denied':f=>f.g.download=()=>{throw Error('403 denied');},
  'immutable policy differs':f=>f.g.get=()=>({type:'file',encoding:'base64',content:Buffer.from('{}').toString('base64')}),
  'wrong runner attempt':f=>f.env.GITHUB_RUN_ATTEMPT='2',
  'foreign runner':f=>f.env.GITHUB_REPOSITORY='foreign/core',
  'manual producer':f=>f.run.event='workflow_dispatch',
  'wrong run source':f=>f.run.head_sha=sha(2),
  'fork producer':f=>f.run.head_repository.full_name='foreign/core',
  'wrong branch':f=>f.run.head_branch='main',
  'finished producer':f=>f.run.status='completed',
  'Core moved':f=>f.g.head=()=>sha(2)
}))test(`producer artifact rejects ${name} without resolving again`,()=>{
  const f=fixture();f.save();mutate(f);
  const expected={
    'duplicate artifacts':/Ambiguous producer test selection artifact/,
    'expired artifact':/artifact identity\/expiry mismatch/,
    'foreign artifact run':/artifact identity\/expiry mismatch/,
    'foreign artifact source':/artifact identity\/expiry mismatch/,
    'wrong creating attempt':/envelope\/creating attempt mismatch/,
    'wrong envelope workflow':/envelope\/creating attempt mismatch/,
    'corrupt selection':/Test selection digest mismatch/,
    'extra envelope field':/envelope\/creating attempt mismatch/,
    'policy read denied':/403 denied/,'artifact read denied':/403 denied/,'download denied':/403 denied/,
    'immutable policy differs':/differs from immutable producer policy/,
    'wrong runner attempt':/producer run\/source\/attempt mismatch/,
    'foreign runner':/exact canonical producer runner/,
    'manual producer':/producer run\/source\/attempt mismatch/,
    'wrong run source':/producer run\/source\/attempt mismatch/,
    'fork producer':/producer run\/source\/attempt mismatch/,
    'wrong branch':/producer run\/source\/attempt mismatch/,
    'finished producer':/producer run\/source\/attempt mismatch/,
    'Core moved':/Core moved before producer test selection/
  }[name];
  assert.ok(expected,'Every mutation needs an expected rejection');
  let fallbacks=0;
  assert.throws(()=>prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>{fallbacks++;assert.fail('no fallback');}}),expected);
  assert.throws(()=>loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}),expected);
  assert.equal(fallbacks,0);assert.equal(f.state.reads,1);assert.equal(f.state.heads,1);
});
test('paired freeze cannot resolve an active policy from current variables implicitly',()=>{
  const f=fixture();assert.throws(()=>freezeCampaign({},f.m,f.policy,f.c,{verificationOnly:true}),/authenticated durable/);
});

for(const channel of ['dev','main'])test(`${channel} effective v2 captures persist and recover without settings/source reread`,()=>{
 const f=fixture(channel),variables={},captures={};
 for(const t of f.m.targets){
  const request=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel,producer:{repository:CORE,commit:f.c.commit,run_id:f.c.run_id,attempt:1},target:{id:t.id,repository:t.repository},source:{ref:`refs/heads/${channel}`,commit:sha(2),workflow:SNAPSHOT_WORKFLOW,workflow_blob:sha(3),tooling_tree:sha(4),reviewed_commit:sha(2),actor_id:'123'},nonce:'a'.repeat(64)});
  variables[t.id]={WSS_USE_PIN_TESTS:'false'};
  captures[t.id]={request,settings:variables[t.id],binding:{request_id:request.request_id,run_id:'99',artifact_id:'100',archive_digest:'sha256:'+'b'.repeat(64),document_digest:'c'.repeat(64)}};
 }
 const prepared=prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>({schema:'wss-authenticated-effective-variables/v1',variables,captures})});
 assert.equal(prepared.document.schema,'wss-test-selection-artifact/v2');assert.equal(f.state.heads,1);
 f.state.document=prepared.document;f.state.artifacts=[f.artifact];f.env.GITHUB_RUN_ATTEMPT='2';f.run.run_attempt=2;
 const result=prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>assert.fail('recaptured settings')});
 assert.equal(result.created,false);assert.equal(result.selection.selection_id,prepared.selection.selection_id);assert.equal(f.state.heads,1);
 f.state.document.captures[f.m.targets[0].id].binding.request_id='d'.repeat(64);
 assert.throws(()=>loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}),/capture binding/);
});

for(const channel of ['dev','main'])test(`${channel} new snapshot sources cannot move between capture and paired freeze; historical v1 remains readable`,()=>{
 const document={schema:'wss-test-selection-artifact/v2',selection:{selection_id:'a'.repeat(64)},captures:{'python-wrapper':{request:{source:{commit:sha(2)},target:{repository:'owner/python'},channel}}}};
 const pair={acceptance:{test_selection_id:'a'.repeat(64),target_snapshots:{'python-wrapper':{head_sha:sha(2)}}},stage:{test_selection_id:'a'.repeat(64),policy:{libraries:[{id:'python',repository:'owner/python'}]},receivers:{python:{heads:{[channel]:sha(2)}}}}};
 assertSnapshotSourcesMatchPlans(document,pair,{head:()=>sha(2)});
 pair.acceptance.target_snapshots['python-wrapper'].head_sha=sha(3);
 assert.throws(()=>assertSnapshotSourcesMatchPlans(document,pair,{head:()=>sha(2)}),/Receiver moved/);
 pair.acceptance.target_snapshots['python-wrapper'].head_sha=sha(2);pair.stage.receivers.python.heads[channel]=sha(3);
 assert.throws(()=>assertSnapshotSourcesMatchPlans(document,pair,{head:()=>sha(2)}),/Stage receiver moved/);
 assert.doesNotThrow(()=>assertSnapshotSourcesMatchPlans({schema:'wss-test-selection-artifact/v1'},pair));
});

for(const channel of ['dev','main'])test(`${channel} application source movement with unchanged workflow cannot pass paired source binding`,()=>{
 const id='python-cli',document={schema:'wss-test-selection-artifact/v2',selection:{selection_id:'a'.repeat(64)},captures:{[id]:{request:{source:{commit:sha(2),workflow_blob:sha(9)},target:{repository:'owner/python-cli'},channel}}}};
 const pair={acceptance:{test_selection_id:'a'.repeat(64),target_snapshots:{[id]:{head_sha:sha(2),trusted:{'.github/workflows':sha(9)}}}},stage:{test_selection_id:'a'.repeat(64),policy:{libraries:[]},receivers:{}}};
 assertSnapshotSourcesMatchPlans(document,pair,{head:()=>sha(2)});
 pair.acceptance.target_snapshots[id].head_sha=sha(3);
 assert.throws(()=>assertSnapshotSourcesMatchPlans(document,pair,{head:()=>sha(2)}),/Receiver moved/);
 assert.equal(pair.acceptance.target_snapshots[id].trusted['.github/workflows'],sha(9));
});

for(const channel of ['dev','main'])for(const kind of ['wrapper','cli'])test(`${channel} ${kind} movement after pair snapshot observation aborts before returning plans`,()=>{
 let live=sha(2),returned=false;
 const id=`python-${kind}`,repository=`owner/python-${kind}`;
 const document={schema:'wss-test-selection-artifact/v2',selection:{selection_id:'a'.repeat(64)},captures:{[id]:{request:{source:{commit:sha(2),workflow_blob:sha(9)},target:{repository},channel}}}};
 const pair={acceptance:{test_selection_id:'a'.repeat(64),target_snapshots:{[id]:{head_sha:sha(2),trusted:{'.github/workflows':sha(9)}}}},stage:{test_selection_id:'a'.repeat(64),policy:{libraries:kind==='wrapper'?[{id:'python',repository}]:[]},receivers:kind==='wrapper'?{python:{heads:{[channel]:sha(2)}}}:{}}};
 const g={head:(repo,branch)=>{assert.equal(repo,repository);assert.equal(branch,channel);return live;}};
 assertSnapshotSourcesMatchPlans(document,pair,g);
 live=sha(3); // move AFTER both snapshots, without modifying either executable tree
 assert.throws(()=>{assertSnapshotSourcesMatchPlans(document,pair,g);returned=true;},/after paired source observation/);
 assert.equal(returned,false);
});
