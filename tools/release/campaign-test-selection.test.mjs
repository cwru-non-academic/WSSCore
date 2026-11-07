import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,mkdtempSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {CORE,digest,plan} from './verify.mjs';
import {freeze} from './github.mjs';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {CAMPAIGN_TEST_POLICY,selectionFields,testSelectionPolicy,resolveCampaignTestSelection,planTestSelection,targetTestEvidence,verifySelectedTestArtifact,selectedTestArtifactName} from './campaign-test-selection.mjs';
const sha=n=>String(n).padStart(40,'0');
function fixture() {
  const m=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  m.test_selection_policy={schema:CAMPAIGN_TEST_POLICY,targets:m.targets.map(t=>({id:t.id,repository:t.repository,default_test_repository:'cwru-non-academic/TestBundle',allowed_test_repositories:['cwru-non-academic/TestBundle']}))};
  const c={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const g={head:()=>sha(3),object:()=>({sha:sha(4)}),get:()=>({path:'.github/workflows/release-receipt.yml',state:'active'})};
  return {m,c,g,snapshots:Object.fromEntries(m.targets.map(t=>[t.id,{}]))};
}
test('campaign locks shared test branch once and retries without reading variables or refs',()=>{
  const f=fixture(),directory=mkdtempSync(join(tmpdir(),'campaign-selection-')),path=join(directory,'selection.json');
  try {
    let reads=0,heads=0;const g={head:()=>{heads++;return sha(9);}};
    const selection=resolveCampaignTestSelection(g,f.m,f.c,path,()=>{reads++;return f.snapshots;});
    assert.equal(reads,1);assert.equal(heads,1);
    assert.deepEqual(resolveCampaignTestSelection({},f.m,f.c,path,()=>assert.fail('retry read variables')),selection);
    assert.throws(()=>resolveCampaignTestSelection({},f.m,{...f.c,run_id:'99'},path,()=>f.snapshots),/campaign/);
    const p=freeze(f.g,f.m,f.c,{verificationOnly:true,testSelection:selection});plan(p);
    assert.equal(planTestSelection(p).selection_id,selection.selection_id);
    assert.equal(targetTestEvidence(p,'python-cli').test_commit,sha(9));
    for(const key of ['test_selection','test_selection_id']){const q=structuredClone(p);delete q[key];const {plan_id,...body}=q;q.plan_id=digest(body);assert.throws(()=>plan(q));}
    assert.throws(()=>freeze(f.g,f.m,f.c,{verificationOnly:true}),/Required frozen/);
    const legacy=structuredClone(f.m);delete legacy.test_selection_policy;
    assert.throws(()=>freeze(f.g,legacy,f.c,{verificationOnly:true,testSelection:selection}),/legacy/);
  } finally {rmSync(directory,{recursive:true,force:true});}
});
test('snapshot acquisition is explicit, complete and never interprets a failed read as unset',()=>{
  const f=fixture(),directory=mkdtempSync(join(tmpdir(),'campaign-selection-'));
  try {
    const path=join(directory,'selection.json');
    assert.throws(()=>resolveCampaignTestSelection(f.g,f.m,f.c,path),/authenticated/);
    assert.throws(()=>resolveCampaignTestSelection(f.g,f.m,f.c,path,()=>{throw Error('403 denied');}),/403/);
    assert.throws(()=>resolveCampaignTestSelection(f.g,f.m,f.c,path,()=>({})),/snapshots/);
  } finally {rmSync(directory,{recursive:true,force:true});}
});
test('new stage policies fail closed before receiver reads when selection is missing',()=>{
  const f=fixture(),policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  policy.test_selection_policy=f.m.test_selection_policy;
  assert.throws(()=>freezeStage({},policy,f.c),/Required frozen/);
  for(const mutate of [p=>p.schema='unknown',p=>p.targets.pop(),p=>p.targets[0].allowed_test_repositories=[],p=>p.targets[0].repository=p.targets[1].repository,p=>p.targets[0].default_test_repository='untrusted/repo']) {
    const m=structuredClone(f.m);mutate(m.test_selection_policy);assert.throws(()=>testSelectionPolicy(m));
  }
});
