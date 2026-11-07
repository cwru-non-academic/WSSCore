import {randomBytes} from 'node:crypto';
import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {GitHub} from './github.mjs';
import {downloadVerifiedJsonArtifact} from './verified-json-artifact.mjs';
import {CORE,canonical,requireThat} from './verify.mjs';
import {testSelectionPolicy} from './campaign-test-selection.mjs';
import {channelContext} from './channels.mjs';
import {createSnapshotRequest,validateSnapshotRequest,dispatchSnapshotOnce,collectLocalTestVariables,matchingSnapshotRuns,verifySnapshotRun,verifySnapshotSource} from './local-test-variable-snapshot.mjs';
import {isEntrypoint} from './entrypoint.mjs';
export const INTENT_NAME='wss-test-variable-intents';
const same=(a,b)=>canonical(a)===canonical(b);
const SOURCE_PATH='tools/ci/test-variable-snapshot-sources.json';
export function snapshotProducerAuthority(g,c,channel,env=process.env) {
  const context=channelContext(channel);
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===CORE&&env.GITHUB_EVENT_NAME==='push'&&env.GITHUB_REF===context.ref&&env.GITHUB_SHA===c.commit&&env.GITHUB_WORKFLOW_SHA===c.commit&&env.GITHUB_WORKFLOW_REF===`${CORE}/.github/workflows/artifacts.yml@${context.ref}`&&env.GITHUB_RUN_ID===c.run_id&&/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ATTEMPT),'Canonical snapshot producer required');
  const run=g.run(CORE,c.run_id);
  requireThat(run.repository?.full_name===CORE&&run.head_repository?.full_name===CORE&&run.event==='push'&&run.path==='.github/workflows/artifacts.yml'&&run.head_sha===c.commit&&run.head_branch===channel&&run.run_attempt===Number(env.GITHUB_RUN_ATTEMPT)&&run.status==='in_progress','Snapshot producer metadata mismatch');
  return run;
}
function sourcePolicy(g,c,channel,targets) {
  const f=g.get(`repos/${CORE}/contents/${SOURCE_PATH}?ref=${c.commit}`);
  requireThat(f?.type==='file'&&f.encoding==='base64','Missing immutable snapshot source registry');
  const sources=JSON.parse(Buffer.from(f.content,'base64').toString('utf8'));
  requireThat(sources.schema==='wss-local-test-variable-sources/v1'&&sources[channel]&&same(Object.keys(sources[channel]).sort(),targets.map(t=>t.id).sort()),'Snapshot deployment registry incomplete; selection remains blocked');
  return sources[channel];
}
export function loadSnapshotIntents(g,c,channel,targets) {
  const a=g.artifact(CORE,c.run_id,INTENT_NAME);
  requireThat(a.expired===false&&String(a.workflow_run?.id)===c.run_id&&a.workflow_run?.head_sha===c.commit,'Foreign/expired snapshot intents');
  const d=JSON.parse(downloadVerifiedJsonArtifact(g,CORE,a,['intents.json'])['intents.json']);
  requireThat(d.schema==='wss-test-variable-intents/v1'&&Array.isArray(d.requests)&&d.requests.length===targets.length,'Malformed snapshot intents');
  const sources=sourcePolicy(g,c,channel,targets);
  for(let i=0;i<targets.length;i++) {
    const r=validateSnapshotRequest(d.requests[i]),t=targets[i];
    requireThat(r.channel===channel&&same(r.producer,{repository:CORE,commit:c.commit,run_id:c.run_id,attempt:1})&&same(r.target,{id:t.id,repository:t.repository})&&same(r.source,{...sources[t.id],reviewed_commit:sources[t.id].commit,commit:r.source.commit}),'Snapshot intent target/source/producer mismatch');
  }
  return d;
}
export function collectCampaignVariableSnapshots(g,c,channel,targets) {
  snapshotProducerAuthority(g,c,channel);
  const d=loadSnapshotIntents(g,c,channel,targets),result={},captures={};
  requireThat(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN,'Existing Actions-read snapshot token required');
  const reader=new GitHub(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN);
  reader.downloadVerified=(repository,artifact,files)=>downloadVerifiedJsonArtifact(reader,repository,artifact,files);
  for(const r of d.requests){const capture=collectLocalTestVariables(reader,r);result[r.target.id]=capture.settings;captures[r.target.id]={request:r,...capture};}
  return {schema:'wss-authenticated-effective-variables/v1',variables:result,captures};
}
// Trusted canonical first-attempt job only, not an arbitrary-client exactly-once
// service. Durable intents prohibit prepare/recovery replays; this fixed local
// marker prevents repeating the initial command, even with another output path.
export function dispatchPreparedSnapshotIntents(writer,d,c,env) {
  requireThat(env.GITHUB_RUN_ATTEMPT==='1'&&env.GITHUB_RUN_ID===c.run_id&&env.RUNNER_TEMP,'Snapshot dispatch requires initial actual runner identity');
  requireThat(d.schema==='wss-test-variable-intents/v1'&&Array.isArray(d.requests)&&d.requests.length>0,'Invalid durable dispatch intents');
  for(const r of d.requests){validateSnapshotRequest(r);requireThat(r.producer.commit===c.commit&&r.producer.run_id===c.run_id,'Wrong dispatch producer');}
  writeFileSync(join(env.RUNNER_TEMP,`wss-snapshot-dispatch-${c.run_id}-a1.json`),canonical(d)+'\n',{flag:'wx'});
  for(const r of d.requests)dispatchSnapshotOnce(writer,r,{newIntent:true,authenticatedIntent:r,dispatch:(repository,workflow,body)=>writer.command(['api','--method','POST',`repos/${repository}/actions/workflows/${workflow}/dispatches`,'-f',`ref=${body.ref}`,'-f',`inputs[request]=${body.inputs.request}`])});
}
if(isEntrypoint(import.meta.url)) {
  const [operation,candidateFile,manifestFile,output]=process.argv.slice(2);
  requireThat(['prepare','dispatch','collect','wait'].includes(operation)&&candidateFile&&manifestFile&&output,'Expected snapshot operation candidate manifest output');
  const g=new GitHub(),c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(manifestFile)),policy=testSelectionPolicy(m);
  requireThat(policy,'Snapshot requests require enabled reviewed selection policy');
  snapshotProducerAuthority(g,c,m.channel);
  const manifest=g.get(`repos/${CORE}/contents/${channelContext(m.channel).targetPolicy}?ref=${c.commit}`);
  requireThat(manifest.type==='file'&&manifest.encoding==='base64'&&same(JSON.parse(Buffer.from(manifest.content,'base64')),m),'Snapshot target policy is not immutable producer policy');
  if(operation==='prepare') {
    requireThat(process.env.GITHUB_RUN_ATTEMPT==='1','New settings requests forbidden on recovery');
    const found=g.pages(`repos/${CORE}/actions/runs/${c.run_id}/artifacts`,'artifacts').filter(a=>a.name===INTENT_NAME);
    requireThat(found.length===0,'Existing snapshot intents require correlation, not replacement or redispatch');
    const sources=sourcePolicy(g,c,m.channel,policy.targets);
    requireThat(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN,'Existing Actions/Contents-read source token required');
    const reader=new GitHub(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN);
    const requests=policy.targets.map(t=>{
      const source={...sources[t.id],reviewed_commit:sources[t.id].commit,commit:reader.head(t.repository,m.channel)};
      const r=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel:m.channel,producer:{repository:CORE,commit:c.commit,run_id:c.run_id,attempt:1},target:{id:t.id,repository:t.repository},source,nonce:randomBytes(32).toString('hex')});
      verifySnapshotSource(reader,r);return r;
    });
    mkdirSync(output,{recursive:true});writeFileSync(join(output,'intents.json'),canonical({schema:'wss-test-variable-intents/v1',requests})+'\n',{flag:'wx'});
  } else if(operation==='dispatch') {
    requireThat(process.env.GITHUB_RUN_ATTEMPT==='1','No dispatch on recovery');
    const d=loadSnapshotIntents(g,c,m.channel,policy.targets);
    const writer=new GitHub(process.env.WSS_TEST_SNAPSHOT_DISPATCH_TOKEN);
    requireThat(process.env.WSS_TEST_SNAPSHOT_DISPATCH_TOKEN,'Missing existing Actions-write dispatch token');
    dispatchPreparedSnapshotIntents(writer,d,c,process.env);
  } else {
    if(operation==='wait') {
      const d=loadSnapshotIntents(g,c,m.channel,policy.targets);
      requireThat(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN,'Existing Actions-read snapshot token required');
      const reader=new GitHub(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN);
      let ready=false;
      for(let attempt=0;attempt<60&&!ready;attempt++) {
        ready=d.requests.every(r=>{const runs=matchingSnapshotRuns(reader,r);if(!runs.length)return false;const run=verifySnapshotRun(reader,r,reader.run(r.target.repository,String(runs[0].id)));if(run.status!=='completed')return false;requireThat(run.conclusion==='success','Snapshot workflow failed');return true;});
        if(!ready)await new Promise(resolve=>setTimeout(resolve,15000));
      }
      requireThat(ready,'Snapshot collection timed out; never redispatch');
    }
    const snapshots=collectCampaignVariableSnapshots(g,c,m.channel,policy.targets);
    mkdirSync(output,{recursive:true});writeFileSync(join(output,'snapshots.json'),canonical(snapshots)+'\n',{flag:'wx'});
  }
}
