import test from 'node:test';
import assert from 'node:assert/strict';
import {bytesDigest} from './verify.mjs';
import {startPreflightProof,capturePreflightArtifact,capturePreflightLog,collectPreflightProof,verifyPreflightProof,proofDescriptor} from './preflight-proof.mjs';
function fixture(channel='dev') {
 const repo='cwru-non-academic/Python_WSS_Integration_Lib',source='a'.repeat(40),bytes=Buffer.from('exact rich ZIP fixture'),log=Buffer.from('exact log\r\nUnicode: λ\n');
 const run={id:30,run_attempt:2,repository:{full_name:repo},head_repository:{full_name:repo},path:'.github/workflows/wss-preflight.yml',head_sha:source,head_branch:channel,event:'workflow_dispatch',status:'completed',conclusion:'success'};
 const jobs=[{id:40,name:'Native required proof',status:'completed',conclusion:'success'}];
 const artifact={id:50,name:'rich-proof',digest:'sha256:'+bytesDigest(bytes),expired:false,workflow_run:{id:30,head_sha:source}};
 const state={run,jobs,artifact,bytes,log};
 const g={run:()=>structuredClone(state.run),jobs:()=>structuredClone(state.jobs),pages:()=>[structuredClone(state.artifact)],get:()=>structuredClone(state.artifact),artifactBytes:()=>state.bytes,jobLogBytes:()=>state.log};
 const capture=()=>{const s=startPreflightProof(g,{repository:repo,run_id:'30',source_sha:source,attempt:2});capturePreflightArtifact(g,s,'rich-proof');capturePreflightLog(g,s,'40');return s;};
 return {g,state,capture};
}
for(const channel of ['dev','main'])for(const role of ['csharp-wrapper','csharp-cli','unity-wrapper','unity-gui','python-wrapper','python-cli','python-ros','matlab-wrapper','matlab-cli']) {
 test(`${role}/${channel} retains native observed attempt and independently rechecks exact evidence`,()=>{
  const f=fixture(channel),d=f.capture().descriptor;assert.equal(d.attempt,2);verifyPreflightProof(d,collectPreflightProof(f.g,d));
  f.state.run.run_attempt=3;assert.throws(()=>collectPreflightProof(f.g,d),/attempt/);assert.throws(()=>verifyPreflightProof(d,{run:f.state.run,jobs:d.jobs,artifacts:d.artifacts,logs:d.logs}),/attempt/);
 });
}
for(const [name,mutate] of Object.entries({
 source:f=>f.state.run.head_sha='b'.repeat(40),runId:f=>f.state.run.id=31,
 attempt:f=>f.state.run.run_attempt++,jobMissing:f=>f.state.jobs=[],jobSkipped:f=>f.state.jobs[0].conclusion='skipped',jobDuplicate:f=>f.state.jobs.push({...f.state.jobs[0]}),jobReplaced:f=>f.state.jobs[0].id=41,
 artifactReplaced:f=>f.state.artifact.id=51,artifactExpired:f=>f.state.artifact.expired=true,artifactSource:f=>f.state.artifact.workflow_run.head_sha='b'.repeat(40),artifactDigest:f=>f.state.artifact.digest='sha256:'+'0'.repeat(64),archiveBytes:f=>f.state.bytes=Buffer.from('replaced ZIP'),logBytes:f=>f.state.log=Buffer.from('changed\n'),failedRun:f=>f.state.run.conclusion='failure'
}))test(`independent collector rejects ${name}`,()=>{const f=fixture(),d=f.capture().descriptor;mutate(f);assert.throws(()=>collectPreflightProof(f.g,d));});
test('capture refuses replacement of the attempt observed by native Wait',()=>{const f=fixture();f.state.run.run_attempt=3;assert.throws(()=>f.capture(),/attempt/);});
test('artifact capture refuses duplicate names, IDs and live list ambiguity',()=>{const f=fixture(),s=f.capture();assert.throws(()=>capturePreflightArtifact(f.g,s,'rich-proof'),/Repeated/);f.g.pages=()=>[f.state.artifact,f.state.artifact];assert.throws(()=>collectPreflightProof(f.g,s.descriptor),/duplicate/);});
test('descriptor cannot omit attempt, jobs, artifacts or add arbitrary fields',()=>{const f=fixture(),d=f.capture().descriptor;for(const key of ['attempt','jobs','artifacts','logs']){const copy=structuredClone(d);delete copy[key];assert.throws(()=>proofDescriptor(copy));}assert.throws(()=>proofDescriptor({...d,skip:true}));assert.throws(()=>proofDescriptor({...d,artifacts:[]}));});
test('log must belong to the authenticated attempt job list',()=>{const f=fixture(),s=f.capture();assert.throws(()=>capturePreflightLog(f.g,s,'999'),/observed attempt/);});
test('late movement during exact artifact byte read fails final collection',()=>{const f=fixture(),d=f.capture().descriptor;f.g.artifactBytes=()=>{f.state.run.run_attempt++;return f.state.bytes;};assert.throws(()=>collectPreflightProof(f.g,d),/attempt/);});
test('late movement during attempt job reread fails collection',()=>{const f=fixture(),d=f.capture().descriptor;let n=0;f.g.jobs=()=>++n===1?f.state.jobs:[];assert.throws(()=>collectPreflightProof(f.g,d),/jobs/);});
