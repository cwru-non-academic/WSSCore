import {test} from 'node:test';
import assert from 'node:assert/strict';
import {dispatchIntent,dispatchReuse} from './dispatch-proof.mjs';
test('every fanout refuses uncertain attempts and requires a durable current intent',()=>{
  const c={artifacts:[{expired:false,workflow_run:{id:1,head_sha:'a'.repeat(40)}}],run_id:'1',attempt:'1',source_sha:'a'.repeat(40),required:true};
  assert.equal(dispatchIntent(c),true);
  for(const change of [{attempt:'2'},{artifacts:[]},{artifacts:[...c.artifacts,...c.artifacts]},{source_sha:'b'.repeat(40)}])assert.throws(()=>dispatchIntent({...c,...change}));
});
test('recovery only reuses a unique exact successful application run',()=>{
  const expected={repository:'cwru-non-academic/APP',source_sha:'a'.repeat(40),title:'exact'};
  const run={id:1,repository:{full_name:expected.repository},head_repository:{full_name:expected.repository},head_sha:expected.source_sha,path:'.github/workflows/promote-integration.yml',event:'workflow_dispatch',head_branch:'dev',display_title:'exact',status:'completed',conclusion:'success'};
  assert.equal(dispatchReuse({runs:[run],run,expected}),true);
  for(const change of [{status:'in_progress'},{conclusion:'failure'},{head_sha:'b'.repeat(40)},{head_repository:{full_name:'fork/repo'}}])assert.throws(()=>dispatchReuse({runs:[run],run:{...run,...change},expected}));
  assert.throws(()=>dispatchReuse({runs:[run,run],run,expected}));
});
