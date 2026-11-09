import test from 'node:test';
import assert from 'node:assert/strict';
import {CORE_JOBS} from './verify.mjs';
import {spawnSync} from 'node:child_process';
import {mkdtempSync,writeFileSync,symlinkSync,rmSync} from 'node:fs';
import {dirname,join,relative} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {requiredProducerJobs,assertProducerJobs} from './producer-jobs.mjs';
const complete=CORE_JOBS.map(name=>({name,status:'completed',conclusion:'success'}));
test('current producer requires the exact Core policy, including every BLE aggregate',()=>{
  assert.deepEqual(requiredProducerJobs('macos-ble-v1'),CORE_JOBS);
  assertProducerJobs(complete,'macos-ble-v1');
  for(const name of CORE_JOBS) {
    assert.throws(()=>assertProducerJobs(complete.filter(job=>job.name!==name),'macos-ble-v1'));
    for(const patch of [{status:'in_progress'},{conclusion:'skipped'},{conclusion:'failure'}])
      assert.throws(()=>assertProducerJobs(complete.map(job=>job.name===name?{...job,...patch}:job),'macos-ble-v1'));
  }
  assert.throws(()=>assertProducerJobs([...complete,complete[0]],'macos-ble-v1'));
});
test('historical inventory policy is explicit and cannot satisfy current propagation',()=>{
  const old=complete.filter(job=>requiredProducerJobs('legacy-v1').includes(job.name));
  assert.equal(old.length,11);
  assertProducerJobs(old,'legacy-v1');
  assert.throws(()=>assertProducerJobs(old,'macos-ble-v1'));
  assert.throws(()=>requiredProducerJobs('unknown'));
});

// Exercise the actual consumer CLI, not just the shared helper in isolation.
test('producer CLI validates canonical, relative and Unicode alias paths while eval imports stay inert',()=>{
  const root=mkdtempSync(join(tmpdir(),'wss producer entrypoint '));
  try {
    const source=fileURLToPath(new URL('./producer-jobs.mjs',import.meta.url)),alias=join(root,'release alias ü 漢字');
    symlinkSync(dirname(source),alias,process.platform==='win32'?'junction':'dir');
    const context=join(root,'context with spaces.json');
    const invoke=(args,cwd)=>{const result=spawnSync(process.execPath,args,{cwd,encoding:'utf8'});if(result.error)throw result.error;assert.equal(result.signal,null);return result;};
    const aliased=join(alias,'producer-jobs.mjs');
    const entries=[[source,root],[relative(dirname(source),source),dirname(source)],[aliased,root],[relative(root,aliased),root]];
    const invalid=[
      ['missing job',complete.slice(1)],
      ['duplicate job',[...complete,complete[0]]],
      ...[{status:'in_progress'},{conclusion:'skipped'},{conclusion:'failure'}].map(patch=>[JSON.stringify(patch),complete.map((job,index)=>index===0?{...job,...patch}:job)])
    ];
    for(const [entry,cwd] of entries) {
      writeFileSync(context,JSON.stringify({jobs:complete,contract:'macos-ble-v1'}));
      const accepted=invoke([entry,context],cwd);assert.equal(accepted.status,0,accepted.stderr);
      for(const [name,jobs] of invalid){
        writeFileSync(context,JSON.stringify({jobs,contract:'macos-ble-v1'}));
        const rejected=invoke([entry,context],cwd);assert.equal(rejected.status,1,name);assert.match(rejected.stderr,/Required unique successful job missing/,name);
      }
      const usage=invoke([entry],cwd);assert.equal(usage.status,1);assert.match(usage.stderr,/Usage: producer-jobs/);
      for(const flag of ['--eval','-e']) {
        const imported=invoke(['--input-type=module',flag,'await import('+JSON.stringify(pathToFileURL(source).href)+')','--',entry],cwd);
        assert.equal(imported.status,0,imported.stderr);assert.equal(imported.stdout,'');assert.equal(imported.stderr,'');
      }
    }
  } finally {rmSync(root,{recursive:true,force:true});}
});
