import test from 'node:test';
import assert from 'node:assert/strict';
import {CORE_JOBS} from './verify.mjs';
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
