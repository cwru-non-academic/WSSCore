import {canonical,requireThat} from '../release/verify.mjs';
import {downloadVerifiedJsonArtifact} from '../release/verified-json-artifact.mjs';
const same=(a,b)=>canonical(a)===canonical(b);
const decimal=x=>typeof x==='string'&&/^[1-9][0-9]*$/.test(x);
export const ciContextArtifact=target=>`wss-ci-test-context-${target}`;
function authority(g,input,env) {
  const {run,policy,targetId,channel}=input,attempt=Number(env.GITHUB_RUN_ATTEMPT);
  const prefix=`${run.repository}/`,ref=env.GITHUB_WORKFLOW_REF;
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===run.repository&&env.GITHUB_SHA===run.commit&&env.GITHUB_RUN_ID===run.run_id&&env.GITHUB_EVENT_NAME===run.event&&decimal(env.GITHUB_RUN_ATTEMPT)&&Number.isSafeInteger(attempt)&&typeof ref==='string'&&ref.startsWith(prefix),'Invalid CI context runner authority');
  requireThat(run.event==='pull_request'?env.GITHUB_BASE_REF===channel:env.GITHUB_REF===`refs/heads/${channel}`,'CI context channel differs from the actual target branch');
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
  requireThat(String(a.id)===String(matches[0].id)&&decimal(String(a.id))&&a.name===name&&a.expired===false&&matches[0].expired===false&&String(a.workflow_run?.id)===run.run_id&&a.workflow_run?.head_sha===auth.identity.api_head_sha&&/^sha256:[a-f0-9]{64}$/.test(a.digest),'CI context artifact identity/expiry/digest mismatch');
  const first=g.get(`repos/${run.repository}/actions/runs/${run.run_id}/attempts/1`);
  requireThat(first.run_attempt===1&&String(first.id)===run.run_id&&first.repository?.full_name===run.repository&&first.head_sha===auth.identity.api_head_sha&&first.path===auth.identity.workflow&&first.event===run.event,'CI context first attempt identity mismatch');
  const created=Date.parse(a.created_at),start=Date.parse(first.run_started_at),end=Date.parse(first.updated_at);
  requireThat(Number.isFinite(created)&&Number.isFinite(start)&&created>=start&&(auth.attempt===1||(first.status==='completed'&&Number.isFinite(end)&&created<=end)),'CI context artifact was not published in first attempt');
  // Authenticate the bytes for the exact immutable ID whose metadata was checked.
  // A second name lookup can select a deleted/recreated later-attempt artifact.
  const document=JSON.parse(downloadVerifiedJsonArtifact(g,run.repository,a,['context.json'])['context.json']);
  requireThat(['wss-ci-test-context/v1','wss-ci-test-context/v2'].includes(document.schema)&&document.attempt===1&&same(document.identity,auth.identity)&&same(Object.keys(document).sort(),document.schema==='wss-ci-test-context/v2'?['attempt','context','identity','schema','variable_semantics']:['attempt','context','identity','schema'])&&(document.schema!=='wss-ci-test-context/v2'||document.variable_semantics==='effective-org-repository/no-environment/v1'),'CI context envelope/policy/run mismatch');
  return {created:false,artifact_name:name,document,context:document.context,artifact:{id:String(a.id),name,created_at:a.created_at,digest:a.digest}};
}
// No token writes. The workflow uploads once with overwrite:false, then calls
// load independently before exporting any trusted prerequisite selection outputs.
export async function prepareCiContext(g,input,{env=process.env,select,verify}={}) {
  const auth=authority(g,input,env),saved=existing(g,input,auth);
  if(saved){await verify(saved.context);const {artifact,...recovered}=saved;return recovered;}
  requireThat(auth.attempt===1,'Previous CI attempt has no durable test context; inspect recovery');
  const context=await select();
  return {created:true,artifact_name:ciContextArtifact(input.targetId),context,document:{schema:input.variableSemantics?'wss-ci-test-context/v2':'wss-ci-test-context/v1',...(input.variableSemantics?{variable_semantics:input.variableSemantics}:{}),attempt:1,identity:auth.identity,context}};
}
export async function loadVerifiedCiContext(g,input,{env=process.env,verify}={}) {
  const saved=existing(g,input,authority(g,input,env));
  requireThat(saved,'Required durable CI test context artifact is missing');
  const binding=await verify(saved.context);return {...saved,binding};
}
export async function loadCiContext(g,input,options) {
  return (await loadVerifiedCiContext(g,input,options)).context;
}
