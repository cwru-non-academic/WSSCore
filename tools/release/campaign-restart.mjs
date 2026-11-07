import {canonical,CORE,digest,requireThat,candidate,manifest,plan,producer} from './verify.mjs';
import {loadPlan,archive} from './github.mjs';
import {validatePlan} from '../ci/dev-propagation.mjs';
import {assertPairedTestSelection} from './campaign-test-selection.mjs';
import {channelContext} from './channels.mjs';

export const RESTART_DIRECTORY='tools/ci/campaign-restarts/';
const RECORD_PATH=/^tools\/ci\/campaign-restarts\/(dev|main)\/[a-f0-9]{64}\.json(?![\s\S])/;
const same=(a,b)=>canonical(a)===canonical(b);
const sha=x=>typeof x==='string'&&/^[a-f0-9]{40}$/.test(x);
const hash=x=>typeof x==='string'&&/^[a-f0-9]{64}$/.test(x);
const exact=(x,keys)=>x&&same(Object.keys(x).sort(),keys.sort());
export function restartRequest(r) {
  requireThat(exact(r,['schema','channel','expected_head','predecessor','receivers','reason','request_id']),'Unexpected restart request fields');
  channelContext(r.channel);
  requireThat(r.schema==='wss-campaign-restart/v1'&&sha(r.expected_head),'Invalid restart schema/source');
  requireThat(typeof r.reason==='string'&&r.reason.trim()===r.reason&&r.reason.length>=10&&r.reason.length<=1000&&!/[\x00-\x1f\x7f]/.test(r.reason),'Explicit single-line restart reason required (10-1000 characters)');
  requireThat(exact(r.predecessor,['candidate','acceptance_plan_id','stage_plan_id']),'Unexpected predecessor fields');
  candidate(r.predecessor.candidate);
  requireThat(hash(r.predecessor.acceptance_plan_id)&&hash(r.predecessor.stage_plan_id),'Restart predecessor/source mismatch');
  requireThat(r.receivers&&typeof r.receivers==='object'&&!Array.isArray(r.receivers),'Missing receiver snapshots');
  for(const [repo,heads] of Object.entries(r.receivers))requireThat(/^cwru-non-academic\/[A-Za-z0-9_]+$/.test(repo)&&exact(heads,['dev','main'])&&sha(heads.dev)&&sha(heads.main),'Invalid restart receiver snapshot');
  const {request_id,...body}=r;
  requireThat(hash(request_id)&&digest(body)===request_id,'Restart request digest mismatch');
  return r;
}
export const restartPath=r=>`${RESTART_DIRECTORY}${restartRequest(r).channel}/${r.request_id}.json`;

// Authenticate existing immutable evidence; never replace or resolve old plans.
export function predecessor(g,channel,runId) {
  const context=channelContext(channel),a=loadPlan(g,runId),r=g.run(CORE,runId);
  requireThat(a.channel===channel&&r.status==='completed'&&r.conclusion==='success','Restart requires a successful canonical predecessor');
  const expectedHead=a.candidate.commit;
  const s=validatePlan(JSON.parse(g.download(CORE,runId,context.stageArtifact,['plan.json'])['plan.json']));
  requireThat(s.policy.channel===channel&&same(s.candidate,a.candidate),'Predecessor plans do not share the exact candidate/channel');
  const policy=g.get(`repos/${CORE}/contents/${context.stagePolicy}?ref=${expectedHead}`);
  requireThat(policy?.type==='file'&&policy.encoding==='base64'&&same(JSON.parse(Buffer.from(policy.content,'base64')),s.policy),'Predecessor stage policy differs from reviewed source');
  assertPairedTestSelection(a,s);
  if(channel==='main')requireThat(s.acceptance_plan_id===a.plan_id,'Predecessor main pair link differs');
  const evidence=archive(g,a.candidate);
  producer(a,{run:r,current_head:expectedHead,jobs:g.jobs(CORE,r),artifact:evidence.artifact,archive_sha256:a.candidate.sha256});
  return {acceptance:a,stage:s,identity:{candidate:a.candidate,acceptance_plan_id:a.plan_id,stage_plan_id:s.plan_id}};
}

// An expected receiver race can leave a failed request-only Core head. Recover
// only through ancestry with identical product/policy bytes and modes. This is
// not a fallback for source changes, missing proof, or an unrelated old run.
export function assertRestartSource(g,source,expectedHead) {
  requireThat(sha(source)&&sha(expectedHead),'Invalid restart source identities');
  if(source===expectedHead)return;
  const ancestry=g.get(`repos/${CORE}/compare/${source}...${expectedHead}?per_page=1`);
  requireThat(ancestry.status==='ahead'&&ancestry.base_commit?.sha===source&&ancestry.merge_base_commit?.sha===source,'Historical predecessor is not an ancestor of the requested current source');
  const inventory=ref=>{
    const tree=g.get(`repos/${CORE}/git/trees/${ref}?recursive=1`);
    requireThat(tree.truncated===false&&Array.isArray(tree.tree),'Complete restart source tree required');
    return tree.tree.filter(x=>x.type!=='tree').map(({path,mode,type,sha})=>({path,mode,type,sha})).sort((a,b)=>a.path.localeCompare(b.path));
  };
  const old=inventory(source),current=inventory(expectedHead);
  const product=rows=>rows.filter(x=>!x.path.startsWith(RESTART_DIRECTORY));
  requireThat(same(product(old),product(current)),'Historical predecessor changes product/tooling/policy source');
  for(const entry of [...old,...current].filter(x=>x.path.startsWith(RESTART_DIRECTORY)))requireThat(RECORD_PATH.test(entry.path)&&entry.type==='blob'&&entry.mode==='100644','Historical restart namespace contains an invalid record path/mode');
  const prior=new Map(old.filter(x=>x.path.startsWith(RESTART_DIRECTORY)).map(x=>[x.path,x]));
  const now=new Map(current.filter(x=>x.path.startsWith(RESTART_DIRECTORY)).map(x=>[x.path,x]));
  for(const [path,entry] of prior)requireThat(same(now.get(path),entry),'Historical restart record was modified or removed');
  requireThat(now.size>prior.size,'Historical source must contain a new request-only checkpoint');
  for(const [path,entry] of now)if(!prior.has(path)) {
    requireThat(entry.type==='blob'&&entry.mode==='100644','Restart record must be a regular file');
    const file=g.get(`repos/${CORE}/contents/${path}?ref=${expectedHead}`);
    requireThat(file.type==='file'&&file.encoding==='base64'&&file.sha===entry.sha,'Historical request content identity differs');
    const request=restartRequest(JSON.parse(Buffer.from(file.content,'base64')));
    requireThat(path===restartPath(request),'Historical request path/digest differs');
  }
}

export function receiverHeads(g,m) {
  manifest(m);
  return Object.fromEntries(m.targets.map(t=>[t.repository,{dev:g.head(t.repository,'dev'),main:g.head(t.repository,'main')}]));
}
export function prepareRestart(g,{channel,runId,expectedHead,reason}) {
  channelContext(channel);
  requireThat(sha(expectedHead)&&g.head(CORE,channel)===expectedHead,'Core moved before restart preparation');
  const old=predecessor(g,channel,runId);
  assertRestartSource(g,old.acceptance.candidate.commit,expectedHead);
  const body={schema:'wss-campaign-restart/v1',channel,expected_head:expectedHead,predecessor:old.identity,receivers:receiverHeads(g,old.acceptance.manifest),reason};
  requireThat(g.head(CORE,channel)===expectedHead,'Core moved while preparing restart');
  return restartRequest({...body,request_id:digest(body)});
}
export function assertRestartEvidence(g,r) {
  restartRequest(r);
  const old=predecessor(g,r.channel,r.predecessor.candidate.run_id);
  assertRestartSource(g,old.acceptance.candidate.commit,r.expected_head);
  requireThat(same(old.identity,r.predecessor),'Restart predecessor evidence changed');
  requireThat(same(receiverHeads(g,old.acceptance.manifest),r.receivers),'Receiver heads changed since explicit restart request');
}

// This is deliberately NOT merge-time CAS. Strict PR checks help, but this
// exact-base/tree boundary is repeated on the canonical push before handoff.
export function assertRequestChange(r,{channel,base,changes,existing=false}) {
  restartRequest(r);
  requireThat(r.channel===channel&&r.expected_head===base,'Restart channel/base moved; prepare a new request');
  requireThat(!existing,'Restart request replay is forbidden');
  requireThat(same(changes,[{status:'A',path:restartPath(r)}]),'Restart must add exactly one immutable request and change no source/tooling');
}
export function assertRestartPlans(r,a,s) {
  restartRequest(r);plan(a);validatePlan(s);
  requireThat(a&&a.channel===r.channel&&s.policy.channel===r.channel&&same(a.candidate,s.candidate)&&a.candidate.commit!==r.expected_head&&a.candidate.run_id!==r.predecessor.candidate.run_id,'Restart must freeze a new paired campaign identity');
  requireThat(a.plan_id!==r.predecessor.acceptance_plan_id&&s.plan_id!==r.predecessor.stage_plan_id,'Restart cannot reuse predecessor plans');
  for(const t of a.manifest.targets)requireThat(a.target_snapshots[t.id]?.head_sha===r.receivers[t.repository]?.[r.channel],'Acceptance snapshot differs from requested receiver');
  for(const t of s.policy.libraries)requireThat(same(s.receivers[t.id]?.heads,r.receivers[t.repository]),'Stage snapshots differ from requested receivers');
}

export function parseChangedPaths(output) {
  if(output==='')return [];
  const fields=output.split('\0');
  requireThat(fields.pop()===''&&fields.length%2===0,'Malformed NUL-delimited Git change list');
  const changes=[];
  for(let i=0;i<fields.length;i+=2) {
    requireThat(/^[A-Z]$/.test(fields[i])&&fields[i+1].length>0,'Unexpected Git change status/path');
    changes.push({status:fields[i],path:fields[i+1]});
  }
  return changes;
}
function changed(git,base,head) {
  return parseChangedPaths(git('diff','--no-renames','--name-status','-z',base,head));
}

export function checkRestartRunner(g,{env,event:e,git}) {
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===CORE,'Restart validation requires the canonical repository runner');
  let channel,base,head;
  if(env.GITHUB_EVENT_NAME==='pull_request') {
    channel=e.pull_request.base.ref;base=e.pull_request.base.sha;head=env.GITHUB_SHA;
    requireThat(e.pull_request.base.repo.full_name===CORE,'Foreign request base');
  } else if(env.GITHUB_EVENT_NAME==='push') {
    channel=env.GITHUB_REF_NAME;head=env.GITHUB_SHA;
    requireThat(['dev','main'].includes(channel)&&env.GITHUB_WORKFLOW_SHA===head&&env.GITHUB_WORKFLOW_REF===`${CORE}/.github/workflows/artifacts.yml@refs/heads/${channel}`,'Restart requires exact trusted canonical push source');
    base=e.before;
    requireThat(/^[a-f0-9]{40}$/.test(base)&&!/^0+$/.test(base),'Push requires an existing exact before source');
  } else return null; // Noncanonical dispatch still cannot hand off.
  const changes=changed(git,base,head),allRecords=changes.filter(c=>c.path.startsWith(RESTART_DIRECTORY));
  requireThat(allRecords.every(c=>RECORD_PATH.test(c.path)),'Restart namespace requires exact channel/digest record paths');
  requireThat(allRecords.every(c=>c.status==='A'),'Restart records are immutable; modification/deletion is forbidden');
  for(const entry of allRecords)requireThat(git('ls-tree',head,'--',entry.path).startsWith('100644 blob '),'Restart record must be a regular file');
  const records=allRecords.filter(c=>c.path.startsWith(`${RESTART_DIRECTORY}${channel}/`));
  for(const entry of allRecords.filter(c=>!records.includes(c))) {
    const imported=restartRequest(JSON.parse(git('show',`${head}:${entry.path}`)));
    requireThat(entry.path===restartPath(imported)&&imported.channel!==channel,'Invalid cross-channel restart record');
    const source=g.object(CORE,g.head(CORE,imported.channel),entry.path);
    requireThat(source.type==='blob'&&source.mode==='100644'&&source.sha===git('rev-parse',`${head}:${entry.path}`),'Cross-channel record must already exist unchanged on its origin channel');
  }
  if(!records.length)return null;
  requireThat(records.length===1&&records[0].status==='A','Restart records are append-only and singular');
  const r=restartRequest(JSON.parse(git('show',`${head}:${records[0].path}`)));
  requireThat(records[0].path===restartPath(r),'Restart record path differs from digest');
  assertRequestChange(r,{channel,base,changes});
  if(env.GITHUB_EVENT_NAME==='push')requireThat(git('rev-parse',`${head}^1`)===base,'Restart push must directly extend its expected before source');
  const live=g.head(CORE,channel);
  requireThat(live===(env.GITHUB_EVENT_NAME==='push'?head:base),'Core moved during restart validation');
  if(env.GITHUB_EVENT_NAME==='push')requireThat(env.WSS_DEV_ACCEPTANCE_ENABLED==='true','Restart requires both acceptance and stage plans enabled');
  assertRestartEvidence(g,r);
  requireThat(g.head(CORE,channel)===live,'Core moved while validating restart');
  return {request:r,head};
}

export function assertPreparationBranch({expected,head,parent,changes,path}) {
  requireThat(sha(expected)&&sha(head),'Invalid preparation source');
  requireThat(changes.length===0?head===expected:
    changes.length===1&&changes[0]===`A\t${path}`&&parent===expected,
    'Preparation branch must be the exact expected head or its single existing request commit');
}
