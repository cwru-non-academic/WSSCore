// Ordinary CI snapshots repository settings once. A current, selection-enabled
// product campaign must reuse its authenticated lock instead of resolving latest.
import {prepareCiContext,loadCiContext,loadVerifiedCiContext} from './ci-test-context-artifact.mjs';
import {resolveTestSelection,verifyTestSelection} from './test-selection.mjs';
import {normalizeEffectiveTestVariables} from '../release/local-test-variable-snapshot.mjs';
import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {GitHub} from '../release/github.mjs';
import {testSelectionPolicy} from '../release/campaign-test-selection.mjs';
import {inspectSelectedTestCheckouts,writeSelectedTestEvidence} from './selected-test-source.mjs';
import {loadCampaignTestBinding,loadCampaignTestAuthority} from './selected-test-source.mjs';
import {candidate,canonical,CORE,digest,requireThat} from '../release/verify.mjs';
import {channelContext} from '../release/channels.mjs';
import {createCiExecutorHandoff,verifyCiExecutorHandoff,assertNoExecutorBootstrapCredentials} from './ci-test-executor-handoff.mjs';
const sha=v=>typeof v==='string'&&/^[a-f0-9]{40}$/.test(v);
export async function selectCiTests({channel,targetId,policy,variables={},run,productCandidate=null,mode='committed'},g,{loadCampaign=loadCampaignTestBinding}={}) {
  const context=channelContext(channel);
  requireThat(['committed','candidate'].includes(mode)&&policy&&Array.isArray(policy.targets),'Invalid CI test selection mode/policy');
  const matches=policy.targets.filter(t=>t.id===targetId);
  requireThat(matches.length===1,'Missing or duplicate test target policy');
  const target=matches[0];
  requireThat(run&&run.repository===target.repository&&sha(run.commit)&&typeof run.run_id==='string'&&/^[1-9][0-9]*$/.test(run.run_id)&&['pull_request','push','workflow_dispatch'].includes(run.event),'CI test selection must bind the actual target run/source');
  if(mode==='candidate')requireThat(productCandidate&&run.event==='workflow_dispatch','Candidate tests require the explicit canonical producer and dispatch');
  if(productCandidate)candidate(productCandidate);
  // PR checks validate proposed product source independently of an old installed
  // payload's campaign. Actual candidate and committed current-producer runs use
  // the frozen campaign selection whenever the producer policy requires it.
  if(productCandidate&&run.event!=='pull_request'&&(mode==='candidate'||g.head(CORE,channel)===productCandidate.commit)) {
    const file=g.get(`repos/${CORE}/contents/${context.targetPolicy}?ref=${productCandidate.commit}`);
    requireThat(file?.type==='file'&&file.encoding==='base64'&&typeof file.content==='string','Could not read exact producer test policy');
    const manifest=JSON.parse(Buffer.from(file.content,'base64').toString('utf8'));
    requireThat(manifest.channel===channel,'Producer test policy channel mismatch');
    if(Object.hasOwn(manifest,'test_selection_policy')) {
      const binding=await loadCampaign(g,{kind:'acceptance',runId:productCandidate.run_id,channel,targetId,expectedCandidate:productCandidate});
      requireThat(binding&&binding.selection.targets[targetId].repository===run.repository,'Campaign selected another target');
      return {scope:'campaign',...binding};
    }
  }
  const request={channel,campaign:{repository:run.repository,commit:run.commit,run_id:run.run_id},targets:[{...target,variables:typeof variables==='function'?variables():variables}]};
  const selection=resolveTestSelection(request,g);
  return {scope:'run',selection,request,selection_id:selection.selection_id};
}

export async function verifyCiTestBinding(value,policy,targetId,run,expectedId,g,{loadCampaign=loadCampaignTestBinding,expectedCandidate}={}) {
  requireThat(value&&['run','campaign'].includes(value.scope)&&/^[a-f0-9]{64}$/.test(expectedId)&&value.selection_id===expectedId,'CI test context differs from trusted prerequisite output');
  const targets=policy.targets.filter(t=>t.id===targetId);
  requireThat(targets.length===1&&targets[0].repository===run.repository,'CI target policy differs from actual run');
  if(value.scope==='campaign') {
    const binding=await loadCampaign(g,{kind:'acceptance',runId:value.selection.campaign.run_id,channel:value.selection.channel,targetId,expectedCandidate});
    requireThat(binding.selection_id===expectedId&&canonical(binding.selection)===canonical(value.selection),'CI context differs from authenticated frozen campaign');
    return binding;
  }
  const request={channel:value.selection.channel,campaign:{repository:run.repository,commit:run.commit,run_id:run.run_id},targets:[{...targets[0],variables:{}}]};
  verifyTestSelection(value.selection,request,expectedId);
  return {selection:value.selection,request,selection_id:expectedId};
}
// Authenticated prerequisite only. Recovery revalidates the durable first-attempt
// envelope and canonical campaign before issuing a new current-attempt handoff.
export async function loadCiExecutorHandoff(g,input,{env=process.env}={}) {
  let campaignAuthority=null;
  const verify=async value=>{
    requireThat(value.selection.channel===input.channel,'Durable CI channel mismatch');
    requireThat(value.scope!=='campaign'||input.productCandidate,'Durable campaign context requires the exact product candidate');
    return verifyCiTestBinding(value,input.policy,input.targetId,input.run,value.selection_id,g,{
      expectedCandidate:input.productCandidate||undefined,
      loadCampaign:async(client,options)=>{
        const result=await loadCampaignTestAuthority(client,options);
        campaignAuthority=result.authority;return result.binding;
      }
    });
  };
  const saved=await loadVerifiedCiContext(g,input,{env,verify});
  const handoff=createCiExecutorHandoff(saved,campaignAuthority,env),handoff_sha256=digest(handoff);
  verifyCiExecutorHandoff(handoff,input.policy,input.targetId,input.run,handoff.context.selection_id,handoff_sha256,{env});
  return {handoff,handoff_sha256};
}
if(isEntrypoint(import.meta.url)) {
  const [operation,targetId,first,second,third,...rest]=process.argv.slice(2);
  requireThat(['select','load','load-executor','inspect','evidence','inspect-executor','evidence-executor'].includes(operation)&&targetId&&first&&second&&rest.length===0,'Usage: ci-test-selection.mjs select|load|load-executor <target> <candidate.json|-> <output-dir> [committed|candidate] OR inspect|evidence|inspect-executor|evidence-executor <target> <context.json> <roots.json> [evidence.json]');
  requireThat(process.env.GITHUB_ACTIONS==='true'&&sha(process.env.GITHUB_SHA)&&/^[1-9][0-9]*$/.test(process.env.GITHUB_RUN_ID||''),'CI selection requires authenticated runner identity');
  const policy=testSelectionPolicy({test_selection_policy:JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url)))});
  const run={repository:process.env.GITHUB_REPOSITORY,commit:process.env.GITHUB_SHA,run_id:process.env.GITHUB_RUN_ID,event:process.env.GITHUB_EVENT_NAME};
  const channel=channelContext(process.env.WSS_CI_CHANNEL).channel;
  if(['select','load','load-executor'].includes(operation)) {
    const g=new GitHub();
    const raw=first==='-'?null:JSON.parse(readFileSync(first,'utf8').replace(/^\uFEFF/,''));
    const productCandidate=raw?(raw.candidate||raw):null;
    // Named effective vars are supplied by the reviewed repository-local resolver
    // job, with no environment declaration. Missing opt-in is not an unset pin.
    // Deferred callback preserves campaign and durable-context recovery without reread.
    const variables=()=>{requireThat(process.env.WSS_LOCAL_TEST_VARIABLES_SCHEMA==='wss-effective-test-variables/v1','Reviewed local effective-variable capture is unavailable');return normalizeEffectiveTestVariables(process.env);};
    const input={channel,targetId,policy,variables,run,productCandidate,mode:third||'committed',variableSemantics:'effective-org-repository/no-environment/v1'};
    const verify=value=>{requireThat(value.selection.channel===channel,'Durable CI channel mismatch');requireThat(value.scope!=='campaign'||productCandidate,'Durable campaign context requires the exact product candidate');return verifyCiTestBinding(value,policy,targetId,run,value.selection_id,g,{expectedCandidate:productCandidate||undefined});};
    requireThat(process.env.GITHUB_OUTPUT,'CI selection requires runner output context');
    if(operation==='select') {
      const prepared=await prepareCiContext(g,input,{select:()=>selectCiTests(input,g),verify});
      mkdirSync(second,{recursive:true});writeFileSync(join(second,'context.json'),canonical(prepared.document)+'\n',{flag:'wx'});
      appendFileSync(process.env.GITHUB_OUTPUT,`created=${prepared.created}\nartifact_name=${prepared.artifact_name}\n`);
      console.log(JSON.stringify({created:prepared.created,artifact_name:prepared.artifact_name}));
    } else if(operation==='load-executor') {
      const {handoff,handoff_sha256}=await loadCiExecutorHandoff(g,input);
      mkdirSync(second,{recursive:true});writeFileSync(join(second,'executor-handoff.json'),canonical(handoff)+'\n',{flag:'wx'});
      const chosen=handoff.context.selection.targets[targetId];
      appendFileSync(process.env.GITHUB_OUTPUT,`executor_handoff_sha256=${handoff_sha256}\nscope=${handoff.context.scope}\nselection_id=${handoff.context.selection_id}\ntarget_id=${targetId}\ntest_repository=${chosen.tests.repository}\ntest_commit=${chosen.tests.commit}\n`);
      console.log(JSON.stringify({scope:handoff.context.scope,selection_id:handoff.context.selection_id,executor_handoff_sha256:handoff_sha256,test_repository:chosen.tests.repository,test_commit:chosen.tests.commit}));
    } else {
      const result=await loadCiContext(g,input,{verify});
      mkdirSync(second,{recursive:true});writeFileSync(join(second,'context.json'),canonical(result)+'\n',{flag:'wx'});
      const chosen=result.selection.targets[targetId];
      requireThat(process.env.GITHUB_OUTPUT,'CI selection requires runner output context');
      appendFileSync(process.env.GITHUB_OUTPUT,`scope=${result.scope}\nselection_id=${result.selection_id}\ntarget_id=${targetId}\ntest_repository=${chosen.tests.repository}\ntest_commit=${chosen.tests.commit}\n`);
      console.log(JSON.stringify({scope:result.scope,selection_id:result.selection_id,test_repository:chosen.tests.repository,test_commit:chosen.tests.commit}));
    }
  } else {
    const offline=operation.endsWith('-executor'),evidence=operation==='evidence'||operation==='evidence-executor';
    requireThat(evidence?Boolean(third):!third,'Invalid selected-test evidence output arguments');
    const value=JSON.parse(readFileSync(first,'utf8'));
    let binding;
    if(offline) {
      assertNoExecutorBootstrapCredentials();
      binding=verifyCiExecutorHandoff(value,policy,targetId,run,process.env.WSS_TEST_SELECTION_ID,process.env.WSS_TEST_EXECUTOR_HANDOFF_SHA256);
    } else {
      requireThat(value.selection.channel===channel,'CI selection channel differs from runner');
      binding=await verifyCiTestBinding(value,policy,targetId,run,process.env.WSS_TEST_SELECTION_ID,new GitHub());
    }
    const roots=JSON.parse(readFileSync(second,'utf8').replace(/^\uFEFF/,''));
    const result=!evidence?inspectSelectedTestCheckouts(binding,targetId,roots,run.commit).evidence:writeSelectedTestEvidence(binding,targetId,roots,run.commit,third);
    console.log(JSON.stringify(result));
  }
}
