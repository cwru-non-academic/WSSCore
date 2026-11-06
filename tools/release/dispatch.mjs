import {basename} from 'node:path';
import {channelContext} from './channels.mjs';
import {archive} from './github.mjs';
import {findNormalRuns} from './normal-ci.mjs';
import {bytesDigest,canonical,CORE,digest,plan,producer,requireThat} from './verify.mjs';
import {assertWorkflows,guardedWriter} from '../ci/workflow-freeze.mjs';

export const journalName = (p,run,attempt) => `wss-dispatch-intents-${p.plan_id}-${run}-a${attempt}`;
export function action(p,t,workflow,head,inputs,mergedAt) {
  requireThat(Number.isFinite(Date.parse(mergedAt)),'Missing actual merge time for dispatch intent');
  const body={plan_id:p.plan_id,target_id:t.id,repository:t.repository,workflow,head_sha:head,merged_at:mergedAt,ref:t.branch,inputs};
  return {...body,key:digest(body)};
}
export function journal(p,run,attempt,actions) {
  requireThat(/^[1-9][0-9]*$/.test(run) && Number.isSafeInteger(attempt) && attempt>=1,'Invalid journal run identity');
  requireThat(new Set(actions.map(a=>a.key)).size===actions.length,'Duplicate dispatch intent');
  return {schema:'wss-dispatch-intents/v1',plan_id:p.plan_id,run_id:run,attempt,actions};
}
export function validateJournal(p,j) {
  requireThat(j.schema==='wss-dispatch-intents/v1' && j.plan_id===p.plan_id,'Journal plan/schema mismatch');
  requireThat(canonical(journal(p,j.run_id,j.attempt,j.actions))===canonical(j),'Invalid journal envelope');
  for(const a of j.actions) {
    const t=p.manifest.targets.find(t=>t.id===a.target_id);
    requireThat(t && a.repository===t.repository && a.ref===t.branch && /^[a-f0-9]{40}$/.test(a.head_sha),'Unknown journal target/branch/source');
    requireThat(a.workflow===t.workflow || a.workflow==='.github/workflows/release-receipt.yml','Dispatch outside registered normal/receipt workflow');
    requireThat(a.workflow!==t.workflow || t.kind==='gitlink','Integration normal CI belongs only to the staged coordinator');
    const expected=a.workflow===t.workflow?{}:{producer_run_id:p.candidate.run_id,plan_id:p.plan_id};
    requireThat(canonical(a.inputs)===canonical(expected) && canonical(action(p,t,a.workflow,a.head_sha,expected,a.merged_at))===canonical(a),'Invalid journal action/input/key');
  }
  return j;
}
export function previousIntents(g,p) {
  const result=[];
  const runs=g.pages(`repos/${CORE}/actions/workflows/${channelContext(p.channel).acceptanceWorkflow.split('/').at(-1)}/runs?branch=main`,'workflow_runs');
  for(const r of runs) {
    const prefix=`wss-dispatch-intents-${p.plan_id}-${r.id}-a`;
    const artifacts=g.pages(`repos/${CORE}/actions/runs/${r.id}/artifacts`,'artifacts').filter(a=>a.name.startsWith(prefix));
    for(const artifact of artifacts) {
      requireThat(!artifact.expired,'Prior dispatch intent expired; inspect before recovery');
      const bytes=g.download(CORE,String(r.id),artifact.name,['intent.json'])['intent.json'];
      const j=validateJournal(p,JSON.parse(bytes));
      requireThat(j.run_id===String(r.id) && artifact.name===journalName(p,j.run_id,j.attempt),'Intent artifact identity mismatch');
      result.push(...j.actions);
    }
  }
  return result;
}
export function prepare(g,p,run,attempt) {
  plan(p);
  assertWorkflows(g,p,'acceptance',{live:false});
  const c=p.candidate,r=g.run(CORE,c.run_id),zip=archive(g,c);
  producer(p,{run:r,current_head:g.head(CORE,p.channel),jobs:g.jobs(CORE,r),artifact:zip.artifact,archive_sha256:bytesDigest(zip.files[c.archive])});
  const prior=previousIntents(g,p),actions=[];
  function request(t,workflow,head,inputs,mergedAt) {
    const a=action(p,t,workflow,head,inputs,mergedAt);
    requireThat(!prior.some(x=>x.key===a.key),`Uncertain prior dispatch for ${t.id}; no automatic retry without inspected recovery`);
    actions.push(a);
  }
  for(const t of p.manifest.targets) {
    assertWorkflows(g,p,'acceptance',{ids:[t.id]});
    const head=g.head(t.repository,t.branch);
    if(head===p.target_snapshots[t.id].head_sha) continue;
    const prs=g.pages(`repos/${t.repository}/commits/${head}/pulls`).filter(pr=>pr.merged_at && pr.merge_commit_sha===head && pr.base?.ref===t.branch && pr.base?.repo?.full_name===t.repository && pr.head?.repo?.full_name===t.repository);
    requireThat(prs.length===1 && prs[0].user?.type==='Bot',`Current ${t.id} is not a unique bot dependency merge`);
    const normals=findNormalRuns(g,t,head,prs[0].merged_at);
    if(normals.length===0) {
      // The stage coordinator is the sole writer for integration normal CI.
      if(t.kind==='gitlink') request(t,t.workflow,head,{},prs[0].merged_at);
      continue;
    }
    requireThat(normals[0].status!=='completed' || normals[0].conclusion==='success',`Postmerge CI failed for ${t.id}; inspect recovery`);
    if(normals[0].status!=='completed') continue;
    const title=`WSS acceptance ${t.id} ${p.plan_id}`;
    const receipts=g.pages(`repos/${t.repository}/actions/workflows/release-receipt.yml/runs?branch=${t.branch}&event=workflow_dispatch`,'workflow_runs').filter(r=>r.display_title===title);
    requireThat(receipts.length<=1,`Ambiguous receipt runs for ${t.id}`);
    if(receipts.length) {requireThat(receipts[0].status!=='completed' || receipts[0].conclusion==='success',`Receipt failed for ${t.id}; inspect recovery`);continue;}
    request(t,'.github/workflows/release-receipt.yml',head,{producer_run_id:c.run_id,plan_id:p.plan_id},prs[0].merged_at);
  }
  return journal(p,run,attempt,actions);
}
export function execute(g,reader,p,j) {
  plan(p);validateJournal(p,j);
  assertWorkflows(reader,p,'acceptance',{live:false});
  // A successful upload is required BEFORE the first external mutation. A crash,
  // failed HTTP response, or missing run after this point remains a durable intent.
  const saved=reader.download(CORE,j.run_id,journalName(p,j.run_id,j.attempt),['intent.json'])['intent.json'];
  requireThat(canonical(JSON.parse(saved))===canonical(j),'Durable dispatch journal missing or different');
  requireThat(reader.head(CORE,p.channel)===p.candidate.commit,'Core moved after intent preparation');
  for(const a of j.actions) requireThat(reader.head(a.repository,a.ref)===a.head_sha,'Receiver moved after intent preparation');
  for(const a of j.actions) {
    const isReceipt=a.workflow==='.github/workflows/release-receipt.yml';
    const runs=isReceipt?reader.pages(`repos/${a.repository}/actions/workflows/${basename(a.workflow)}/runs?branch=${a.ref}&event=workflow_dispatch&head_sha=${a.head_sha}`,'workflow_runs'):findNormalRuns(reader,p.manifest.targets.find(t=>t.id===a.target_id),a.head_sha,a.merged_at);
    const existing=isReceipt?runs.filter(r=>r.display_title===`WSS acceptance ${a.target_id} ${p.plan_id}`):runs;
    if(existing.length) {
      requireThat(existing.length===1,'Ambiguous concurrent proof dispatch');
      const latest=[...existing].sort((a,b)=>b.id-a.id)[0];
      requireThat(latest.status!=='completed' || latest.conclusion==='success','Concurrent proof failed; inspect before recovery');
      continue;
    }
    requireThat(reader.head(CORE,p.channel)===p.candidate.commit && reader.head(a.repository,a.ref)===a.head_sha,'Branch moved before proof dispatch');
    const args=['api','--method','POST',`repos/${a.repository}/actions/workflows/${basename(a.workflow)}/dispatches`,'-f',`ref=${a.ref}`];
    for(const [key,value] of Object.entries(a.inputs)) args.push('-f',`inputs[${key}]=${value}`);
    guardedWriter(g,reader,p,'acceptance',{ids:[a.target_id],purpose:'proof',...(p.channel==='main'?{executionSource:a.head_sha}:{})}).command(args);
  }
}
