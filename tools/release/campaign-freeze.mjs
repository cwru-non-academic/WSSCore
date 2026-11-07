import {assertSnapshotSourcesMatchPlans} from './campaign-selection-artifact.mjs';
import {readFileSync} from 'node:fs';
import {resolveOrReuseTestSelection,verifyTestSelection} from '../ci/test-selection.mjs';
import {freeze} from './github.mjs';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {freezeWorkflows,freezeWorkflowPair} from '../ci/workflow-freeze.mjs';
import {canonical,requireThat} from './verify.mjs';
import {testSelectionPolicy,selectionRequest,assertPairedTestSelection} from './campaign-test-selection.mjs';

// testSelection must come from loadProducerTestSelection after durable upload.
// This entrypoint never resolves current settings or branch tips itself.
export function freezeCampaign(g,manifest,policy,candidate,{testSelection,captureDocument,verificationOnly=false}={}) {
  requireThat(manifest.channel===policy.channel,'Campaign policies use different channels');
  const required=testSelectionPolicy(manifest);
  requireThat(canonical(required)===canonical(testSelectionPolicy(policy)),'Campaign policies must share one reviewed test source policy');
  requireThat(!required||testSelection,'Load the authenticated durable test selection before paired freeze');
  // Selection is locked before either receiver snapshot or workflow freeze.
  const a=freeze(g,manifest,candidate,{verificationOnly,testSelection});
  const s=freezeStage(g,policy,candidate,{freeze:false,testSelection});
  const pair=manifest.channel==='main'?freezeWorkflowPair(g,a,s):{acceptance:freezeWorkflows(g,a,'acceptance'),stage:freezeWorkflows(g,s,'stage')};
  assertPairedTestSelection(pair.acceptance,pair.stage);
  if(captureDocument)assertSnapshotSourcesMatchPlans(captureDocument,pair);
  return pair;
}

// resolveSnapshots is injected by the authenticated controller. Access failures
// must throw, never become an empty snapshot. Existing locks never invoke it.
export function resolveCampaignTestSelection(g,m,c,path,resolveSnapshots) {
  const expected=selectionRequest(m,c);
  try {
    const selection=JSON.parse(readFileSync(path,'utf8'));
    return verifyTestSelection(selection,expected,selection.selection_id);
  } catch(error) {if(error.code!=='ENOENT')throw error;}
  requireThat(typeof resolveSnapshots==='function','An authenticated effective variable snapshot reader is required');
  return resolveOrReuseTestSelection(selectionRequest(m,c,resolveSnapshots(testSelectionPolicy(m).targets)),g,path);
}
