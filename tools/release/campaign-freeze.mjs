import {freeze} from './github.mjs';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {freezeWorkflows,freezeWorkflowPair} from '../ci/workflow-freeze.mjs';
import {canonical,requireThat} from './verify.mjs';
import {testSelectionPolicy,resolveCampaignTestSelection,assertPairedTestSelection} from './campaign-test-selection.mjs';

// resolveSnapshots is supplied by a controller with authenticated repository-var
// access. No permissions are expanded and no failed read means "unset".
export function freezeCampaign(g,manifest,policy,candidate,{selectionPath,resolveSnapshots,verificationOnly=false}={}) {
  requireThat(manifest.channel===policy.channel,'Campaign policies use different channels');
  const required=testSelectionPolicy(manifest);
  requireThat(canonical(required)===canonical(testSelectionPolicy(policy)),'Campaign policies must share one reviewed test source policy');
  let testSelection;
  if(required) {
    requireThat(typeof selectionPath==='string'&&selectionPath.length>0,'Campaign selection needs a durable create-only lock path');
    testSelection=resolveCampaignTestSelection(g,manifest,candidate,selectionPath,resolveSnapshots);
  }
  // Selection is locked before either receiver snapshot or workflow freeze.
  const a=freeze(g,manifest,candidate,{verificationOnly,testSelection});
  const s=freezeStage(g,policy,candidate,{freeze:false,testSelection});
  if(manifest.channel==='main')return freezeWorkflowPair(g,a,s);
  const pair={acceptance:freezeWorkflows(g,a,'acceptance'),stage:freezeWorkflows(g,s,'stage')};
  assertPairedTestSelection(pair.acceptance,pair.stage);
  return pair;
}
