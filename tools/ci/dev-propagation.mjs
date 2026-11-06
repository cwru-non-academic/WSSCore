import {isEntrypoint} from '../release/entrypoint.mjs';
import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
import {GitHub,archive,loadPlan} from '../release/github.mjs';
import {findNormalRuns} from '../release/normal-ci.mjs';
import {CORE,CORE_JOBS,candidate,canonical,digest,jobs,requireThat} from '../release/verify.mjs';
import {assertWorkflows,assertWorkflowPair,freezeWorkflows,guardedWriter} from './workflow-freeze.mjs';
import {channelContext,integrationConfiguration} from '../release/channels.mjs';
import {channelActivation,controllerContext} from './channel-controls.mjs';

const same=(a,b)=>canonical(a)===canonical(b);
const stageContext=p=>channelContext(p.policy.channel);
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
  const context=channelContext(m.channel);
  requireThat(m.schema===context.stageSchema,'Unsupported channel staging policy');
  if(context.channel==='main')requireThat(m.application_fanout==='per-integration','Main requires independent guarded family staging');
  requireThat(same(m.libraries.map(t=>t.id).sort(),['csharp','matlab','python','unity']),'All four integrations are required');
  requireThat(new Set(m.libraries.map(t=>t.repository)).size===4,'Duplicate integration repository');
  requireThat(m.application_fanout===undefined || ['all-integrations','per-integration'].includes(m.application_fanout),'Unknown integration fanout policy');
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
export function freezeStage(g,m,c,{freeze=true}={}) {
  policy(m);candidate(c);
  const context=channelContext(m.channel);
  requireThat(g.head(CORE,context.branch)===c.commit,'Core channel moved before integration handoff');
  registered(g,CORE,context.stageWorkflow.split('/').at(-1));
  registered(g,CORE,'propagate-matlab.yml');
  const receivers={};
  for(const t of m.libraries) {
    const heads={main:g.head(t.repository,'main'),dev:g.head(t.repository,'dev')};
    const trusted={};
    for(const branch of ['main','dev']) {
      const cap=json(g,t.repository,heads[branch],context.receiverPolicy);
      requireThat(cap.schema===context.receiverSchema && cap.core_workflow===context.stageWorkflow && cap.fanout_workflow===t.fanout && cap.staged_applications===true && same([...cap.applications].sort(),[...t.applications].sort()),`Deploy staged application guard on ${t.repository}/${branch}`);
      trusted[branch]=Object.fromEntries([context.receiverPolicy,'.github/scripts',`.github/workflows/${t.normal}`,`.github/workflows/${t.fanout}`,...(m.application_fanout==='per-integration'?['.github/workflows/notify-core-stage.yml']:[]),...(context.channel==='main'?[integrationConfiguration(t.id),'.github/workflows']:[])].map(p=>[p,g.object(t.repository,heads[branch],p).sha]));
    }
    if(context.channel==='main')for(const path of ['.github/scripts',integrationConfiguration(t.id),'.github/workflows'])requireThat(trusted.main[path]===trusted.dev[path],`Owner channel tooling differs between main and dev: ${t.id}/${path}`);
    for(const f of [t.normal,t.fanout,'promote-wss.yml','wss-preflight.yml']) registered(g,t.repository,f);
    if(m.application_fanout==='per-integration')registered(g,t.repository,'notify-core-stage.yml');
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
      const mergeGuard=t.merge_evidence_guard;
      requireThat(mergeGuard?.path==='.github/scripts/MatlabPromotion.ps1' && /^[0-9a-f]{40}$/.test(mergeGuard.sha),'Pin the reviewed MATLAB closed-PR CI validator before handoff');
      for(const branch of ['main','dev']) requireThat(g.object(t.repository,heads[branch],mergeGuard.path).sha===mergeGuard.sha,`MATLAB merged-wrapper evidence validator is not the reviewed version on ${branch}`);
      matlabReady(g,t.repository);
      for(const app of t.applications) matlabReady(g,app);
    }
    receivers[t.id]={heads,trusted};
  }
  const body={schema:context.stagePlanSchema,...(context.channel==='main'?{channel:'main'}:{}),candidate:c,policy:m,policy_sha256:digest(m),receivers};
  requireThat(g.head(CORE,context.branch)===c.commit,'Core channel moved while freezing receiver tooling');
  const p={...body,plan_id:digest(body)};
  return freeze?freezeWorkflows(g,p,'stage'):p;
}
export function validatePlan(p) {
  candidate(p.candidate);policy(p.policy);
  const {plan_id,...body}=p;
  const context=stageContext(p);
  requireThat(p.schema===context.stagePlanSchema && (context.channel!=='main'||p.channel==='main') && (p.channel===undefined||p.channel===context.channel) && digest(body)===plan_id && digest(p.policy)===p.policy_sha256,'Stage plan identity mismatch');
  if(context.channel==='main')requireThat(/^[a-f0-9]{64}$/.test(p.acceptance_plan_id),'Main stage requires its paired acceptance plan');
  requireThat(same(Object.keys(p.receivers).sort(),p.policy.libraries.map(t=>t.id).sort()),'Missing integration snapshots');
  return p;
}
export function loadStage(g,id,{channel='dev'}={}) {
  requireThat(/^[1-9][0-9]*$/.test(id),'Invalid producer run ID');
  const context=channelContext(channel);
  const p=validatePlan(JSON.parse(g.download(CORE,id,context.stageArtifact,['plan.json'])['plan.json']));
  const c=p.candidate,r=g.run(CORE,id);
  requireThat(p.policy.channel===context.channel && c.run_id===id && r.repository?.full_name===CORE && r.head_repository?.full_name===CORE && r.path==='.github/workflows/artifacts.yml' && r.event==='push' && r.head_branch===context.branch && r.head_sha===c.commit && r.status==='completed' && r.conclusion==='success' && g.head(CORE,context.branch)===c.commit,'Stale or unsuccessful canonical channel producer');
  jobs(g.jobs(CORE,r),CORE_JOBS);
  archive(g,c);
  requireThat(same(json(g,CORE,c.commit,context.stagePolicy),p.policy),'Plan policy is not the reviewed producer policy');
  assertWorkflows(g,p,'stage',{live:false});
  if(context.channel==='main')assertWorkflowPair(g,loadPlan(g,id),p);
  return p;
}
function trusted(g,p,t,head) {
  assertWorkflows(g,p,'stage',{ids:[t.id]});
  const context=stageContext(p);
  for(const branch of ['main','dev']) {
    const ref=branch===context.branch?head:g.head(t.repository,branch);
    for(const [path,sha] of Object.entries(p.receivers[t.id].trusted[branch])) requireThat(g.object(t.repository,ref,path).sha===sha,`Staged receiver tooling changed: ${t.id}/${branch}/${path}`);
  }
}
function mergedLibrary(g,p,t) {
  const context=stageContext(p),head=g.head(t.repository,context.branch),record=json(g,t.repository,head,t.record);
  if(!record.candidate || !Object.keys(p.candidate).every(k=>String(record.candidate[k])===p.candidate[k])) return null;
  if(context.channel==='main')requireThat(head!==p.receivers[t.id].heads.main,'Main requires an actual new dependency merge, not frozen/no-op source');
  const matches=g.pages(`repos/${t.repository}/commits/${head}/pulls`).filter(pr=>pr.merged_at && pr.merge_commit_sha===head && pr.base?.ref===context.branch);
  requireThat(matches.length===1,`Require unique dependency merge for ${t.id}; no no-op or preparation-only acceptance`);
  const pr=g.get(`repos/${t.repository}/pulls/${matches[0].number}`);
  requireThat(pr.merged===true && pr.state==='closed' && pr.merge_commit_sha===head && pr.user?.type==='Bot' && pr.merged_by?.type==='User' && pr.head?.repo?.full_name===t.repository && pr.base?.repo?.full_name===t.repository && pr.base.ref===context.branch,`Require human-reviewed bot dependency merge for ${t.id}`);
  trusted(g,p,t,head);
  const accepted={repository:t.repository,head_sha:head,pr_number:String(pr.number),merged_at:pr.merged_at};
  if(context.channel==='main')accepted.execution={source_sha:head,authority_source_sha:p.receivers[t.id].heads.main,trusted:Object.fromEntries(['.github/scripts',integrationConfiguration(t.id),'.github/workflows'].map(path=>[path,p.receivers[t.id].trusted.main[path]]))};
  return accepted;
}
function normalRuns(g,p,t,accepted) {
  return findNormalRuns(g,{repository:t.repository,workflow:`.github/workflows/${t.normal}`,branch:stageContext(p).branch},accepted.head_sha,accepted.merged_at);
}
const title=p=>`Resume application promotion / wss-${p.candidate.run_id}`;
const journalName=(p,run,attempt)=>`wss-${stageContext(p).channel}-stage-intents-${p.candidate.run_id}-${run}-a${attempt}`;
function action(p,t,accepted,phase,inputs) {
  const context=stageContext(p),main=context.channel==='main';
  const body={candidate:p.candidate,plan_id:p.plan_id,target:t.id,repository:t.repository,head_sha:accepted.head_sha,merged_at:accepted.merged_at,phase,
    workflow:phase==='normal-ci'?t.normal:t.fanout,ref:phase==='normal-ci'?context.branch:'main',source_sha:phase==='normal-ci'||main?accepted.head_sha:p.receivers[t.id].heads.main,
    ...(main&&phase==='applications'?{authority_source_sha:p.receivers[t.id].heads.main}:{}),inputs};
  return {...body,key:digest({...body,inputs:phase==='applications'?{producer_run_id:p.candidate.run_id}:inputs})};
}
function existing(g,p,a) {
  if(a.phase==='normal-ci') return findNormalRuns(g,{repository:a.repository,workflow:`.github/workflows/${a.workflow}`,branch:stageContext(p).branch},a.source_sha,a.merged_at);
  const runs=g.pages(`repos/${a.repository}/actions/workflows/${a.workflow}/runs?branch=${a.ref}&event=workflow_dispatch&head_sha=${a.source_sha}`,'workflow_runs');
  return a.phase==='applications'?runs.filter(r=>r.display_title===title(p)):runs.filter(r=>Date.parse(r.created_at)>=Date.parse(a.merged_at));
}
function priorIntents(g,p,excludeRun='') {
  const result=[];
  for(const r of g.pages(`repos/${CORE}/actions/workflows/${stageContext(p).stageWorkflow.split('/').at(-1)}/runs?branch=main`,'workflow_runs')) {
    if(String(r.id)===excludeRun) continue;
    for(const a of g.pages(`repos/${CORE}/actions/runs/${r.id}/artifacts`,'artifacts').filter(a=>a.name.startsWith(`wss-${stageContext(p).channel}-stage-intents-${p.candidate.run_id}-`))) {
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
  const context=stageContext(p);
  assertWorkflows(g,p,'stage',{live:false});
  requireThat(/^[1-9][0-9]*$/.test(run) && Number.isSafeInteger(attempt) && attempt>=1,'Invalid stage emitter');
  const perFamily=p.policy.application_fanout==='per-integration';
  const integrations={},pending=[],blocked=[],actions=[];
  for(const t of p.policy.libraries) {
    try {
      const accepted=mergedLibrary(g,p,t);
      if(!accepted) pending.push(t.id); else integrations[t.id]=accepted;
    } catch(error) {if(!perFamily)throw error;blocked.push({target:t.id,reason:error.message});pending.push(t.id);}
  }
  const base={schema:`wss-${context.channel}-stage-intents/v1`,plan_id:p.plan_id,candidate:p.candidate,run_id:run,attempt};
  // Preserve older frozen all-integration policy. New reviewed producers authorize
  // each family independently, without accepting another family's missing proof.
  if(!perFamily && pending.length) return {...base,state:'waiting-for-integrations',pending,actions};
  const ready={};
  for(const t of p.policy.libraries) {
    const accepted=integrations[t.id];if(!accepted)continue;
    try {
      const runs=normalRuns(g,p,t,accepted);
      requireThat(runs.length<=1,`Ambiguous committed-state CI for ${t.id}; inspect recovery`);
      if(!runs.length) {actions.push(action(p,t,accepted,'normal-ci',{}));pending.push(t.id);continue;}
      const r=runs[0];
      requireThat(r.repository?.full_name===t.repository && r.head_repository?.full_name===t.repository && r.path===`.github/workflows/${t.normal}` && r.head_sha===accepted.head_sha,'Normal CI source mismatch');
      requireThat(r.status!=='completed' || r.conclusion==='success',`Failed integration CI for ${t.id}; no blind retry`);
      if(r.status!=='completed') {pending.push(t.id);continue;}
      jobs(g.jobs(t.repository,r),t.jobs);
      accepted.normal_ci={run_id:String(r.id),attempt:r.run_attempt};ready[t.id]=accepted;
    } catch(error) {if(!perFamily)throw error;blocked.push({target:t.id,reason:error.message});pending.push(t.id);}
  }
  const authorized=perFamily?Object.keys(ready).length>0:Object.keys(ready).length===4;
  const barrier=authorized?{schema:context.channel==='main'?'wss-integration-barrier/v3':perFamily?'wss-integration-barrier/v2':'wss-integration-barrier/v1',...(context.channel==='main'?{channel:'main'}:{}),plan_id:p.plan_id,candidate:p.candidate,integrations:ready,emitter:{run_id:run,attempt,workflow:context.stageWorkflow}}:null;
  if(barrier) for(const t of p.policy.libraries.filter(t=>Object.hasOwn(ready,t.id))) actions.push(action(p,t,ready[t.id],'applications',{producer_run_id:p.candidate.run_id,barrier_run_id:run,barrier_attempt:String(attempt),...(context.channel==='main'?{channel:'main'}:{})}));
  const prior=priorIntents(g,p,excludeCurrent?run:''),needed=[];
  for(const a of actions) {
    try {
      const runs=existing(g,p,a);
      requireThat(runs.length<=1,`Ambiguous staged dispatch for ${a.target}`);
      if(runs.length) {requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success',`Staged dispatch failed for ${a.target}; inspect before recovery`);continue;}
      requireThat(!prior.some(x=>x.key===a.key),`Uncertain prior dispatch for ${a.target}; no automatic duplicate`);
      needed.push(a);
    } catch(error) {if(!perFamily)throw error;blocked.push({target:a.target,reason:error.message});}
  }
  return {...base,state:barrier?'applications-authorized':Object.keys(integrations).length?'waiting-for-committed-ci':'waiting-for-integrations',pending:[...new Set(pending)],...(blocked.length?{blocked}:{}),actions:needed,...(barrier?{barrier}:{})};
}
export function assertFamilyBarrier(p,barrier,live,target,run,attempt) {
  const context=stageContext(p);
  requireThat(barrier?.schema===(context.channel==='main'?'wss-integration-barrier/v3':'wss-integration-barrier/v2') && (context.channel!=='main'||barrier.channel==='main') && barrier.plan_id===p.plan_id && same(barrier.candidate,p.candidate) && same(barrier.emitter,{run_id:run,attempt,workflow:context.stageWorkflow}) &&
    Object.keys(barrier.integrations||{}).every(id=>p.policy.libraries.some(t=>t.id===id)) &&
    barrier.integrations?.[target.id] && live?.integrations?.[target.id] && same(barrier.integrations[target.id],live.integrations[target.id]),'Current integration no longer matches its authorized barrier');
}
export function executeStage(g,reader,p,j) {
  validatePlan(p);
  const context=stageContext(p);
  const saved=JSON.parse(reader.download(CORE,j.run_id,journalName(p,j.run_id,j.attempt),['intent.json'])['intent.json']);
  requireThat(same(saved,j),'Durable dev stage journal differs');
  const live=prepareStage(reader,p,j.run_id,j.attempt,true);
  requireThat(live.plan_id===j.plan_id,'Dev plan changed');
  const perFamily=p.policy.application_fanout==='per-integration';
  if(j.barrier && !perFamily) requireThat(same(j.barrier,live.barrier),'Integration barrier changed after journaling');
  for(const a of j.actions) {
    const t=p.policy.libraries.find(t=>t.id===a.target);
    const inputs=a.phase==='normal-ci'?{}:{producer_run_id:p.candidate.run_id,barrier_run_id:j.run_id,barrier_attempt:String(j.attempt),...(context.channel==='main'?{channel:'main'}:{})};
    requireThat(t && same(a.inputs,inputs) && same(a,action(p,t,{head_sha:a.head_sha,merged_at:a.merged_at},a.phase,inputs)),'Invalid staged action');
    requireThat(a.phase==='normal-ci' || a.phase==='applications' && j.barrier && (perFamily?Boolean(j.barrier.integrations[t.id]):Object.keys(j.barrier.integrations).length===4),'Applications require their verified integration authorization');
    if(perFamily && a.phase==='applications')assertFamilyBarrier(p,j.barrier,live.barrier,t,j.run_id,j.attempt);
    requireThat(reader.head(CORE,context.branch)===p.candidate.commit,'Core moved before stage dispatch');
    for(const lib of perFamily?[t]:p.policy.libraries) requireThat(reader.head(lib.repository,context.branch)=== (j.barrier?.integrations[lib.id]?.head_sha || mergedLibrary(reader,p,lib)?.head_sha),'Integration moved before stage dispatch');
    requireThat(reader.head(a.repository,a.ref)===a.source_sha,'Dispatch source moved');
    trusted(reader,p,t,a.head_sha);
    const runs=existing(reader,p,a);
    requireThat(runs.length<=1,'Ambiguous concurrent stage dispatch');
    if(runs.length) {requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success','Concurrent stage failed');continue;}
    const args=['api','--method','POST',`repos/${a.repository}/actions/workflows/${a.workflow}/dispatches`,'-f',`ref=${a.ref}`];
    for(const [k,v] of Object.entries(a.inputs)) args.push('-f',`inputs[${k}]=${v}`);
    guardedWriter(g,reader,p,'stage',{ids:[t.id],purpose:'stage',...(context.channel==='main'?{executionSource:a.source_sha}:{})}).command(args);
  }
}
export function verifyBarrier(g,producerId,runId,attempt,repository,{channel='dev'}={}) {
  const context=channelContext(channel),p=loadStage(g,producerId,{channel});
  const target=p.policy.libraries.find(t=>t.repository===repository);
  requireThat(target,'Unknown application fanout caller');
  requireThat(/^[1-9][0-9]*$/.test(runId) && Number.isSafeInteger(attempt) && attempt>=1,'Invalid barrier emitter identity');
  const r=g.run(CORE,runId);
  requireThat(r.repository?.full_name===CORE && r.head_repository?.full_name===CORE && r.path===context.stageWorkflow && r.head_branch==='main' && r.head_sha===g.head(CORE,'main') && r.run_attempt===attempt && ['workflow_run','schedule','workflow_dispatch'].includes(r.event),'Barrier did not originate from trusted Core main');
  const actual=g.jobs(CORE,r).filter(j=>j.name===`Prepare verified ${context.channel} propagation stage`);
  requireThat(actual.length===1 && actual[0].status==='completed' && actual[0].conclusion==='success','Barrier preparation job did not succeed');
  const name=`wss-integration-barrier-${producerId}-a${attempt}`,a=g.artifact(CORE,runId,name);
  requireThat(a.workflow_run.head_sha===r.head_sha,'Barrier artifact source mismatch');
  const barrier=JSON.parse(g.download(CORE,runId,name,['barrier.json'])['barrier.json']);
  const live=prepareStage(g,p,runId,attempt,true);
  if(p.policy.application_fanout==='per-integration') {
    assertFamilyBarrier(p,barrier,live.barrier,target,runId,attempt);
  } else requireThat(barrier.schema==='wss-integration-barrier/v1' && same(barrier,live.barrier),'Current integrations no longer match the authorized barrier');
  return barrier;
}
async function main() {
  const [command,...args]=process.argv.slice(2),g=new GitHub();
  const context=channelContext(process.env.WSS_CI_CHANNEL||'dev');
  if(['prepare','dispatch'].includes(command))controllerContext(process.env,command==='prepare'?'stage-prepare':'stage-dispatch',CORE,channelActivation(JSON.parse(readFileSync(new URL('./channel-activation.json',import.meta.url)))));
  if(command==='freeze') {
    requireThat(context.channel==='dev','Main requires the atomic paired freeze command');
    const [candidateFile,directory]=args,c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(new URL(`./${context.channel}-propagation.json`,import.meta.url)));
    requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/dev' && process.env.GITHUB_EVENT_NAME==='push' && c.run_id===process.env.GITHUB_RUN_ID && c.commit===process.env.GITHUB_SHA,'Freeze only the canonical dev push candidate');
    archive(g,c);
    const p=freezeStage(g,m,c);mkdirSync(directory,{recursive:true});writeFileSync(join(directory,'plan.json'),canonical(p)+'\n');
  } else if(command==='prepare') {
    let [id,directory]=args;
    if(!id) {
      const event=JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH));
      if(event.workflow_run) id=String(event.workflow_run.id);
      else {const runs=g.pages(`repos/${CORE}/actions/workflows/artifacts.yml/runs?branch=${context.branch}&event=push&head_sha=${g.head(CORE,context.branch)}`,'workflow_runs');requireThat(runs.length===1,'Require one current canonical producer');id=String(runs[0].id);}
    }
    const p=loadStage(g,id,{channel:context.channel});
    if(context.channel==='main')requireThat(process.env.GITHUB_SHA===p.candidate.commit,'Main stage controller source differs from the canonical main candidate');
    const j=prepareStage(g,p,process.env.GITHUB_RUN_ID,Number(process.env.GITHUB_RUN_ATTEMPT));
    mkdirSync(directory,{recursive:true});writeFileSync(join(directory,'intent.json'),canonical(j)+'\n');
    if(j.barrier) writeFileSync(join(directory,'barrier.json'),canonical(j.barrier)+'\n');
    appendFileSync(process.env.GITHUB_OUTPUT,`producer_run_id=${id}\nartifact_name=${journalName(p,j.run_id,j.attempt)}\nactions=${j.actions.length}\nbarrier=${Boolean(j.barrier)}\n`);
    appendFileSync(process.env.GITHUB_STEP_SUMMARY,`Dev propagation: ${j.state}; pending integrations: ${j.pending.join(', ') || 'none'}; blocked families: ${(j.blocked||[]).map(x=>`${x.target}: ${x.reason}`).join('; ') || 'none'}. No release publication.\n`);
  } else if(command==='dispatch') {
    requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main' && process.env.GITHUB_WORKFLOW_REF===`${CORE}/${context.stageWorkflow}@refs/heads/main`,'Dispatch only from registered trusted Core main workflow');
    const j=JSON.parse(readFileSync(args[0]));
    requireThat(j.run_id===process.env.GITHUB_RUN_ID && j.attempt===Number(process.env.GITHUB_RUN_ATTEMPT),'Journal must belong to this exact workflow attempt');
    requireThat(process.env.WSS_JOURNAL_READ_TOKEN,'Missing Core read-only journal token');
    const reader=new GitHub(process.env.WSS_JOURNAL_READ_TOKEN),p=loadStage(reader,j.candidate.run_id,{channel:context.channel});
    if(context.channel==='main')requireThat(process.env.GITHUB_SHA===p.candidate.commit,'Main stage controller source differs from the canonical main candidate');
    executeStage(g,reader,p,j);
  } else if(command==='verify-barrier') {
    requireThat(args.length===3,'verify-barrier requires producer run ID, barrier run ID and attempt');
    console.log(canonical(verifyBarrier(g,args[0],args[1],Number(args[2]),process.env.GITHUB_REPOSITORY,{channel:context.channel})));
  } else if(command==='matlab') {
    const event=JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH)),id=String(event.workflow_run?.id),p=loadStage(g,id,{channel:context.channel}),t=p.policy.libraries.find(t=>t.id==='matlab');
    matlabReady(g,t.repository);for(const app of t.applications) matlabReady(g,app);
    registered(g,t.repository,'promote-wss.yml');
    const runs=g.pages(`repos/${t.repository}/actions/workflows/promote-wss.yml/runs?event=workflow_dispatch`,'workflow_runs').filter(r=>r.display_title===`Promote universal WSS / wss-${id}-${p.candidate.artifact_id}`);
    requireThat(runs.length<=1,'Ambiguous MATLAB promotion');
    if(runs.length) requireThat(runs[0].status!=='completed' || runs[0].conclusion==='success','Prior MATLAB promotion failed; inspect recovery');
    mkdirSync(args[0],{recursive:true});writeFileSync(join(args[0],'dispatch.json'),JSON.stringify({ref:context.branch,inputs:{candidate_json:canonical(p.candidate)}}));
    appendFileSync(process.env.GITHUB_OUTPUT,`dispatch_needed=${runs.length===0}\n`);
  } else throw new Error('Expected freeze, prepare, dispatch, verify-barrier or matlab');
}
if(isEntrypoint(import.meta.url)) main().catch(e=>{console.error(e.message);process.exitCode=1;});
