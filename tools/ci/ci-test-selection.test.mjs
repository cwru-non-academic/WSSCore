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
