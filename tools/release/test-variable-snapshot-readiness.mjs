import {randomBytes} from 'node:crypto';
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {join} from 'node:path';
import {GitHub} from './github.mjs';
import {downloadVerifiedJsonArtifact} from './verified-json-artifact.mjs';
import {CORE,canonical,requireThat} from './verify.mjs';
import {isEntrypoint} from './entrypoint.mjs';
import {createSnapshotRequest,validateSnapshotRequest,verifySnapshotSource,dispatchSnapshotOnce,collectLocalTestVariables,matchingSnapshotRuns,verifySnapshotRun} from './local-test-variable-readiness.mjs';
export const WORKFLOW='.github/workflows/test-variable-snapshot-readiness.yml';
export const REGISTRY='tools/ci/test-variable-readiness-sources.json';
export const INTENT='wss-readiness-variable-intent';
export const REPORT='wss-readiness-variable-report';
const same=(a,b)=>canonical(a)===canonical(b);
const decimal=x=>typeof x==='string'&&/^[1-9][0-9]*$/.test(x);
const sha=x=>typeof x==='string'&&/^[a-f0-9]{40}$/.test(x);
export function readinessAuthority(g,env=process.env) {
  const channel=env.GITHUB_REF?.replace('refs/heads/','');
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===CORE&&env.GITHUB_EVENT_NAME==='workflow_dispatch'&&['dev','main'].includes(channel)&&env.GITHUB_REF===`refs/heads/${channel}`&&sha(env.GITHUB_SHA)&&env.GITHUB_WORKFLOW_SHA===env.GITHUB_SHA&&env.GITHUB_WORKFLOW_REF===`${CORE}/${WORKFLOW}@${env.GITHUB_REF}`&&decimal(env.GITHUB_RUN_ID)&&decimal(env.GITHUB_RUN_ATTEMPT),'Actual readiness controller runner required');
  const run=g.run(CORE,env.GITHUB_RUN_ID);
  requireThat(String(run.id)===env.GITHUB_RUN_ID&&run.repository?.full_name===CORE&&run.head_repository?.full_name===CORE&&run.head_repository?.fork===false&&run.event==='workflow_dispatch'&&run.path===WORKFLOW&&run.head_sha===env.GITHUB_SHA&&run.head_branch===channel&&run.run_attempt===Number(env.GITHUB_RUN_ATTEMPT)&&run.status==='in_progress'&&Array.isArray(run.pull_requests)&&run.pull_requests.length===0,'Readiness controller metadata mismatch');
  const f=g.get(`repos/${CORE}/contents/${REGISTRY}?ref=${env.GITHUB_SHA}`);
  requireThat(f?.type==='file'&&f.encoding==='base64','Missing immutable readiness registry');
  const registry=JSON.parse(Buffer.from(f.content,'base64'));
  requireThat(registry.schema==='wss-test-variable-readiness-sources/v1'&&same(Object.keys(registry).sort(),['dev','main','schema']),'Invalid readiness registry');
  const entry=registry[channel];
  requireThat(entry&&same(Object.keys(entry).sort(),['controller_actor_ids','source'])&&Array.isArray(entry.controller_actor_ids)&&entry.controller_actor_ids.length>0&&entry.controller_actor_ids.every(decimal)&&new Set(entry.controller_actor_ids).size===entry.controller_actor_ids.length,'Readiness representative is not deployed/reviewed');
  requireThat(entry.controller_actor_ids.includes(String(run.actor?.id))&&entry.controller_actor_ids.includes(String(run.triggering_actor?.id))&&String(run.triggering_actor?.id)===env.GITHUB_ACTOR_ID,'Unreviewed readiness controller actor');
  const workflow=g.get(`repos/${CORE}/actions/workflows/${run.workflow_id}`);
  requireThat(workflow.path===WORKFLOW&&workflow.state==='active','Readiness controller is not registered');
  return {channel,controller:{repository:CORE,commit:env.GITHUB_SHA,run_id:env.GITHUB_RUN_ID,attempt:1},source:entry.source,attempt:Number(env.GITHUB_RUN_ATTEMPT)};
}
export function prepareReadiness(g,reader,auth) {
  requireThat(auth.attempt===1,'Readiness recovery cannot prepare new settings');
  const artifacts=g.pages(`repos/${CORE}/actions/runs/${auth.controller.run_id}/artifacts`,'artifacts');
  requireThat(!artifacts.some(a=>a.name===INTENT),'Existing readiness intent requires correlation, not replacement');
  const target={id:'python-wrapper',repository:'cwru-non-academic/Python_WSS_Integration_Lib'};
  const source={...auth.source,reviewed_commit:auth.source.commit,commit:reader.head(target.repository,auth.channel)};
  const request=createSnapshotRequest({schema:'wss-test-variable-readiness-request/v1',channel:auth.channel,controller:auth.controller,target,source,nonce:randomBytes(32).toString('hex')});
  verifySnapshotSource(reader,request);
  return {schema:'wss-test-variable-readiness-intent/v1',request};
}
function artifactDocument(g,auth,name,file) {
  const a=g.artifact(CORE,auth.controller.run_id,name);
  requireThat(a.expired===false&&String(a.workflow_run?.id)===auth.controller.run_id&&a.workflow_run?.head_sha===auth.controller.commit,'Foreign/expired readiness artifact');
  const first=g.get(`repos/${CORE}/actions/runs/${auth.controller.run_id}/attempts/1`);
  requireThat(first.run_attempt===1&&String(first.id)===auth.controller.run_id&&first.repository?.full_name===CORE&&first.head_repository?.full_name===CORE&&first.head_repository?.fork===false&&first.event==='workflow_dispatch'&&first.path===WORKFLOW&&first.head_sha===auth.controller.commit&&first.head_branch===auth.channel,'Readiness artifact first attempt mismatch');
  const created=Date.parse(a.created_at),start=Date.parse(first.run_started_at),end=Date.parse(first.updated_at);
  requireThat(Number.isFinite(created)&&Number.isFinite(start)&&created>=start&&(auth.attempt===1||(first.status==='completed'&&Number.isFinite(end)&&created<=end)),'Readiness artifact was not created on initial attempt');
  const download=g.downloadVerified||((repository,artifact,files)=>downloadVerifiedJsonArtifact(g,repository,artifact,files));
  return JSON.parse(download(CORE,a,[file])[file]);
}
export function loadReadinessIntent(g,auth) {
  const d=artifactDocument(g,auth,INTENT,'intent.json');
  requireThat(d.schema==='wss-test-variable-readiness-intent/v1'&&same(Object.keys(d).sort(),['request','schema']),'Invalid readiness intent');
  const r=validateSnapshotRequest(d.request);
  requireThat(r.channel===auth.channel&&same(r.controller,auth.controller)&&same(r.source,{...auth.source,reviewed_commit:auth.source.commit,commit:r.source.commit}),'Readiness intent authority mismatch');
  return d;
}
export function dispatchReadiness(writer,d,auth,env=process.env) {
  requireThat(auth.attempt===1&&env.GITHUB_RUN_ATTEMPT==='1'&&env.GITHUB_RUN_ID===auth.controller.run_id&&env.RUNNER_TEMP,'Readiness dispatch requires initial actual runner');
  requireThat(d.schema==='wss-test-variable-readiness-intent/v1'&&same(d.request.controller,auth.controller),'Wrong readiness dispatch intent');
  const r=validateSnapshotRequest(d.request);
  writeFileSync(join(env.RUNNER_TEMP,`wss-readiness-dispatch-${auth.controller.run_id}-a1.json`),canonical(d)+'\n',{flag:'wx'});
  return dispatchSnapshotOnce(writer,r,{newIntent:true,authenticatedIntent:r,dispatch:(repository,workflow,body)=>writer.command(['api','--method','POST',`repos/${repository}/actions/workflows/${workflow}/dispatches`,'-f',`ref=${body.ref}`,'-f',`inputs[request]=${body.inputs.request}`])});
}
export function collectReadiness(reader,d,auth) {
  requireThat(same(d.request.controller,auth.controller),'Wrong readiness collection authority');
  const capture=collectLocalTestVariables(reader,d.request);
  requireThat(reader.head(d.request.target.repository,auth.channel)===d.request.source.commit,'Readiness receiver moved before report');
  return {schema:'wss-test-variable-readiness-report/v1',purpose:'readiness-only-not-campaign-evidence',request:d.request,capture};
}
export function loadReadinessReport(g,reader,auth,d) {
  const saved=artifactDocument(g,auth,REPORT,'report.json');
  requireThat(same(saved,collectReadiness(reader,d,auth)),'Readiness report differs from authenticated local capture');
  return saved;
}
// Recovery is read-only correlation of the original request and capture. A missing
// initial report is not permission to republish, redispatch or read current vars.
export function correlateReadiness(g,reader,auth,d) {
  const reports=g.pages(`repos/${CORE}/actions/runs/${auth.controller.run_id}/artifacts`,'artifacts').filter(a=>a.name===REPORT);
  requireThat(reports.length<=1,'Duplicate readiness reports');
  return reports.length?loadReadinessReport(g,reader,auth,d):collectReadiness(reader,d,auth);
}
if(isEntrypoint(import.meta.url)) {
  const [operation,output,...extra]=process.argv.slice(2);
  requireThat(['prepare','dispatch','collect','load','correlate'].includes(operation)&&output&&!extra.length,'Expected readiness operation output');
  const g=new GitHub(),auth=readinessAuthority(g);
  requireThat(process.env.WSS_READINESS_TOKEN,'Existing narrowly scoped App Actions/Contents token required');
  const reader=new GitHub(process.env.WSS_READINESS_TOKEN);
  reader.downloadVerified=(repository,artifact,files)=>downloadVerifiedJsonArtifact(reader,repository,artifact,files);
  let value,file;
  if(operation==='prepare'){value=prepareReadiness(g,reader,auth);file='intent.json';}
  else {
    const d=loadReadinessIntent(g,auth);
    if(operation==='dispatch') {dispatchReadiness(reader,d,auth);}
    else if(operation==='correlate'){value=correlateReadiness(g,reader,auth,d);file='correlated-report.json';console.log(JSON.stringify({purpose:value.purpose,request_id:d.request.request_id,...value.capture.binding}));}
    else if(operation==='load'){value=loadReadinessReport(g,reader,auth,d);file='verified-report.json';}
    else {
      requireThat(auth.attempt===1,'Recovery must load the existing readiness report, never republish');
      let ready=false;
      for(let i=0;i<60&&!ready;i++) {
        const runs=matchingSnapshotRuns(reader,d.request);
        if(runs.length){const run=verifySnapshotRun(reader,d.request,reader.run(d.request.target.repository,String(runs[0].id)));if(run.status==='completed'){requireThat(run.conclusion==='success','Readiness capture failed');ready=true;}}
        if(!ready)await new Promise(resolve=>setTimeout(resolve,15000));
      }
      requireThat(ready,'Readiness timed out; correlate without redispatch');
      value=collectReadiness(reader,d,auth);file='report.json';
    }
  }
  if(file){mkdirSync(output,{recursive:true});writeFileSync(join(output,file),canonical(value)+'\n',{flag:'wx'});}
}
