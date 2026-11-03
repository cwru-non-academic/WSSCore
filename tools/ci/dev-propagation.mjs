import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {GitHub,archive} from '../release/github.mjs';
import {CORE,CORE_JOBS,candidate,canonical,digest,jobs,requireThat} from '../release/verify.mjs';

const POLICY='tools/ci/dev-propagation.json',WORKFLOW='.github/workflows/dev-propagation.yml';
const RECEIVER='.github/dev-propagation.json';
const same=(a,b)=>canonical(a)===canonical(b);
function json(g,repo,ref,path) {
  const f=g.get(`repos/${repo}/contents/${path}?ref=${ref}`);
  requireThat(f.type==='file' && f.encoding==='base64','Expected immutable JSON file');
  return JSON.parse(Buffer.from(f.content,'base64').toString('utf8'));
}
function registered(g,repo,file) {
  const w=g.get(`repos/${repo}/actions/workflows/${file}`);
  requireThat(w.path===`.github/workflows/${file}` && w.state==='active',`Register ${repo}/${file} on its default branch first`);
}
export function policy(m) {
  requireThat(m.schema==='wss-dev-stage/v1' && m.channel==='dev','Unsupported dev staging policy');
  requireThat(same(m.libraries.map(t=>t.id).sort(),['csharp','matlab','python','unity']),'All four integrations are required');
  requireThat(new Set(m.libraries.map(t=>t.repository)).size===4,'Duplicate integration repository');
  for(const t of m.libraries) {
    requireThat(/^cwru-non-academic\/[A-Za-z0-9_]+$/.test(t.repository) && t.jobs.length>0 && t.applications.length>0,'Invalid integration policy');
    for(const f of [t.normal,t.fanout]) requireThat(/^[a-z-]+\.yml$/.test(f),'Invalid registered workflow');
    for(const app of t.applications) requireThat(/^cwru-non-academic\/[A-Za-z0-9_]+$/.test(app),'Invalid application repository');
  }
  return m;
}
function matlabReady(g,repo) {
  for(const branch of ['main','dev']) {
    const config=json(g,repo,branch,'.github/matlab-ci.json');
    requireThat(config.baseline_ready===true && config.candidate_preflight_ready===true,`MATLAB readiness is disabled on ${repo}/${branch}`);
  }
  // The destination workflow enforces its own activation variable. A Core
  // read-only token cannot establish another repository's variable visibility.
}
// Run before the first library write token. Missing receiver hooks cannot silently
// turn the requested integration barrier into independent per-library fanout.
export function freezeStage(g,m,c) {
  policy(m);candidate(c);
  requireThat(g.head(CORE)===c.commit,'Core dev moved before integration handoff');
  registered(g,CORE,'dev-propagation.yml');
  registered(g,CORE,'propagate-matlab.yml');
  const receivers={};
  for(const t of m.libraries) {
    const heads={main:g.head(t.repository,'main'),dev:g.head(t.repository,'dev')};
    const trusted={};
    for(const branch of ['main','dev']) {
      const cap=json(g,t.repository,heads[branch],RECEIVER);
      requireThat(cap.schema==='wss-dev-stage-receiver/v1' && cap.core_workflow===WORKFLOW && cap.fanout_workflow===t.fanout && cap.staged_applications===true && same([...cap.applications].sort(),[...t.applications].sort()),`Deploy staged application guard on ${t.repository}/${branch}`);
      trusted[branch]=Object.fromEntries([RECEIVER,'.github/scripts',`.github/workflows/${t.normal}`,`.github/workflows/${t.fanout}`].map(p=>[p,g.object(t.repository,heads[branch],p).sha]));
    }
    for(const f of [t.normal,t.fanout,'promote-wss.yml','wss-preflight.yml']) registered(g,t.repository,f);
    for(const app of t.applications) {
      for(const f of ['promote-integration.yml','wss-preflight.yml']) registered(g,app,f);
    }
    if(t.id==='matlab') {
      const pin=t.native_evidence_guard;
      requireThat(pin?.path==='.github/scripts/Complete-MatlabPreflight.ps1' && /^[0-9a-f]{40}$/.test(pin.sha),'Pin the reviewed MATLAB native evidence validator before handoff');
      for(const branch of ['main','dev']) requireThat(g.object(t.repository,heads[branch],pin.path).sha===pin.sha,`MATLAB native evidence validator is not the reviewed version on ${branch}`);
      const fixture=t.promotion_contract_test;
      requireThat(fixture?.path==='.github/scripts/tests/Test-MatlabPromotion.ps1' && /^[0-9a-f]{40}$/.test(fixture.sha),'Pin the candidate-aware MATLAB promotion contract test before handoff');
      for(const branch of ['main','dev']) requireThat(g.object(t.repository,heads[branch],fixture.path).sha===fixture.sha,`MATLAB promotion contract test is not the reviewed version on ${branch}`);
      matlabReady(g,t.repository);
      for(const app of t.applications) matlabReady(g,app);
    }
    receivers[t.id]={heads,trusted};
  }
  const body={schema:'wss-dev-propagation-plan/v1',candidate:c,policy:m,policy_sha256:digest(m),receivers};
  requireThat(g.head(CORE)===c.commit,'Core dev moved while freezing receiver tooling');
  return {...body,plan_id:digest(body)};
}
export function validatePlan(p) {
  candidate(p.candidate);policy(p.policy);
  const {plan_id,...body}=p;
  requireThat(p.schema==='wss-dev-propagation-plan/v1' && digest(body)===plan_id && digest(p.policy)===p.policy_sha256,'Dev stage plan identity mismatch');
  requireThat(same(Object.keys(p.receivers).sort(),p.policy.libraries.map(t=>t.id).sort()),'Missing integration snapshots');
  return p;
}
export function loadStage(g,id) {
  requireThat(/^[1-9][0-9]*$/.test(id),'Invalid producer run ID');
  const p=validatePlan(JSON.parse(g.download(CORE,id,'wss-dev-propagation-plan',['plan.json'])['plan.json']));
  const c=p.candidate,r=g.run(CORE,id);
  requireThat(c.run_id===id && r.repository?.full_name===CORE && r.head_repository?.full_name===CORE && r.path==='.github/workflows/artifacts.yml' && r.event==='push' && r.head_branch==='dev' && r.head_sha===c.commit && r.status==='completed' && r.conclusion==='success' && g.head(CORE)===c.commit,'Stale or unsuccessful canonical dev producer');
  jobs(g.jobs(CORE,r),CORE_JOBS);
  archive(g,c);
  requireThat(same(json(g,CORE,c.commit,POLICY),p.policy),'Plan policy is not the reviewed producer policy');
  return p;
}
function trusted(g,p,t,head) {
  for(const branch of ['main','dev']) {
    const ref=branch==='main'?g.head(t.repository,'main'):head;
    for(const [path,sha] of Object.entries(p.receivers[t.id].trusted[branch])) requireThat(g.object(t.repository,ref,path).sha===sha,`Staged receiver tooling changed: ${t.id}/${branch}/${path}`);
  }
}
function mergedLibrary(g,p,t) {
  const head=g.head(t.repository),record=json(g,t.repository,head,t.record);
  if(!record.candidate || !Object.keys(p.candidate).every(k=>String(record.candidate[k])===p.candidate[k])) return null;
  const matches=g.pages(`repos/${t.repository}/commits/${head}/pulls`).filter(pr=>pr.merged_at && pr.merge_commit_sha===head && pr.base?.ref==='dev');
  requireThat(matches.length===1,`Require unique dependency merge for ${t.id}; no no-op or preparation-only acceptance`);
  const pr=g.get(`repos/${t.repository}/pulls/${matches[0].number}`);
  requireThat(pr.merged===true && pr.state==='closed' && pr.merge_commit_sha===head && pr.user?.type==='Bot' && pr.merged_by?.type==='User' && pr.head?.repo?.full_name===t.repository && pr.base?.repo?.full_name===t.repository && pr.base.ref==='dev',`Require human-reviewed bot dependency merge for ${t.id}`);
  trusted(g,p,t,head);
  return {repository:t.repository,head_sha:head,pr_number:String(pr.number),merged_at:pr.merged_at};
}
function normalRuns(g,t,accepted) {
  return g.pages(`repos/${t.repository}/actions/workflows/${t.normal}/runs?branch=dev&head_sha=${accepted.head_sha}`,'workflow_runs').filter(r=>['push','workflow_dispatch'].includes(r.event) && Date.parse(r.created_at)>=Date.parse(accepted.merged_at));
}
const title=p=>`Resume application promotion / wss-${p.candidate.run_id}`;
const journalName=(p,run,attempt)=>`wss-dev-stage-intents-${p.candidate.run_id}-${run}-a${attempt}`;
function action(p,t,accepted,phase,inputs) {
  const body={candidate:p.candidate,plan_id:p.plan_id,target:t.id,repository:t.repository,head_sha:accepted.head_sha,merged_at:accepted.merged_at,phase,
    workflow:phase==='normal-ci'?t.normal:t.fanout,ref:phase==='normal-ci'?'dev':'main',source_sha:phase==='normal-ci'?accepted.head_sha:p.receivers[t.id].heads.main,inputs};
  return {...body,key:digest({...body,inputs:phase==='applications'?{producer_run_id:p.candidate.run_id}:inputs})};
}
function existing(g,p,a) {
  const runs=g.pages(`repos/${a.repository}/actions/workflows/${a.workflow}/runs?branch=${a.ref}&event=workflow_dispatch&head_sha=${a.source_sha}`,'workflow_runs');
  return a.phase==='applications'?runs.filter(r=>r.display_title===title(p)):runs.filter(r=>Date.parse(r.created_at)>=Date.parse(a.merged_at));
}
function priorIntents(g,p,excludeRun='') {
  const result=[];
  for(const r of g.pages(`repos/${CORE}/actions/workflows/dev-propagation.yml/runs?branch=main`,'workflow_runs')) {
    if(String(r.id)===excludeRun) continue;
    for(const a of g.pages(`repos/${CORE}/actions/runs/${r.id}/artifacts`,'artifacts').filter(a=>a.name.startsWith(`wss-dev-stage-intents-${p.candidate.run_id}-`))) {
      requireThat(!a.expired,'Expired dev stage intent; inspect before recovery');
      const j=JSON.parse(g.download(CORE,String(r.id),a.name,['intent.json'])['intent.json']);
      requireThat(j.plan_id===p.plan_id && a.name===journalName(p,j.run_id,j.attempt),'Foreign or inconsistent dev stage journal');
      result.push(...j.actions);
    }
  }
  return result;
}
export function prepareStage(g,p,run,attempt,excludeCurrent=false) {
  validatePlan(p);
  requireThat(/^[1-9][0-9]*$/.test(run) && Number.isSafeInteger(attempt) && attempt>=1,'Invalid stage emitter');
  const integrations={},pending=[],actions=[];
  for(const t of p.policy.libraries) {
    const accepted=mergedLibrary(g,p,t);
    if(!accepted) pending.push(t.id); else integrations[t.id]=accepted;
  }
  const base={schema:'wss-dev-stage-intents/v1',plan_id:p.plan_id,candidate:p.candidate,run_id:run,attempt};
  // No application work or CI write token until every integration is merged.
  if(pending.length) return {...base,state:'waiting-for-integrations',pending,actions};
  let normalsReady=true;
  for(const t of p.policy.libraries) {
    const accepted=integrations[t.id],runs=normalRuns(g,t,accepted);
    requireThat(runs.length<=1,`Ambiguous committed-state CI for ${t.id}; inspect recovery`);
    if(!runs.length) {actions.push(action(p,t,accepted,'normal-ci',{}));normalsReady=false;continue;}
    const r=runs[0];
    requireThat(r.repository?.full_name===t.repository && r.head_repository?.full_name===t.repository && r.path===`.github/workflows/${t.normal}` && r.head_sha===accepted.head_sha,'Normal CI source mismatch');
    requireThat(r.status!=='completed' || r.conclusion==='success',`Failed integration CI for ${t.id}; no blind retry`);
    if(r.status!=='completed') {normalsReady=false;continue;}
    jobs(g.jobs(t.repository,r),t.jobs);
    accepted.normal_ci={run_id:String(r.id),attempt:r.run_attempt};
  }
  const barrier=normalsReady?{schema:'wss-integration-barrier/v1',plan_id:p.plan_id,candidate:p.candidate,integrations,emitter:{run_id:run,attempt,workflow:WORKFLOW}}:null;
  if(barrier) for(const t of p.policy.libraries) actions.push(action(p,t,integrations[t.id],'applications',{producer_run_id:p.candidate.run_id,barrier_run_id:run,barrier_attempt:String(attempt)}));
  const prior=priorIntents(g,p,excludeCurrent?run:''),needed=[];
  for(const a of actions) {
    const runs=existing(g,p,a);
    requireThat(runs.length<=1,`Ambiguous staged dispatch for ${a.target}`);
    if(runs.length) {requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success',`Staged dispatch failed for ${a.target}; inspect before recovery`);continue;}
    requireThat(!prior.some(x=>x.key===a.key),`Uncertain prior dispatch for ${a.target}; no automatic duplicate`);
    needed.push(a);
  }
  return {...base,state:barrier?'applications-authorized':'waiting-for-committed-ci',pending:[],actions:needed,...(barrier?{barrier}:{})};
}
export function executeStage(g,reader,p,j) {
  validatePlan(p);
  const saved=JSON.parse(reader.download(CORE,j.run_id,journalName(p,j.run_id,j.attempt),['intent.json'])['intent.json']);
  requireThat(same(saved,j),'Durable dev stage journal differs');
  const live=prepareStage(reader,p,j.run_id,j.attempt,true);
  requireThat(live.plan_id===j.plan_id,'Dev plan changed');
  if(j.barrier) requireThat(same(j.barrier,live.barrier),'Integration barrier changed after journaling');
  for(const a of j.actions) {
    const t=p.policy.libraries.find(t=>t.id===a.target);
    const inputs=a.phase==='normal-ci'?{}:{producer_run_id:p.candidate.run_id,barrier_run_id:j.run_id,barrier_attempt:String(j.attempt)};
    requireThat(t && same(a.inputs,inputs) && same(a,action(p,t,{head_sha:a.head_sha,merged_at:a.merged_at},a.phase,inputs)),'Invalid staged action');
    requireThat(a.phase==='normal-ci' || a.phase==='applications' && j.barrier && Object.keys(j.barrier.integrations).length===4,'Applications require the complete integration barrier');
    requireThat(reader.head(CORE)===p.candidate.commit,'Core moved before stage dispatch');
    for(const lib of p.policy.libraries) requireThat(reader.head(lib.repository)=== (j.barrier?.integrations[lib.id]?.head_sha || mergedLibrary(reader,p,lib)?.head_sha),'Integration moved before stage dispatch');
    requireThat(reader.head(a.repository,a.ref)===a.source_sha,'Dispatch source moved');
    trusted(reader,p,t,a.head_sha);
    const runs=existing(reader,p,a);
    requireThat(runs.length<=1,'Ambiguous concurrent stage dispatch');
    if(runs.length) {requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success','Concurrent stage failed');continue;}
    const args=['api','--method','POST',`repos/${a.repository}/actions/workflows/${a.workflow}/dispatches`,'-f',`ref=${a.ref}`];
    for(const [k,v] of Object.entries(a.inputs)) args.push('-f',`inputs[${k}]=${v}`);
    g.command(args);
  }
}
export function verifyBarrier(g,producerId,runId,attempt,repository) {
  const p=loadStage(g,producerId);
  requireThat(p.policy.libraries.some(t=>t.repository===repository),'Unknown application fanout caller');
  requireThat(/^[1-9][0-9]*$/.test(runId) && Number.isSafeInteger(attempt) && attempt>=1,'Invalid barrier emitter identity');
  const r=g.run(CORE,runId);
  requireThat(r.repository?.full_name===CORE && r.head_repository?.full_name===CORE && r.path===WORKFLOW && r.head_branch==='main' && r.head_sha===g.head(CORE,'main') && r.run_attempt===attempt && ['workflow_run','schedule','workflow_dispatch'].includes(r.event),'Barrier did not originate from trusted Core main');
  const actual=g.jobs(CORE,r).filter(j=>j.name==='Prepare verified dev propagation stage');
  requireThat(actual.length===1 && actual[0].status==='completed' && actual[0].conclusion==='success','Barrier preparation job did not succeed');
  const name=`wss-integration-barrier-${producerId}-a${attempt}`,a=g.artifact(CORE,runId,name);
  requireThat(a.workflow_run.head_sha===r.head_sha,'Barrier artifact source mismatch');
  const barrier=JSON.parse(g.download(CORE,runId,name,['barrier.json'])['barrier.json']);
  const live=prepareStage(g,p,runId,attempt,true);
  requireThat(barrier.schema==='wss-integration-barrier/v1' && same(barrier,live.barrier),'Current integrations no longer match the authorized barrier');
  return barrier;
}
async function main() {
  const [command,...args]=process.argv.slice(2),g=new GitHub();
  if(command==='freeze') {
    const [candidateFile,directory]=args,c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(new URL('./dev-propagation.json',import.meta.url)));
    requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/dev' && process.env.GITHUB_EVENT_NAME==='push' && c.run_id===process.env.GITHUB_RUN_ID && c.commit===process.env.GITHUB_SHA,'Freeze only the canonical dev push candidate');
    archive(g,c);
    const p=freezeStage(g,m,c);mkdirSync(directory,{recursive:true});writeFileSync(join(directory,'plan.json'),canonical(p)+'\n');
  } else if(command==='prepare') {
    let [id,directory]=args;
    if(!id) {
      const event=JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH));
      if(event.workflow_run) id=String(event.workflow_run.id);
      else {const runs=g.pages(`repos/${CORE}/actions/workflows/artifacts.yml/runs?branch=dev&event=push&head_sha=${g.head(CORE)}`,'workflow_runs');requireThat(runs.length===1,'Require one current canonical producer');id=String(runs[0].id);}
    }
    const p=loadStage(g,id),j=prepareStage(g,p,process.env.GITHUB_RUN_ID,Number(process.env.GITHUB_RUN_ATTEMPT));
    mkdirSync(directory,{recursive:true});writeFileSync(join(directory,'intent.json'),canonical(j)+'\n');
    if(j.barrier) writeFileSync(join(directory,'barrier.json'),canonical(j.barrier)+'\n');
    appendFileSync(process.env.GITHUB_OUTPUT,`producer_run_id=${id}\nartifact_name=${journalName(p,j.run_id,j.attempt)}\nactions=${j.actions.length}\nbarrier=${Boolean(j.barrier)}\n`);
    appendFileSync(process.env.GITHUB_STEP_SUMMARY,`Dev propagation: ${j.state}; pending integrations: ${j.pending.join(', ') || 'none'}. No release publication.\n`);
  } else if(command==='dispatch') {
    requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main' && process.env.GITHUB_WORKFLOW_REF===`${CORE}/${WORKFLOW}@refs/heads/main`,'Dispatch only from registered trusted Core main workflow');
    const j=JSON.parse(readFileSync(args[0]));
    requireThat(j.run_id===process.env.GITHUB_RUN_ID && j.attempt===Number(process.env.GITHUB_RUN_ATTEMPT),'Journal must belong to this exact workflow attempt');
    requireThat(process.env.WSS_JOURNAL_READ_TOKEN,'Missing Core read-only journal token');
    const reader=new GitHub(process.env.WSS_JOURNAL_READ_TOKEN),p=loadStage(reader,j.candidate.run_id);
    executeStage(g,reader,p,j);
  } else if(command==='verify-barrier') {
    requireThat(args.length===3,'verify-barrier requires producer run ID, barrier run ID and attempt');
    console.log(canonical(verifyBarrier(g,args[0],args[1],Number(args[2]),process.env.GITHUB_REPOSITORY)));
  } else if(command==='matlab') {
    const event=JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH)),id=String(event.workflow_run?.id),p=loadStage(g,id),t=p.policy.libraries.find(t=>t.id==='matlab');
    matlabReady(g,t.repository);for(const app of t.applications) matlabReady(g,app);
    registered(g,t.repository,'promote-wss.yml');
    const runs=g.pages(`repos/${t.repository}/actions/workflows/promote-wss.yml/runs?event=workflow_dispatch`,'workflow_runs').filter(r=>r.display_title===`Promote universal WSS / wss-${id}-${p.candidate.artifact_id}`);
    requireThat(runs.length<=1,'Ambiguous MATLAB promotion');
    if(runs.length) requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success','Prior MATLAB promotion failed; inspect recovery');
    mkdirSync(args[0],{recursive:true});writeFileSync(join(args[0],'dispatch.json'),JSON.stringify({ref:'dev',inputs:{candidate_json:canonical(p.candidate)}}));
    appendFileSync(process.env.GITHUB_OUTPUT,`dispatch_needed=${runs.length===0}\n`);
  } else throw new Error('Expected freeze, prepare, dispatch, verify-barrier or matlab');
}
if(process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) main().catch(e=>{console.error(e.message);process.exitCode=1;});
