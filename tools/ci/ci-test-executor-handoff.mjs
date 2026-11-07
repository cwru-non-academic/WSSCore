// This is transport validation, not a signature. expectedDigest must be supplied
// directly by needs.<reviewed-auth-job>.outputs.executor_handoff_sha256. Never
// obtain it from the downloaded file, dispatch input, or selected-test code.
import {candidate,canonical,digest,requireThat} from '../release/verify.mjs';
import {testSelectionPolicy} from '../release/campaign-test-selection.mjs';
import {verifyTestSelection} from '../release/test-selection-contract.mjs';
import {channelContext} from '../release/channels.mjs';
const same=(a,b)=>canonical(a)===canonical(b);
const keys=(value,names)=>value&&typeof value==='object'&&!Array.isArray(value)&&same(Object.keys(value).sort(),[...names].sort());
const sha=value=>typeof value==='string'&&/^[a-f0-9]{40}$/.test(value);
const hash=value=>typeof value==='string'&&/^[a-f0-9]{64}$/.test(value);
const decimal=value=>typeof value==='string'&&/^[1-9][0-9]*$/.test(value);
export const EXECUTOR_HANDOFF_SCHEMA='wss-ci-test-executor-handoff/v1';

// Only the authenticated loader calls this serializer. Copy a closed non-secret
// projection, never process.env, a GitHub client, token action state, or raw bytes.
export function createCiExecutorHandoff(saved,campaignAuthority,env) {
  return {
    schema:EXECUTOR_HANDOFF_SCHEMA,
    execution:{attempt:Number(env.GITHUB_RUN_ATTEMPT),ref:env.GITHUB_REF,base_ref:env.GITHUB_BASE_REF||''},
    origin:{schema:saved.document.schema,attempt:1,artifact_id:saved.artifact.id,artifact_name:saved.artifact.name,created_at:saved.artifact.created_at,context_sha256:digest(saved.document.context)},
    identity:structuredClone(saved.document.identity),
    context:{scope:saved.context.scope,selection:structuredClone(saved.binding.selection),request:structuredClone(saved.binding.request),selection_id:saved.binding.selection_id},
    campaign:campaignAuthority?structuredClone(campaignAuthority):null
  };
}

export function verifyCiExecutorHandoff(value,policy,targetId,run,expectedId,expectedDigest,{env=process.env}={}) {
  requireThat(hash(expectedDigest)&&digest(value)===expectedDigest,'Executor handoff differs from trusted prerequisite output');
  requireThat(keys(value,['schema','execution','origin','identity','context','campaign'])&&value.schema===EXECUTOR_HANDOFF_SCHEMA,'Invalid executor handoff schema/fields');
  const {identity:i,execution:e,origin:o,context:c}=value;
  requireThat(keys(i,['repository','commit','run_id','event','api_head_sha','workflow','workflow_ref','workflow_sha','channel','targetId','policy','mode','productCandidate']),'Invalid executor handoff identity fields');
  channelContext(i.channel);
  testSelectionPolicy({test_selection_policy:i.policy});
  requireThat(same(i.policy,policy)&&i.targetId===targetId&&policy.targets.filter(t=>t.id===targetId&&t.repository===run.repository).length===1,'Executor handoff policy/target differs');
  requireThat(keys(run,['repository','commit','run_id','event'])&&sha(run.commit)&&decimal(run.run_id)&&['push','pull_request','workflow_dispatch'].includes(run.event)&&['repository','commit','run_id','event'].every(k=>i[k]===run[k]),'Executor handoff run/source differs');
  requireThat(keys(e,['attempt','ref','base_ref'])&&Number.isSafeInteger(e.attempt)&&e.attempt>=1&&decimal(env.GITHUB_RUN_ATTEMPT)&&e.attempt===Number(env.GITHUB_RUN_ATTEMPT)&&e.ref===env.GITHUB_REF&&e.base_ref===(env.GITHUB_BASE_REF||''),'Executor handoff attempt/ref differs');
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===run.repository&&env.GITHUB_SHA===run.commit&&env.GITHUB_RUN_ID===run.run_id&&env.GITHUB_EVENT_NAME===run.event&&env.WSS_CI_CHANNEL===i.channel,'Executor handoff runner identity differs');
  requireThat(run.event==='pull_request'?e.base_ref===i.channel&&/^refs\/pull\/[1-9][0-9]*\/merge$/.test(e.ref):e.ref===`refs/heads/${i.channel}`&&e.base_ref==='','Executor handoff channel differs');
  requireThat(/^\.github\/workflows\/[^/@]+\.ya?ml$/.test(i.workflow)&&i.workflow_ref===`${run.repository}/${i.workflow}@${e.ref}`&&i.workflow_ref===env.GITHUB_WORKFLOW_REF&&i.workflow_sha===run.commit&&i.workflow_sha===env.GITHUB_WORKFLOW_SHA&&sha(i.api_head_sha)&&(run.event==='pull_request'||i.api_head_sha===run.commit),'Executor handoff workflow/source differs');
  requireThat(['committed','candidate'].includes(i.mode)&&(i.mode!=='candidate'||run.event==='workflow_dispatch'&&i.productCandidate),'Executor handoff mode differs');
  if(i.productCandidate!==null)candidate(i.productCandidate);
  requireThat(keys(o,['schema','attempt','artifact_id','artifact_name','created_at','context_sha256'])&&['wss-ci-test-context/v1','wss-ci-test-context/v2'].includes(o.schema)&&o.attempt===1&&decimal(o.artifact_id)&&o.artifact_name===`wss-ci-test-context-${targetId}`&&typeof o.created_at==='string'&&Number.isFinite(Date.parse(o.created_at))&&hash(o.context_sha256),'Invalid durable executor handoff origin');
  requireThat(keys(c,['scope','selection','request','selection_id'])&&['run','campaign'].includes(c.scope)&&hash(expectedId)&&c.selection_id===expectedId,'Executor handoff selection differs from trusted prerequisite output');
  verifyTestSelection(c.selection,c.request,expectedId);
  requireThat(c.selection.channel===i.channel&&c.selection.targets[targetId]?.repository===run.repository&&c.request.targets.every(t=>keys(t.variables,[])),'Executor handoff selected context differs');
  if(c.scope==='campaign') {
    const a=value.campaign;
    requireThat(keys(a,['kind','producer','plan_id','policy_sha256'])&&a.kind==='acceptance'&&hash(a.plan_id)&&hash(a.policy_sha256)&&i.productCandidate&&same(a.producer,i.productCandidate),'Invalid authenticated campaign authority');
    candidate(a.producer);
    requireThat(same(c.selection.campaign,{repository:a.producer.repository,commit:a.producer.commit,run_id:a.producer.run_id}),'Executor handoff campaign producer differs');
  } else {
    const target=policy.targets.find(t=>t.id===targetId);
    requireThat(value.campaign===null&&same(c.request,{channel:i.channel,campaign:{repository:run.repository,commit:run.commit,run_id:run.run_id},targets:[{...target,variables:{}}]}),'Executor handoff ordinary context differs');
  }
  return {selection:c.selection,request:c.request,selection_id:c.selection_id};
}

// Defense in depth only: fresh, isolated jobs are the credential boundary. This
// guard cannot establish that a runner home, action state, or unknown env is clean.
export function assertNoExecutorBootstrapCredentials(env=process.env) {
  const credential=/^(?:GH_TOKEN|GITHUB_TOKEN|GH_ENTERPRISE_TOKEN|GITHUB_ENTERPRISE_TOKEN|WSS_.*(?:TOKEN|PRIVATE_KEY|SECRET)|INPUT_(?:TOKEN|PRIVATE_KEY|PRIVATE-KEY)|ACTIONS_ID_TOKEN_REQUEST_TOKEN)$/i;
  requireThat(!Object.entries(env).some(([name,value])=>credential.test(name)&&value!==undefined&&value!==''),'Selected executor contains bootstrap credentials');
}
