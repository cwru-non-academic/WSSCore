import {createSnapshotRequest,SNAPSHOT_WORKFLOW} from './local-test-variable-snapshot.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {CORE} from './verify.mjs';
import {CAMPAIGN_TEST_POLICY} from './campaign-test-selection.mjs';
import {freezeCampaign} from './campaign-freeze.mjs';
import {prepareProducerTestSelection,loadProducerTestSelection,TEST_SELECTION_ARTIFACT} from './campaign-selection-artifact.mjs';
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
  assert.throws(()=>prepareProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env,resolveSnapshots:()=>assert.fail('no fallback')}));
  assert.throws(()=>loadProducerTestSelection(f.g,f.m,f.policy,f.c,{env:f.env}));
  assert.equal(f.state.reads,1);
});
test('paired freeze cannot resolve an active policy from current variables implicitly',()=>{
  const f=fixture();assert.throws(()=>freezeCampaign({},f.m,f.policy,f.c,{verificationOnly:true}),/authenticated durable/);
});

for(const channel of ['dev','main'])test(`${channel} effective v2 captures persist and recover without settings/source reread`,()=>{
 const f=fixture(channel),variables={},captures={};
 for(const t of f.m.targets){
  const request=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel,producer:{repository:CORE,commit:f.c.commit,run_id:f.c.run_id,attempt:1},target:{id:t.id,repository:t.repository},source:{ref:`refs/heads/${channel}`,commit:sha(2),workflow:SNAPSHOT_WORKFLOW,workflow_blob:sha(3),actor_id:'123'},nonce:'a'.repeat(64)});
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
