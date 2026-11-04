import {requireThat} from './verify.mjs';

// Shared by staged integration checks and acceptance reconciliation. A push and
// a manual run are both real committed-state CI; neither may hide the other.
export function findNormalRuns(g,target,head,mergedAt) {
  const after=Date.parse(mergedAt);
  requireThat(Number.isFinite(after),'Missing actual merge time for committed-state CI');
  const runs=g.pages(`repos/${target.repository}/actions/workflows/${target.workflow.split('/').at(-1)}/runs?branch=dev&head_sha=${head}`,'workflow_runs');
  const eligible=[];
  for(const run of runs) {
    if(!['push','workflow_dispatch'].includes(run.event)) continue;
    const created=Date.parse(run.created_at);
    requireThat(Number.isFinite(created),'Invalid committed-state CI creation time');
    if(created<after) continue;
    requireThat(run.repository?.full_name===target.repository && run.head_repository?.full_name===target.repository &&
      run.path===target.workflow && run.head_sha===head && run.head_branch==='dev' &&
      /^[1-9][0-9]*$/.test(String(run.id)) && Number.isSafeInteger(run.run_attempt) && run.run_attempt>=1,
    'Committed-state CI identity/source mismatch');
    eligible.push(run);
  }
  requireThat(eligible.length<=1,`Ambiguous committed-state CI for ${target.repository}; inspect recovery`);
  return eligible;
}
