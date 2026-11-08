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
