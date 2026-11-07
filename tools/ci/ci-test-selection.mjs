// Ordinary CI snapshots repository settings once. A current, selection-enabled
// product campaign must reuse its authenticated lock instead of resolving latest.
import {resolveTestSelection,verifyTestSelection} from './test-selection.mjs';
import {readRepositoryTestVariables} from './repository-test-variables.mjs';
import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {GitHub} from '../release/github.mjs';
import {testSelectionPolicy} from '../release/campaign-test-selection.mjs';
import {inspectSelectedTestCheckouts,writeSelectedTestEvidence} from './selected-test-source.mjs';
import {loadCampaignTestBinding} from './selected-test-source.mjs';
import {candidate,canonical,CORE,requireThat} from '../release/verify.mjs';
import {channelContext} from '../release/channels.mjs';
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

export async function verifyCiTestBinding(value,policy,targetId,run,expectedId,g,{loadCampaign=loadCampaignTestBinding}={}) {
  requireThat(value&&['run','campaign'].includes(value.scope)&&/^[a-f0-9]{64}$/.test(expectedId)&&value.selection_id===expectedId,'CI test context differs from trusted prerequisite output');
  const targets=policy.targets.filter(t=>t.id===targetId);
  requireThat(targets.length===1&&targets[0].repository===run.repository,'CI target policy differs from actual run');
  if(value.scope==='campaign') {
    const binding=await loadCampaign(g,{kind:'acceptance',runId:value.selection.campaign.run_id,channel:value.selection.channel,targetId});
    requireThat(binding.selection_id===expectedId&&canonical(binding.selection)===canonical(value.selection),'CI context differs from authenticated frozen campaign');
    return binding;
  }
  const request={channel:value.selection.channel,campaign:{repository:run.repository,commit:run.commit,run_id:run.run_id},targets:[{...targets[0],variables:{}}]};
  verifyTestSelection(value.selection,request,expectedId);
  return {selection:value.selection,request,selection_id:expectedId};
}
if(isEntrypoint(import.meta.url)) {
  const [operation,targetId,first,second,third,...rest]=process.argv.slice(2);
  requireThat(['select','inspect','evidence'].includes(operation)&&targetId&&first&&second&&rest.length===0,'Usage: ci-test-selection.mjs select <target> <candidate.json|-> <output-dir> [committed|candidate] OR inspect|evidence <target> <context.json> <roots.json> [evidence.json]');
  requireThat(process.env.GITHUB_ACTIONS==='true'&&sha(process.env.GITHUB_SHA)&&/^[1-9][0-9]*$/.test(process.env.GITHUB_RUN_ID||''),'CI selection requires authenticated runner identity');
  const policy=testSelectionPolicy({test_selection_policy:JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url)))});
  const run={repository:process.env.GITHUB_REPOSITORY,commit:process.env.GITHUB_SHA,run_id:process.env.GITHUB_RUN_ID,event:process.env.GITHUB_EVENT_NAME};
  const channel=channelContext(process.env.WSS_CI_CHANNEL).channel,g=new GitHub();
  if(operation==='select') {
    const raw=first==='-'?null:JSON.parse(readFileSync(first,'utf8').replace(/^\uFEFF/,''));
    const productCandidate=raw?(raw.candidate||raw):null;
    // Read repository-only settings just like the producer; inherited org or
    // environment variables must not silently choose a different test source.
    const variables=()=>{requireThat(process.env.WSS_TEST_VARIABLE_READ_TOKEN,'Repository variable read token unavailable');return readRepositoryTestVariables(new GitHub(process.env.WSS_TEST_VARIABLE_READ_TOKEN),run.repository);};
    const result=await selectCiTests({channel,targetId,policy,variables,run,productCandidate,mode:third||'committed'},g);
    mkdirSync(second,{recursive:true});writeFileSync(join(second,'context.json'),canonical(result)+'\n',{flag:'wx'});
    const chosen=result.selection.targets[targetId];
    requireThat(process.env.GITHUB_OUTPUT,'CI selection requires runner output context');
    appendFileSync(process.env.GITHUB_OUTPUT,`scope=${result.scope}\nselection_id=${result.selection_id}\ntarget_id=${targetId}\ntest_repository=${chosen.tests.repository}\ntest_commit=${chosen.tests.commit}\n`);
    console.log(JSON.stringify({scope:result.scope,selection_id:result.selection_id,test_repository:chosen.tests.repository,test_commit:chosen.tests.commit}));
  } else {
    requireThat(operation==='evidence'?Boolean(third):!third,'Invalid selected-test evidence output arguments');
    const value=JSON.parse(readFileSync(first,'utf8'));
    requireThat(value.selection.channel===channel,'CI selection channel differs from runner');
    const binding=await verifyCiTestBinding(value,policy,targetId,run,process.env.WSS_TEST_SELECTION_ID,g);
    const roots=JSON.parse(readFileSync(second,'utf8').replace(/^\uFEFF/,''));
    const result=operation==='inspect'?inspectSelectedTestCheckouts(binding,targetId,roots,run.commit).evidence:writeSelectedTestEvidence(binding,targetId,roots,run.commit,third);
    console.log(JSON.stringify(result));
  }
}
