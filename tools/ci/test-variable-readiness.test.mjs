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
test('hosted probe uses existing App only, no PR secrets or write grants',()=>{
  assert.match(workflow,/workflow_dispatch:/);assert.doesNotMatch(workflow,/pull_request/);
  assert.match(workflow,/branches: \[dev, main\]/);
  assert.match(workflow,/github\.ref == 'refs\/heads\/dev'.*github\.ref == 'refs\/heads\/main'/);
  assert.deepEqual([...workflow.matchAll(/INPUT_PERMISSION-([A-Z-]+):\s*(\w+)/g)].map(m=>m.slice(1)),[['VARIABLES','read']]);
  assert.match(workflow,/actions\/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349/);
  assert.doesNotMatch(workflow,/permission-[a-z]+:/);
  assert.match(workflow,/permissions:\n  contents: read/);
  assert.match(workflow,/persist-credentials: false/);
  assert.match(workflow,/app-id: \$\{\{ vars.WSS_GITHUB_APP_ID \}\}/);
  assert.match(workflow,/repositories: \$\{\{ steps.targets.outputs.repositories \}\}/);
  assert.doesNotMatch(workflow,/gh api|--method|upload-artifact|overwrite:|write|continue-on-error|skip-token-revoke: true/);
});
