import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {checkVariableReadiness,readinessTargets} from './test-variable-readiness.mjs';
const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url),'utf8'));
const workflow=readFileSync(new URL('../../.github/workflows/test-variable-readiness.yml',import.meta.url),'utf8').replace(/\r\n/g,'\n');
test('probe reads exactly nine repository owners without retaining variable values',()=>{
  const paths=[];
  const result=checkVariableReadiness({get(path){paths.push(path);return {total_count:2,variables:[{name:'WSS_USE_PIN_TESTS',value:'true'},{name:'PRIVATE_UNRELATED',value:'must-not-be-published'}]};}},policy);
  assert.equal(result.targets.length,9);
  assert.deepEqual(paths,policy.targets.map(t=>`repos/${t.repository}/actions/variables?per_page=30&page=1`));
  assert.ok(result.targets.every(t=>t.read==='verified'));
  assert.doesNotMatch(JSON.stringify(result),/PRIVATE_UNRELATED|must-not-be-published|WSS_USE_PIN_TESTS|true/);
});
test('successful absent repository settings still prove the existing read capability',()=>{
  assert.equal(checkVariableReadiness({get(){return {total_count:0,variables:[]};}},policy).targets.length,9);
});
test('a denied read fails closed and does not leak response values',()=>{
  for(const status of [401,403,404,500]){
    assert.throws(()=>checkVariableReadiness({get(){throw Error(`HTTP${status} hidden-response`);}},policy),error=>{
      assert.match(error.message,/Repository Variables read failed/);assert.doesNotMatch(error.message,/hidden-response/);return true;
    });
  }
});
test('incomplete or duplicate snapshots cannot establish readiness',()=>{
  for(const response of [{total_count:1,variables:[]},{total_count:2,variables:[{name:'OTHER',value:'x'},{name:'OTHER',value:'x'}]}]){
    assert.throws(()=>checkVariableReadiness({get(){return response;}},policy));
  }
});
test('probe cannot expand, omit, duplicate or redirect the reviewed scope',()=>{
  for(const change of [p=>p.targets.pop(),p=>p.targets.push(p.targets[0]),p=>p.targets.reverse(),p=>p.targets[0].repository='foreign/secret']){
    const copy=structuredClone(policy);change(copy);let called=false;
    assert.throws(()=>checkVariableReadiness({get(){called=true;}},copy),/exactly the nine/);assert.equal(called,false);
  }
  assert.equal(readinessTargets(policy).length,9);
});
test('replacement diagnostic never requests Variables permission or dispatches',()=>{
  assert.match(workflow,/not hosted readiness/);
  assert.match(workflow,/node --test/);
  assert.doesNotMatch(workflow,/create-github-app-token|WSS_GITHUB_APP_PRIVATE_KEY|INPUT_PERMISSION|permission-variables|gh api/);
  assert.match(workflow,/permissions:\n  contents: read/);
});
