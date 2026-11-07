import test from 'node:test';
import assert from 'node:assert/strict';
import {CORE,digest} from './verify.mjs';
import {createSnapshotRequest,captureLocalTestVariables,collectLocalTestVariables,normalizeEffectiveTestVariables,dispatchSnapshotOnce,snapshotName,snapshotTitle,SNAPSHOT_WORKFLOW,SNAPSHOT_JOB} from './local-test-variable-snapshot.mjs';
const sha=n=>String(n).repeat(40),repository='cwru-non-academic/TestReceiver';
function fixture(channel='dev') {
 const r=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel,producer:{repository:CORE,commit:sha(1),run_id:'10',attempt:1},target:{id:'test-target',repository},source:{ref:`refs/heads/${channel}`,commit:sha(2),workflow:SNAPSHOT_WORKFLOW,workflow_blob:sha(3),actor_id:'123'},nonce:'a'.repeat(64)});
 const env={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:repository,GITHUB_EVENT_NAME:'workflow_dispatch',GITHUB_REF:r.source.ref,GITHUB_SHA:sha(2),GITHUB_WORKFLOW_SHA:sha(2),GITHUB_WORKFLOW_REF:`${repository}/${SNAPSHOT_WORKFLOW}@${r.source.ref}`,GITHUB_ACTOR_ID:'123',GITHUB_RUN_ATTEMPT:'1',GITHUB_RUN_ID:'20',WSS_USE_PIN_TESTS:'false'};
 const document=captureLocalTestVariables(r,env);
 const run={id:20,repository:{full_name:repository},head_repository:{full_name:repository,fork:false},event:'workflow_dispatch',path:SNAPSHOT_WORKFLOW,head_branch:channel,head_sha:sha(2),run_attempt:1,actor:{id:123},triggering_actor:{id:123},pull_requests:[],display_title:snapshotTitle(r),workflow_id:30,status:'completed',conclusion:'success'};
 const artifact={id:40,name:snapshotName(r),expired:false,workflow_run:{id:20,head_sha:sha(2)},digest:'sha256:'+'b'.repeat(64)};
 const job={name:SNAPSHOT_JOB,status:'completed',conclusion:'success',run_id:20,head_sha:sha(2)};
 const state={runs:[run],artifacts:[artifact],jobs:[job],posts:0};
 const g={head:()=>sha(2),get:()=>({path:SNAPSHOT_WORKFLOW,state:'active'}),object:()=>({sha:sha(3),mode:'100644',type:'blob'}),pages:path=>path.includes('/artifacts')?state.artifacts:state.runs,run:()=>run,jobs:()=>state.jobs,artifact:()=>artifact,downloadVerified:()=>({'snapshot.json':Buffer.from(JSON.stringify(document))})};
 return {r,env,document,run,artifact,job,state,g};
}
for(const channel of ['dev','main'])test(`${channel} local capture and authenticated collection`,()=>{
 const f=fixture(channel);assert.deepEqual(collectLocalTestVariables(f.g,f.r).settings,{WSS_USE_PIN_TESTS:'false'});
});
for(const value of [undefined,'','false'])test(`latest ignores stale disabled pin fields: ${value}`,()=>assert.deepEqual(normalizeEffectiveTestVariables({WSS_USE_PIN_TESTS:value,WSS_PIN_TEST_REPOSITORY:'bad',WSS_PIN_TEST_COMMIT:'bad'}),{WSS_USE_PIN_TESTS:'false'}));
for(const value of ['True','TRUE',' true','0',false,null])test(`reject malformed flag ${value}`,()=>{
 assert.throws(()=>normalizeEffectiveTestVariables({WSS_USE_PIN_TESTS:value}));
});
test('effective mapping honors GitHub repo-over-org values, never claims repository-only origin',()=>{
 const org={WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:repository,WSS_PIN_TEST_COMMIT:sha(4)};
 assert.equal(normalizeEffectiveTestVariables({...org,WSS_USE_PIN_TESTS:'false'}).WSS_USE_PIN_TESTS,'false');
 assert.equal(normalizeEffectiveTestVariables(org).WSS_PIN_TEST_COMMIT,sha(4));
});
for(const pin of ['', 'dev','a'.repeat(39),'A'.repeat(40),'$(echo unsafe)'])test(`reject invalid enabled commit ${pin}`,()=>assert.throws(()=>normalizeEffectiveTestVariables({WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:repository,WSS_PIN_TEST_COMMIT:pin})));
for(const [name,mutate] of Object.entries({
 'fork':f=>f.run.head_repository.fork=true,
 'foreign repository':f=>f.run.head_repository.full_name='bad/repo',
 'PR':f=>f.run.event='pull_request',
 'wrong actor':f=>f.run.actor.id=456,
 'wrong rerun actor':f=>f.run.triggering_actor.id=456,
 'rerun':f=>f.run.run_attempt=2,
 'wrong source':f=>f.run.head_sha=sha(4),
 'wrong workflow':f=>f.run.path='.github/workflows/other.yml',
 'cross-channel':f=>f.run.head_branch='main',
 'failed run':f=>f.run.conclusion='failure',
 'skipped job':f=>f.job.conclusion='skipped',
 'missing job':f=>f.state.jobs=[],
 'duplicate job':f=>f.state.jobs.push(f.job),
 'missing run':f=>f.state.runs=[],
 'duplicate run':f=>f.state.runs.push(f.run),
 'missing artifact':f=>f.state.artifacts=[],
 'duplicate artifact':f=>f.state.artifacts.push(f.artifact),
 'expired artifact':f=>f.artifact.expired=true,
 'wrong artifact run':f=>f.artifact.workflow_run.id=21,
 'wrong archive digest':f=>f.artifact.digest='sha256:bad',
 'replayed request':f=>f.document.request={...f.r,nonce:'d'.repeat(64)},
 'wrong local run':f=>f.document.local.run_id='21',
 'malformed settings':f=>f.document.settings.WSS_USE_PIN_TESTS='TRUE',
 'extra document field':f=>f.document.other=true,
 'untrusted workflow blob':f=>f.g.object=()=>({sha:sha(4),mode:'100644',type:'blob'}),
 'symlink workflow':f=>f.g.object=()=>({sha:sha(3),mode:'120000',type:'blob'}),
 'denied artifact':f=>f.g.downloadVerified=()=>{throw Error('403');}
}))test(`collection rejects ${name}`,()=>{const f=fixture();mutate(f);assert.throws(()=>collectLocalTestVariables(f.g,f.r));});
test('dispatch requires durable intent and never retries uncertainty',()=>{
 const f=fixture();f.state.runs=[];let posts=0;const options={authenticatedIntent:f.r,newIntent:true,dispatch:()=>{posts++;throw Error('uncertain POST');}};
 assert.throws(()=>dispatchSnapshotOnce(f.g,f.r,{...options,authenticatedIntent:null}));assert.equal(posts,0);
 assert.throws(()=>dispatchSnapshotOnce(f.g,f.r,options),/uncertain POST/);assert.equal(posts,1);
 assert.throws(()=>dispatchSnapshotOnce(f.g,f.r,{...options,newIntent:false}),/uncertain/);assert.equal(posts,1);
 f.state.runs=[f.run];assert.equal(dispatchSnapshotOnce(f.g,f.r,{...options,newIntent:false}).run_id,'20');assert.equal(posts,1);
});
test('source movement after intent blocks dispatch',()=>{const f=fixture();f.state.runs=[];f.g.head=()=>sha(4);assert.throws(()=>dispatchSnapshotOnce(f.g,f.r,{newIntent:true,authenticatedIntent:f.r,dispatch:()=>assert.fail('write')}),/moved/);});
for(const key of ['GITHUB_SHA','GITHUB_WORKFLOW_SHA','GITHUB_ACTOR_ID','GITHUB_REF','GITHUB_RUN_ATTEMPT','GITHUB_EVENT_NAME'])test(`capture rejects ${key} mismatch`,()=>{const f=fixture();assert.throws(()=>captureLocalTestVariables(f.r,{...f.env,[key]:'wrong'}));});
