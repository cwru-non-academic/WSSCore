import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,existsSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {dispatchPreparedSnapshotIntents} from './campaign-variable-snapshots.mjs';
import {createSnapshotRequest,SNAPSHOT_WORKFLOW,snapshotTitle} from './local-test-variable-snapshot.mjs';
import {CORE} from './verify.mjs';
function setup(){
 const temp=mkdtempSync(join(tmpdir(),'wss-once-')),c={commit:'1'.repeat(40),run_id:'10'};
 const r=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel:'dev',producer:{repository:CORE,...c,attempt:1},target:{id:'target',repository:'owner/repo'},source:{ref:'refs/heads/dev',commit:'2'.repeat(40),workflow:SNAPSHOT_WORKFLOW,workflow_blob:'3'.repeat(40),actor_id:'123'},nonce:'a'.repeat(64)});
 const d={schema:'wss-test-variable-intents/v1',requests:[r]},env={GITHUB_RUN_ATTEMPT:'1',GITHUB_RUN_ID:'10',RUNNER_TEMP:temp};
 const state={posts:0,fail:false,runs:[]};
 const writer={head:()=>r.source.commit,get:()=>({path:SNAPSHOT_WORKFLOW,state:'active'}),pages:()=>state.runs,command:()=>{assert.ok(existsSync(join(temp,'wss-snapshot-dispatch-10-a1.json')),'marker must exist before POST');state.posts++;if(state.fail)throw Error('uncertain');}};
 return {temp,c,r,d,env,state,writer};
}
for(const partial of [false,true])test(`fixed first-attempt marker blocks repeated invocation including alternate output (partial=${partial})`,()=>{
 const f=setup();try{
  f.state.fail=partial;
  if(partial)assert.throws(()=>dispatchPreparedSnapshotIntents(f.writer,f.d,f.c,f.env),/uncertain/);else dispatchPreparedSnapshotIntents(f.writer,f.d,f.c,f.env);
  assert.equal(f.state.posts,1);
  // No output-directory argument can change the marker identity.
  assert.throws(()=>dispatchPreparedSnapshotIntents(f.writer,f.d,f.c,{...f.env,OUTPUT_DIRECTORY:join(f.temp,'other')}),/EEXIST/);
  assert.equal(f.state.posts,1);
 }finally{rmSync(f.temp,{recursive:true,force:true});}
});
test('later attempts cannot redispatch even with a clean runner filesystem',()=>{const f=setup();try{assert.throws(()=>dispatchPreparedSnapshotIntents(f.writer,f.d,f.c,{...f.env,GITHUB_RUN_ATTEMPT:'2'}),/initial actual/);assert.equal(f.state.posts,0);}finally{rmSync(f.temp,{recursive:true,force:true});}});
