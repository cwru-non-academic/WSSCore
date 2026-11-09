import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {parse} from './vendor/yaml/dist/index.js';
const read=p=>readFileSync(new URL(p,import.meta.url),'utf8');
test('phase emitter has one integration owner and only wrapper consumers',()=>{
 const manifest=JSON.parse(read('./sync-tooling.json'));
 assert.deepEqual(manifest.files['.github/scripts/wss-ci/PhaseDiagnostics.ps1'],{origin:'integration',path:'.github/scripts/wss-ci/PhaseDiagnostics.ps1',repositories:['CSharp_WSS_Integration_Lib','Unity_WSS_Integration_Lib','Python_WSS_Integration_Lib','Matlab_WSS_Integration_Lib']});
});
test('Core registers portable pinned owner regression tests on every contract OS',()=>{
 const job=parse(read('../../.github/workflows/shared-ci.yml')).jobs.contracts;
 assert.deepEqual(job.strategy.matrix.os,['ubuntu-latest','windows-latest','macos-latest']);
 const step=name=>{const rows=job.steps.filter(s=>s.name===name);assert.equal(rows.length,1);assert.equal(rows[0].if,undefined);return rows[0];};
 assert(job.steps.some(s=>s.uses==='actions/setup-python@v5'&&s.with['python-version']==='3.12'&&!s.if));
 assert.equal(step('Test pinned common application sender contracts').run,'./.ci-origins/Python_WSS_Integration_Lib/.github/scripts/wss-ci/tests/ApplicationDispatch.Tests.ps1');
 assert.equal(step('Test pinned common application sender contracts').shell,'pwsh');
 for(const name of ['dispatch-proof.test.mjs','application-dispatch-exit.test.mjs','application-dispatch-diagnostics.test.mjs'])assert(step('Test pinned sender process and diagnostic outcomes').run.includes('/tests/'+name));
 assert.equal(step('Test pinned observational phase diagnostics').run,'python .ci-origins/Python_WSS_Integration_Lib/.github/scripts/wss-ci/tests/test_phase_diagnostics.py');
});
test('receiver CI explicitly tests each actual peer adapter from its existing immutable owner checkout',()=>{
 const job=parse(read('./shared/shared-ci.yml')).jobs.identical;
 assert.equal(job['runs-on'],'ubuntu-latest');
 const checkouts=job.steps.filter(s=>s.uses==='actions/checkout@v4');assert.equal(checkouts.length,3);
 const owner=checkouts.find(s=>s.with?.repository==='cwru-non-academic/Python_WSS_Integration_Lib');
 assert.equal(owner.with.path,'.wss-core/.ci-origins/Python_WSS_Integration_Lib');
 assert.equal(owner.with.ref,'${{ steps.lock.outputs.integration_commit }}');
 for(const [family,adapter] of [['CSharp','WssFanout.ps1'],['Unity','UnityPromotion.ps1']]){
  const rows=job.steps.filter(s=>s.name===`Test actual ${family} application sender adapter`);assert.equal(rows.length,1);const s=rows[0];
  assert.equal(s.if,"${{ github.event.repository.name == '"+family+"_WSS_Integration_Lib' }}");assert.equal(s.shell,'pwsh');
  assert.equal(s.run,`./.wss-core/.ci-origins/Python_WSS_Integration_Lib/.github/scripts/wss-ci/tests/ApplicationDispatch.Tests.ps1 -${family}SenderPath ./.github/scripts/${adapter}`);
 }
});
