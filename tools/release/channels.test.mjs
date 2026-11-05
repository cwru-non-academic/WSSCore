import test from 'node:test';
import assert from 'node:assert/strict';
import {channelContext,targetForChannel,releaseFingerprint} from './channels.mjs';
test('dev and main select distinct immutable policy and evidence namespaces',()=>{
  const dev=channelContext('dev'),main=channelContext('main');
  assert.equal(dev.kind,'beta');assert.equal(main.kind,'stable');
  for(const key of ['ref','manifestSchema','planSchema','receiptSchema','acceptanceSchema','targetPolicy','stagePolicy','stageWorkflow','acceptanceWorkflow','stageArtifact'])assert.notEqual(dev[key],main[key],key);
  const target={id:'example',branch:'dev',jobs:['required'],parents:{wrapper:'path'}};
  assert.deepEqual(targetForChannel(target,'main'),{...target,branch:'main'});assert.equal(target.branch,'dev');
});
test('fingerprints are channel-distinct and retries deterministic without choosing semantic versions',()=>{
  const plan='a'.repeat(64);assert.equal(releaseFingerprint('dev','12',plan),'dev-beta-12-aaaaaaaaaaaa');
  assert.equal(releaseFingerprint('main','12',plan),'main-stable-12-aaaaaaaaaaaa');
  assert.equal(releaseFingerprint('main','12',plan),releaseFingerprint('main','12',plan));
  for(const c of ['','stable','beta','refs/heads/main','../main'])assert.throws(()=>channelContext(c));
  assert.throws(()=>releaseFingerprint('main','0',plan));assert.throws(()=>releaseFingerprint('main','12','unknown'));
});
