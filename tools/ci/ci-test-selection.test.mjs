import test from 'node:test';
import assert from 'node:assert/strict';
import {selectCiTests} from './ci-test-selection.mjs';
const sha=n=>String(n).repeat(40),repository='cwru-non-academic/Python_WSS_Integration_Lib',targetId='python-wrapper';
const policy={targets:[{id:targetId,repository,default_test_repository:repository,allowed_test_repositories:[repository]}]};
const producer={repository:'cwru-non-academic/WSSCore',commit:sha(1),run_id:'42',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
const input=(event='push')=>({channel:'dev',targetId,policy,run:{repository,commit:sha(2),run_id:'99',event},productCandidate:producer});
const manifest=enabled=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify({channel:'dev',...(enabled?{test_selection_policy:{}}:{})})).toString('base64')});
test('ordinary PR source snapshots matching branch tests without using old product campaign',async()=>{
  let calls=0;const result=await selectCiTests(input('pull_request'),{head:(r,c)=>{calls++;assert.equal(r,repository);assert.equal(c,'dev');return sha(3);},get:()=>{throw Error('No campaign read');}});
  assert.equal(calls,1);assert.equal(result.scope,'run');assert.equal(result.selection.targets[targetId].tests.commit,sha(3));
});
test('current enabled product campaign reuses frozen selection with no variable/test-head reads',async()=>{
  const binding={selection_id:'b'.repeat(64),selection:{targets:{[targetId]:{repository}}},request:{}};let loaded=0;
  const result=await selectCiTests({...input(),variables:{WSS_USE_PIN_TESTS:'invalid'}},{head:r=>{assert.equal(r,producer.repository);return producer.commit;},get:()=>manifest(true)},{loadCampaign:async(_g,parameters)=>{loaded++;assert.deepEqual(parameters.expectedCandidate,producer);return binding;}});
  assert.equal(result.scope,'campaign');assert.equal(result.selection_id,binding.selection_id);assert.equal(loaded,1);
});
test('candidate dispatch never replaces a required campaign with latest tests',async()=>{
  await assert.rejects(()=>selectCiTests({...input('workflow_dispatch'),mode:'candidate'},{head:()=>{throw Error('No latest lookup');},get:()=>manifest(true)},{loadCampaign:async()=>{throw Error('Missing authenticated selection artifact');}}),/Missing authenticated/);
});
test('inaccessible active producer policy does not mean legacy or unset',async()=>{
  await assert.rejects(()=>selectCiTests(input(),{head:()=>producer.commit,get:()=>{throw Error('403');}}),/403/);
});
test('old installed payload and legacy producer policy use fresh run selection',async()=>{
  for(const current of [false,true]) {
    const result=await selectCiTests(input(),{head:r=>r===producer.repository?(current?producer.commit:sha(7)):sha(4),get:()=>manifest(false)});
    assert.equal(result.scope,'run');assert.equal(result.selection.targets[targetId].tests.commit,sha(4));
  }
});
test('run selection uses repository pin while preserving actual target source',async()=>{
  const result=await selectCiTests({...input('pull_request'),variables:{WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:repository,WSS_PIN_TEST_COMMIT:sha(5)}},{head:()=>{throw Error('No floating ref');},get:()=>({sha:sha(5)})});
  assert.equal(result.selection.campaign.commit,sha(2));assert.equal(result.selection.targets[targetId].tests.commit,sha(5));
});

test('run context is checked against actual source and trusted prerequisite ID',async()=>{
  const {verifyCiTestBinding}=await import('./ci-test-selection.mjs');
  const x=input('pull_request'),selected=await selectCiTests(x,{head:()=>sha(3)});
  assert.equal((await verifyCiTestBinding(selected,policy,targetId,x.run,selected.selection_id,{})).selection_id,selected.selection_id);
  await assert.rejects(()=>verifyCiTestBinding(selected,policy,targetId,{...x.run,commit:sha(9)},selected.selection_id,{}),/another campaign/);
  await assert.rejects(()=>verifyCiTestBinding(selected,policy,targetId,x.run,'a'.repeat(64),{}),/prerequisite output/);
});
test('default source policy keeps all required targets and their own bundle owners',async()=>{
  const {readFileSync}=await import('node:fs');const {testSelectionPolicy}=await import('../release/campaign-test-selection.mjs');
  const source=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url)));
  const manifest=JSON.parse(readFileSync(new URL('../release/targets.dev.json',import.meta.url)));
  assert.equal(testSelectionPolicy({...manifest,test_selection_policy:source}),source);
  assert.ok(source.targets.every(t=>t.default_test_repository===t.repository&&t.allowed_test_repositories.length===1));
});

test('repository variable reader is invoked only for fresh run selections',async()=>{
  let reads=0;const variables=()=>{reads++;return {};};
  await selectCiTests({...input('pull_request'),variables},{head:()=>sha(3)});assert.equal(reads,1);
  const binding={selection_id:'b'.repeat(64),selection:{targets:{[targetId]:{repository}}},request:{}};
  await selectCiTests({...input(),variables},{head:()=>producer.commit,get:()=>manifest(true)},{loadCampaign:async()=>binding});
  assert.equal(reads,1,'Frozen campaign cannot reread changed settings');
});

// Pure contract negatives supplement the actual authenticated child-CLI tests.
// Synthetic fixture identities below are not campaign or readiness evidence.
import {readFileSync} from 'node:fs';
import {digest} from '../release/verify.mjs';
import {resolveTestSelection} from './test-selection.mjs';
import {createCiExecutorHandoff,verifyCiExecutorHandoff,assertNoExecutorBootstrapCredentials} from './ci-test-executor-handoff.mjs';
function executorFixture({campaign=false}={}) {
  const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url)));
  const run={repository,commit:sha(2),run_id:'99',event:campaign?'workflow_dispatch':'push'},channel='dev';
  const request={channel,campaign:campaign?{repository:producer.repository,commit:producer.commit,run_id:producer.run_id}:{repository,commit:run.commit,run_id:run.run_id},targets:(campaign?policy.targets:[policy.targets.find(t=>t.id===targetId)]).map(t=>({...t,variables:{}}))};
  const selection=resolveTestSelection(request,{head:()=>sha(3)});
  const context={scope:campaign?'campaign':'run',selection,request,selection_id:selection.selection_id};
  const env={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:repository,GITHUB_SHA:run.commit,GITHUB_RUN_ID:run.run_id,GITHUB_EVENT_NAME:run.event,GITHUB_RUN_ATTEMPT:'1',GITHUB_REF:'refs/heads/dev',GITHUB_BASE_REF:'',GITHUB_WORKFLOW_REF:`${repository}/.github/workflows/ci.yml@refs/heads/dev`,GITHUB_WORKFLOW_SHA:run.commit,WSS_CI_CHANNEL:channel,GH_TOKEN:'private-auth-only',WSS_GITHUB_APP_PRIVATE_KEY:'private-key-auth-only'};
  const identity={...run,api_head_sha:run.commit,workflow:'.github/workflows/ci.yml',workflow_ref:env.GITHUB_WORKFLOW_REF,workflow_sha:run.commit,channel,targetId,policy,mode:campaign?'candidate':'committed',productCandidate:campaign?producer:null};
  const saved={document:{schema:'wss-ci-test-context/v2',identity,context:{...context,secret:'do-not-export-raw-context'}},context,binding:context,artifact:{id:'123',name:`wss-ci-test-context-${targetId}`,created_at:'2026-01-01T00:01:00Z'}};
  const handoff=createCiExecutorHandoff(saved,campaign?{kind:'acceptance',producer,plan_id:'c'.repeat(64),policy_sha256:'d'.repeat(64)}:null,env);
  const verify=(value=handoff,expectedDigest=digest(value),other={})=>verifyCiExecutorHandoff(value,policy,targetId,run,selection.selection_id,expectedDigest,{env:{...env,...other}});
  return {handoff,verify,env,selection};
}
test('executor serializer has a closed non-secret projection',()=>{
  const {handoff,verify,selection}=executorFixture();assert.equal(verify().selection_id,selection.selection_id);
  assert.doesNotMatch(JSON.stringify(handoff),/private-auth-only|private-key-auth-only|do-not-export-raw-context|GH_TOKEN|PRIVATE_KEY/);
});
for(const [name,mutate,pattern] of [
  ['extra top-level credential',x=>x.token='secret',/schema\/fields/],
  ['readiness schema',x=>x.schema='wss-local-test-variable-readiness-report/v1',/schema\/fields/],
  ['durable context as handoff',x=>x.schema='wss-ci-test-context/v2',/schema\/fields/],
  ['unknown identity fields',x=>x.identity.token='secret',/identity fields/],
  ['foreign policy',x=>x.identity.policy.targets[0].repository='foreign/repository',/policy\/target/],
  ['source replay',x=>x.identity.commit=sha(8),/run\/source/],
  ['run replay',x=>x.identity.run_id='100',/run\/source/],
  ['event change',x=>x.identity.event='workflow_dispatch',/run\/source/],
  ['attempt replay',x=>x.execution.attempt=2,/attempt\/ref/],
  ['noninteger attempt',x=>x.execution.attempt=1.5,/attempt\/ref/],
  ['unknown execution fields',x=>x.execution.secret='secret',/attempt\/ref/],
  ['workflow path substitution',x=>x.identity.workflow='.github/workflows/other.yml',/workflow\/source/],
  ['workflow ref substitution',x=>x.identity.workflow_ref='foreign/repository/.github/workflows/ci.yml@refs/heads/dev',/workflow\/source/],
  ['workflow SHA substitution',x=>x.identity.workflow_sha=sha(8),/workflow\/source/],
  ['API SHA substitution',x=>x.identity.api_head_sha=sha(8),/workflow\/source/],
  ['invalid mode',x=>x.identity.mode='readiness',/mode/],
  ['candidate mode without dispatch',x=>x.identity.mode='candidate',/mode/],
  ['invalid candidate',x=>x.identity.productCandidate={run_id:'42'},/six fields/],
  ['origin readiness schema',x=>x.origin.schema='wss-readiness/v1',/origin/],
  ['origin extra credentials',x=>x.origin.token='secret',/origin/],
  ['origin foreign artifact',x=>x.origin.artifact_name='wss-ci-test-context-matlab-cli',/origin/],
  ['origin invented artifact ID',x=>x.origin.artifact_id='not-an-id',/origin/],
  ['origin second attempt',x=>x.origin.attempt=2,/origin/],
  ['context credentials',x=>x.context.token='secret',/selection differs/],
  ['context selection-ID substitution',x=>x.context.selection_id='f'.repeat(64),/selection differs/],
  ['context scope substitution',x=>x.context.scope='readiness',/selection differs/],
  ['selection mutation',x=>x.context.selection.targets[targetId].tests.commit=sha(8),/digest/],
  ['request extra fields',x=>x.context.request.secret='secret',/exactly channel/],
  ['request foreign producer',x=>x.context.request.campaign.run_id='100',/another campaign/],
  ['nonempty captured variables',x=>x.context.request.targets[0].variables.WSS_USE_PIN_TESTS='true',/selected context differs/],
  ['ordinary fake campaign authority',x=>x.campaign={kind:'acceptance'},/ordinary context/],
  ['campaign without provenance',x=>x.context.scope='campaign',/campaign authority/]
])test(`executor rejects ${name} even with a transport-matching digest`,()=>{
  const {handoff,verify}=executorFixture();mutate(handoff);assert.throws(()=>verify(handoff),pattern);
});
test('executor does not accept its own digest or selection ID as authentication',()=>{
  const {handoff,verify}=executorFixture();assert.throws(()=>verify(handoff,''),/trusted prerequisite/);
});
for(const name of ['GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','WSS_GITHUB_APP_PRIVATE_KEY','WSS_TEST_VARIABLE_READ_TOKEN','INPUT_TOKEN','INPUT_PRIVATE-KEY','ACTIONS_ID_TOKEN_REQUEST_TOKEN'])test(`executor rejects bootstrap credential ${name}`,()=>{
  assert.throws(()=>assertNoExecutorBootstrapCredentials({[name]:'secret'}),/bootstrap credentials/);
  assertNoExecutorBootstrapCredentials({[name]:''});
});

test('campaign handoff retains full producer identity and selection authority',()=>{
  const {handoff,verify}=executorFixture({campaign:true});assert.equal(verify().selection_id,handoff.context.selection_id);
  assert.deepEqual(handoff.campaign.producer,producer);assert.equal(Object.keys(handoff.context.selection.targets).length,9);
});
for(const [name,mutate,pattern] of [
  ['missing plan authority',x=>x.campaign=null,/campaign authority/],
  ['unknown plan kind',x=>x.campaign.kind='readiness',/campaign authority/],
  ['invalid plan ID',x=>x.campaign.plan_id='invented',/campaign authority/],
  ['invalid policy digest',x=>x.campaign.policy_sha256='invented',/campaign authority/],
  ['foreign producer candidate',x=>x.campaign.producer.commit=sha(8),/campaign authority/],
  ['changed expected candidate',x=>x.identity.productCandidate.sha256='e'.repeat(64),/campaign authority/],
  ['extra plan credentials',x=>x.campaign.token='secret',/campaign authority/],
  ['producer replay',x=>{x.campaign.producer.run_id='43';x.identity.productCandidate.run_id='43';},/campaign producer/],
  ['ordinary downgrade',x=>{x.context.scope='run';x.campaign=null;},/ordinary context/]
])test(`campaign executor rejects ${name}`,()=>{
  const {handoff,verify}=executorFixture({campaign:true});mutate(handoff);assert.throws(()=>verify(),pattern);
});
