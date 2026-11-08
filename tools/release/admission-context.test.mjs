import test from 'node:test';
import assert from 'node:assert/strict';
import {detachAdmission,createAdmissionRefresh,refreshPreflightMetadata,refreshEmitterMetadata} from './admission-context.mjs';
import {bytesDigest} from './verify.mjs';
function fixture(){
 const repository='cwru-non-academic/Python_WSS_Integration_Lib',source='a'.repeat(40),run={id:1,run_attempt:1,repository:{full_name:repository},head_repository:{full_name:repository},head_sha:source,head_branch:'dev',path:'.github/workflows/wss-preflight.yml',event:'workflow_dispatch',status:'completed',conclusion:'success'};
 const jobs=[{id:'2',name:'native',status:'completed',conclusion:'success'}],binding={id:'3',name:'proof',digest:'sha256:'+bytesDigest(Buffer.from('proof'))};
 const descriptor={repository,run_id:'1',source_sha:source,workflow:run.path,attempt:1,jobs,artifacts:[binding],logs:[]},artifact={...binding,expired:false,workflow_run:{id:1,head_sha:source}},proof={run,jobs,artifacts:[binding],logs:[]};let reads=0;
 const g={run:()=>{reads++;return run;},jobs:()=>jobs,pages:()=>[artifact],get:()=>artifact,artifactBytes:()=>assert.fail('bytes'),jobLogBytes:()=>assert.fail('logs')};
 return {g,run,jobs,artifact,descriptor,proof,p:{channel:'dev',manifest:{targets:[{id:'python-wrapper',repository}]}},reads:()=>reads};
}
test('V7 detached nested metadata and Buffer bytes cannot alias caller',()=>{const original={bytes:Buffer.from('ok'),nested:{id:1}},copy=detachAdmission(original);original.bytes[0]=0;original.nested.id=2;assert.equal(copy.bytes.toString(),'ok');assert.equal(copy.nested.id,1);});
test('V7 native refresh deduplicates exact expectations without caching final brackets',()=>{const f=fixture(),session=createAdmissionRefresh(f.g),capture=detachAdmission({descriptor:f.descriptor,proof:f.proof,candidate:{commit:'a'}});refreshPreflightMetadata(f.g,f.p,capture,{session});const n=f.reads();refreshPreflightMetadata(f.g,f.p,capture,{session});assert.equal(f.reads(),n);session.finish();assert.equal(f.reads(),n+1);const wrong=detachAdmission(capture);wrong.candidate.commit='b';assert.throws(()=>refreshPreflightMetadata(f.g,f.p,wrong,{session}),/Conflicting/);});
test('V7 final bracket rejects late preflight rerun',()=>{const f=fixture(),session=createAdmissionRefresh(f.g);refreshPreflightMetadata(f.g,f.p,detachAdmission({descriptor:f.descriptor,proof:f.proof,candidate:{}}),{session});f.run.run_attempt++;assert.throws(()=>session.finish());});
test('V7 emitter running identity does not impose completed-success',()=>{const f=fixture();f.run.path='.github/workflows/release-receipt.yml';f.run.status='queued';f.run.conclusion=null;const session=createAdmissionRefresh(f.g);refreshEmitterMetadata(f.g,f.p,f.p.manifest.targets[0],detachAdmission(f.run),{running:true,session});f.run.status='in_progress';session.finish();f.run.run_attempt++;assert.throws(()=>session.finish());});
test('V7 one refresh rejects two attempts of the same physical run',()=>{const f=fixture(),session=createAdmissionRefresh(f.g);session.bracket(f.descriptor.repository,detachAdmission(f.run));f.run.run_attempt++;assert.throws(()=>session.bracket(f.descriptor.repository,detachAdmission(f.run)),/Conflicting captured run attempts/);});
import {GitHub} from './github.mjs';
test('V7 artifact lookup checks list uniqueness and exact detail binding',()=>{const g=new GitHub(),a={id:4,name:'receipt',expired:false};g.pages=()=>[a,a];assert.throws(()=>g.artifact('repo','1','receipt'),/duplicate/);g.pages=()=>[a];for(const bad of [{...a,id:5},{...a,name:'other'},{...a,expired:true}]){g.get=()=>bad;assert.throws(()=>g.artifact('repo','1','receipt'),/detail/);}g.get=()=>a;assert.equal(g.artifact('repo','1','receipt').id,4);});

import {readFileSync} from 'node:fs';
import {CORE} from './verify.mjs';
import {CAMPAIGN_TEST_POLICY,selectionRequest,selectionFields,targetTestEvidence,selectedTestArtifactName} from './campaign-test-selection.mjs';
import {resolveTestSelection} from './test-selection-contract.mjs';
function sharedFixture(channel,selected) {
 const f=fixture();f.run.head_branch=channel;f.p.channel=channel;
 if(selected){
  const manifest=JSON.parse(readFileSync(new URL(`targets.${channel}.json`,import.meta.url)));
  manifest.test_selection_policy={schema:CAMPAIGN_TEST_POLICY,targets:manifest.targets.map(t=>({id:t.id,repository:t.repository,default_test_repository:'cwru-non-academic/Tests',allowed_test_repositories:['cwru-non-academic/Tests']}))};
  const candidate={repository:CORE,commit:'a'.repeat(40),run_id:'11',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'b'.repeat(64)};
  f.p={channel,manifest,candidate,...selectionFields(manifest,candidate,resolveTestSelection(selectionRequest(manifest,candidate),{head:()=> 'c'.repeat(40)}))};
  f.selected={artifact:{id:4,name:selectedTestArtifactName('python-wrapper',1),expired:false,workflow_run:{id:1,head_sha:f.run.head_sha}},bytes:Buffer.from(JSON.stringify(targetTestEvidence(f.p,'python-wrapper')))};
  f.g.artifact=()=>f.selected.artifact;
 }
 f.capture=detachAdmission({descriptor:f.descriptor,proof:f.proof,candidate:f.p.candidate||{commit:'captured'},...(selected?{selected_tests:f.selected}:{})});
 return f;
}
function cached(f){const session=createAdmissionRefresh(f.g);refreshPreflightMetadata(f.g,f.p,f.capture,{session});return session;}
const harmless={
 updated_at:x=>x.proof.run.updated_at='later',
 display_title:x=>x.proof.run.display_title='Unrelated display label',
 unused_proof:x=>x.proof.ignored='unrelated',
 numeric_string_id:x=>x.proof.run.id=String(x.proof.run.id)
};
for(const channel of ['dev','main'])for(const selected of [false,true])for(const [name,mutate]of Object.entries(harmless))test(`V8 shared ${channel} selected=${selected} permits ${name} across receivers`,()=>{
 const f=sharedFixture(channel,selected),session=cached(f),second=detachAdmission(f.capture),before=f.reads();mutate(second);
 assert.doesNotThrow(()=>refreshPreflightMetadata(f.g,f.p,second,{session}));assert.equal(f.reads(),before,'Same physical proof must be deduplicated');session.finish();assert.equal(f.reads(),before+1,'Final live bracket is never cached');
});
for(const channel of ['dev','main'])for(const change of ['artifact_metadata','numeric_string_artifact_id','JSON_whitespace'])test(`V8 selected ${channel} permits ${change} without adopting replacement`,()=>{
 const f=sharedFixture(channel,true),session=cached(f),second=detachAdmission(f.capture),before=f.reads();
 if(change==='artifact_metadata'){second.selected_tests.artifact.size_in_bytes=999;second.selected_tests.artifact.workflow_run.extra='ignored';}
 if(change==='numeric_string_artifact_id'){second.selected_tests.artifact.id=String(second.selected_tests.artifact.id);second.selected_tests.artifact.workflow_run.id='1';}
 if(change==='JSON_whitespace')second.selected_tests.bytes=Buffer.from(JSON.stringify(JSON.parse(second.selected_tests.bytes),null,2));
 const result=refreshPreflightMetadata(f.g,f.p,second,{session});assert.equal(f.reads(),before);assert.equal(result.selected_tests.artifact.id,4);assert.ok(result.selected_tests.bytes.equals(f.capture.selected_tests.bytes));session.finish();
});
for(const channel of ['dev','main'])test(`V8 legacy ${channel} ignores unconsumed selected fields`,()=>{
 const f=sharedFixture(channel,false),session=cached(f),second=detachAdmission(f.capture);second.selected_tests={artifact:'ignored',bytes:new Uint8Array([1,2])};
 const result=refreshPreflightMetadata(f.g,f.p,second,{session});assert.equal(Object.hasOwn(result,'selected_tests'),false);session.finish();
});
const conflicts={
 source:x=>{x.descriptor.source_sha='d'.repeat(40);x.proof.run.head_sha=x.descriptor.source_sha;},
 attempt:x=>{x.descriptor.attempt=2;x.proof.run.run_attempt=2;},
 channel:x=>x.proof.run.head_branch=x.proof.run.head_branch==='dev'?'main':'dev',
 jobs:x=>{x.descriptor.jobs[0].name='different native job';x.proof.jobs[0].name='different native job';},
 artifact_id:x=>{x.descriptor.artifacts[0].id='99';x.proof.artifacts[0].id='99';},
 artifact_digest:x=>{x.descriptor.artifacts[0].digest='sha256:'+'e'.repeat(64);x.proof.artifacts[0].digest=x.descriptor.artifacts[0].digest;},
 logs:x=>{x.descriptor.logs=[{job_id:'2',sha256:'f'.repeat(64)}];x.proof.logs=structuredClone(x.descriptor.logs);},
 candidate:x=>x.candidate.commit='f'.repeat(40),
 run_event:x=>x.proof.run.event='push',
 run_status:x=>x.proof.run.status='in_progress',
 malformed_proof:x=>x.proof.artifacts=[]
};
for(const channel of ['dev','main'])for(const selected of [false,true])for(const [name,mutate]of Object.entries(conflicts))test(`V8 shared ${channel} selected=${selected} rejects meaningful ${name}`,()=>{
 const f=sharedFixture(channel,selected),session=cached(f),second=detachAdmission(f.capture);mutate(second);assert.throws(()=>refreshPreflightMetadata(f.g,f.p,second,{session}));
});
for(const channel of ['dev','main'])for(const change of ['non_Buffer','missing_artifact','missing_bytes','wrong_payload','artifact_id','artifact_digest','artifact_expiry','owner_run','owner_source','missing_selection','partial_selection'])test(`V8 selected ${channel} revalidates cache-hit ${change}`,()=>{
 const f=sharedFixture(channel,true),session=cached(f),second=detachAdmission(f.capture);
 if(change==='non_Buffer')second.selected_tests.bytes=new Uint8Array(second.selected_tests.bytes);
 if(change==='missing_artifact')delete second.selected_tests.artifact;
 if(change==='missing_bytes')delete second.selected_tests.bytes;
 if(change==='wrong_payload'){const body=JSON.parse(second.selected_tests.bytes);body.test_commit='f'.repeat(40);second.selected_tests.bytes=Buffer.from(JSON.stringify(body));}
 if(change==='artifact_id')second.selected_tests.artifact.id=99;
 if(change==='artifact_digest')second.selected_tests.artifact.digest='sha256:'+'f'.repeat(64);
 if(change==='artifact_expiry')second.selected_tests.artifact.expired=true;
 if(change==='owner_run')second.selected_tests.artifact.workflow_run.id=99;
 if(change==='owner_source')second.selected_tests.artifact.workflow_run.head_sha='f'.repeat(40);
 if(change==='missing_selection')delete f.p.test_selection;
 if(change==='partial_selection')delete f.p.test_selection.targets['python-wrapper'];
 assert.throws(()=>refreshPreflightMetadata(f.g,f.p,second,{session}));
});
