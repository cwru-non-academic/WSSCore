import {CORE,requireThat} from './verify.mjs';

export function createdSince(createdAt) {
  requireThat(typeof createdAt==='string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(createdAt) && Number.isFinite(Date.parse(createdAt)),'Missing canonical producer creation time');
  return `created=${encodeURIComponent(`>=${createdAt}`)}`;
}

export function journalArtifacts(g,workflow,prefix,excludeRun='') {
  // A queued run can be created before the producer and still write its journal later.
  const artifacts=g.pages(`repos/${CORE}/actions/artifacts`,'artifacts').filter(a=>a.name.startsWith(prefix));
  const result=[];
  for(const artifact of artifacts) {
    const runId=String(artifact.workflow_run?.id ?? '');
    requireThat(/^[1-9][0-9]*$/.test(runId),'Journal artifact is missing its workflow run');
    if(runId===excludeRun)continue;
    const run=g.run(CORE,runId);
    requireThat(String(run.id)===runId && run.path===workflow && run.head_branch==='main' && run.repository?.full_name===CORE &&
      run.head_repository?.full_name===CORE && run.head_sha===artifact.workflow_run.head_sha,'Journal artifact came from a foreign workflow source');
    result.push({artifact,runId});
  }
  return result;
}
