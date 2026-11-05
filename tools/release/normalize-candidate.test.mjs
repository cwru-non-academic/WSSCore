import {test} from 'node:test';
import assert from 'node:assert/strict';
import {normalizeCandidate} from './normalize-candidate.mjs';
const candidate={repository:'cwru-non-academic/WSSCore',commit:'a'.repeat(40),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'b'.repeat(64)};
test('all resolvers emit the same six-field canonical candidate without diagnostic metadata',()=>{
  assert.deepEqual(normalizeCandidate({...candidate,release_tag:''}),candidate);
  assert.deepEqual(normalizeCandidate({...candidate,run_id:1,artifact_id:2,release_tag:''}),candidate);
  assert.equal(Object.keys(normalizeCandidate(candidate)).length,6);
  assert.throws(()=>normalizeCandidate({...candidate,unreviewed:true}),/exactly six/);
  assert.throws(()=>normalizeCandidate({...candidate,artifact_id:'0'}));
});
