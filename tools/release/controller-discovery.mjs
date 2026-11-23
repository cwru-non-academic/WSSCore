import {CORE,requireThat} from './verify.mjs';

export function createdSince(createdAt) {
  requireThat(typeof createdAt==='string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(createdAt) && Number.isFinite(Date.parse(createdAt)),'Missing canonical producer creation time');
  return `created=${encodeURIComponent(`>=${createdAt}`)}`;
}

export function campaignReceiptRuns(g,plan,target,since) {
  const title=`WSS acceptance ${target.id} ${plan.plan_id}`;
  const path=`repos/${target.repository}/actions/workflows/release-receipt.yml/runs?branch=${target.branch}`;
  return g.pages(`${path}&event=workflow_dispatch&${since}`,'workflow_runs').filter(run=>run.display_title===title);
}

// Scope the shared collector's receipt lookup without changing the helper
// distributed to receiver repos. All other evidence reads use the real client.
export function campaignReceiptReader(g,plan) {
  const since=createdSince(g.run(CORE,plan.candidate.run_id).created_at);
  const searches=new Map(plan.manifest.targets.map(target=>[
    `repos/${target.repository}/actions/workflows/release-receipt.yml/runs?branch=${target.branch}`,target
  ]));
  return Object.assign(Object.create(g),{
    pages(path,key) {
      const target=searches.get(path);
      return target&&key==='workflow_runs'?campaignReceiptRuns(g,plan,target,since):g.pages(path,key);
    }
  });
}

export function snapshotRunReader(g,workflow,since) {
  // Limit controller lookups without changing the receiver's snapshot module.
  const endpoint=`/actions/workflows/${workflow.split('/').at(-1)}/runs?`;
  return Object.assign(Object.create(g),{
    pages(path,key) {
      return g.pages(key==='workflow_runs'&&path.includes(endpoint)?`${path}&${since}`:path,key);
    }
  });
}

export function journalArtifacts(g,workflow,name,{exact=false,excludeRun=''}={}) {
  // A queued run can be created before the producer and still write its journal later.
  const query=exact?`?name=${encodeURIComponent(name)}`:'';
  const artifacts=g.pages(`repos/${CORE}/actions/artifacts${query}`,'artifacts').filter(a=>exact?a.name===name:a.name.startsWith(name));
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
