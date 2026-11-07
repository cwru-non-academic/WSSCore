import {canonical,requireThat} from '../release/verify.mjs';
const same=(a,b)=>canonical(a)===canonical(b);
const decimal=x=>typeof x==='string'&&/^[1-9][0-9]*$/.test(x);
export const ciContextArtifact=target=>`wss-ci-test-context-${target}`;
function authority(g,input,env) {
  const {run,policy,targetId,channel}=input,attempt=Number(env.GITHUB_RUN_ATTEMPT);
  const prefix=`${run.repository}/`,ref=env.GITHUB_WORKFLOW_REF;
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===run.repository&&env.GITHUB_SHA===run.commit&&env.GITHUB_RUN_ID===run.run_id&&env.GITHUB_EVENT_NAME===run.event&&decimal(env.GITHUB_RUN_ATTEMPT)&&Number.isSafeInteger(attempt)&&typeof ref==='string'&&ref.startsWith(prefix),'Invalid CI context runner authority');
  const workflow=ref.slice(prefix.length).split('@')[0];
  requireThat(/^\.github\/workflows\/[^/@]+\.ya?ml$/.test(workflow)&&ref===`${prefix}${workflow}@${env.GITHUB_REF}`&&env.GITHUB_WORKFLOW_SHA===run.commit,'CI context workflow source mismatch');
  const current=g.run(run.repository,run.run_id);
  requireThat(String(current.id)===run.run_id&&current.repository?.full_name===run.repository&&(run.event==='pull_request'?/^[a-f0-9]{40}$/.test(current.head_sha):current.head_sha===run.commit)&&current.path===workflow&&current.event===run.event&&current.run_attempt===attempt&&current.status==='in_progress','CI context run/source/attempt mismatch');
  requireThat(policy.targets.filter(t=>t.id===targetId&&t.repository===run.repository).length===1,'CI context target mismatch');
  return {attempt,identity:{...run,api_head_sha:current.head_sha,workflow,workflow_ref:ref,workflow_sha:env.GITHUB_WORKFLOW_SHA,channel,targetId,policy,mode:input.mode||'committed',productCandidate:input.productCandidate||null}};
}
function existing(g,input,auth) {
  const {run,targetId}=input,name=ciContextArtifact(targetId);
  const matches=g.pages(`repos/${run.repository}/actions/runs/${run.run_id}/artifacts`,'artifacts').filter(a=>a.name===name);
  requireThat(matches.length<=1,'Ambiguous durable CI context artifact');
  if(!matches.length)return null;
  const a=g.artifact(run.repository,run.run_id,name);
  requireThat(String(a.id)===String(matches[0].id)&&decimal(String(a.id))&&a.name===name&&a.expired===false&&matches[0].expired===false&&String(a.workflow_run?.id)===run.run_id&&a.workflow_run?.head_sha===auth.identity.api_head_sha,'CI context artifact identity/expiry mismatch');
  const first=g.get(`repos/${run.repository}/actions/runs/${run.run_id}/attempts/1`);
  requireThat(first.run_attempt===1&&String(first.id)===run.run_id&&first.repository?.full_name===run.repository&&first.head_sha===auth.identity.api_head_sha&&first.path===auth.identity.workflow&&first.event===run.event,'CI context first attempt identity mismatch');
  const created=Date.parse(a.created_at),start=Date.parse(first.run_started_at),end=Date.parse(first.updated_at);
  requireThat(Number.isFinite(created)&&Number.isFinite(start)&&created>=start&&(auth.attempt===1||(first.status==='completed'&&Number.isFinite(end)&&created<=end)),'CI context artifact was not published in first attempt');
  const document=JSON.parse(g.download(run.repository,run.run_id,name,['context.json'])['context.json']);
  requireThat(document.schema==='wss-ci-test-context/v1'&&document.attempt===1&&same(document.identity,auth.identity)&&same(Object.keys(document).sort(),['attempt','context','identity','schema']),'CI context envelope/policy/run mismatch');
  return {created:false,artifact_name:name,document,context:document.context};
}
// No token writes. The workflow uploads once with overwrite:false, then calls
// load independently before exporting any trusted prerequisite selection outputs.
export async function prepareCiContext(g,input,{env=process.env,select,verify}={}) {
  const auth=authority(g,input,env),saved=existing(g,input,auth);
  if(saved){await verify(saved.context);return saved;}
  requireThat(auth.attempt===1,'Previous CI attempt has no durable test context; inspect recovery');
  const context=await select();
  return {created:true,artifact_name:ciContextArtifact(input.targetId),context,document:{schema:'wss-ci-test-context/v1',attempt:1,identity:auth.identity,context}};
}
export async function loadCiContext(g,input,{env=process.env,verify}={}) {
  const saved=existing(g,input,authority(g,input,env));
  requireThat(saved,'Required durable CI test context artifact is missing');
  await verify(saved.context);return saved.context;
}
