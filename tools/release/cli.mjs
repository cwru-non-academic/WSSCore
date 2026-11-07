import {freezeCampaign} from './campaign-freeze.mjs';
import {testSelectionPolicy} from './campaign-test-selection.mjs';
import {prepareProducerTestSelection,loadProducerTestSelection} from './campaign-selection-artifact.mjs';
import {collectCampaignVariableSnapshots} from './campaign-variable-snapshots.mjs';
import {appendFileSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {GitHub,archive,collect,freeze,loadPlan,publish} from './github.mjs';
import {execute,journalName,prepare} from './dispatch.mjs';
import {CORE,digest,requireThat} from './verify.mjs';
import {tagPolicy} from './policy.mjs';
import {assertWorkflows,collectWithWorkflows,freezeWorkflows,guardedWriter} from '../ci/workflow-freeze.mjs';
import {channelContext} from './channels.mjs';
import {channelActivation,controllerContext,publicationControls} from '../ci/channel-controls.mjs';

const [command,...args]=process.argv.slice(2);
const g=new GitHub();
const activation=channelActivation(JSON.parse(readFileSync(new URL('../ci/channel-activation.json',import.meta.url))));
const selected=p=>{requireThat(p.channel===channelContext(process.env.WSS_CI_CHANNEL||'dev').channel,'Producer plan belongs to another CI channel');if(p.channel==='main')requireThat(process.env.GITHUB_SHA===p.candidate.commit,'Main controller source differs from the exact main candidate');return p;};
try {
  if(['prepare-dispatches','dispatch','verify','publish'].includes(command))controllerContext(process.env,command,CORE,activation);
  if(command==='inspect-tag-guard') {
    requireThat(args.length===1 && /^[1-9][0-9]*$/.test(args[0]),'inspect-tag-guard requires a ruleset ID');
    const policy=tagPolicy(g.get(`repos/${CORE}/rulesets/${args[0]}`));
    console.log(JSON.stringify({ruleset_id:args[0],policy_sha256:digest(policy),policy},null,2));
  } else if(command==='prepare-test-selection') {
    requireThat(args.length===3,'prepare-test-selection requires candidate.json manifest.json output directory');
    const [candidateFile,manifestFile,destination]=args;
    const c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(manifestFile)),context=channelContext(m.channel);
    requireThat(activation[context.channel]===true&&testSelectionPolicy(m),'Test selection policy must be explicitly activated by the producer');
    const policy=JSON.parse(readFileSync(new URL('../ci/'+context.channel+'-propagation.json',import.meta.url)));
    const prepared=prepareProducerTestSelection(g,m,policy,c,{resolveGitHub:new GitHub(process.env.WSS_TEST_SNAPSHOT_READ_TOKEN),resolveSnapshots:targets=>{
      return collectCampaignVariableSnapshots(g,c,m.channel,targets);
    }});
    if(prepared.created){mkdirSync(destination,{recursive:true});writeFileSync(join(destination,'selection.json'),JSON.stringify(prepared.document,null,2)+'\n',{flag:'wx'});}
    requireThat(process.env.GITHUB_OUTPUT,'Producer selection requires runner output context');
    appendFileSync(process.env.GITHUB_OUTPUT,`created=${prepared.created}\nselection_id=${prepared.selection.selection_id}\n`);
    console.log(`Authenticated producer test selection ${prepared.selection.selection_id}; created=${prepared.created}`);
  } else if(command==='freeze') {
    const [candidateFile,manifestFile,destination]=args;
    requireThat(args.length===3,'freeze requires candidate.json manifest.json output directory');
    const c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(manifestFile));
    requireThat(m.channel==='dev','Main requires the atomic paired freeze command');
    requireThat(!testSelectionPolicy(m),'Selected tests require paired freeze with the durable artifact');
    archive(g,c);
    const p=freezeWorkflows(g,freeze(g,m,c,{verificationOnly:process.env[publicationControls()[m.channel]]!=='true'}),'acceptance');
    mkdirSync(destination,{recursive:true});writeFileSync(join(destination,'plan.json'),JSON.stringify(p,null,2)+'\n');
    console.log(`Frozen plan ${p.plan_id}`);
  } else if(command==='freeze-campaign') {
    requireThat(args.length===4,'freeze-campaign requires candidate.json channel manifest and two output directories');
    const [candidateFile,manifestFile,acceptanceDirectory,stageDirectory]=args;
    const c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(manifestFile)),context=channelContext(m.channel);
    requireThat(activation[context.channel]===true&&process.env.GITHUB_ACTIONS==='true'&&process.env.GITHUB_REPOSITORY===CORE&&process.env.GITHUB_REF===context.ref&&process.env.GITHUB_EVENT_NAME==='push'&&process.env.GITHUB_WORKFLOW_REF===`${CORE}/.github/workflows/artifacts.yml@${context.ref}`&&process.env.GITHUB_WORKFLOW_SHA===process.env.GITHUB_SHA&&c.commit===process.env.GITHUB_SHA&&c.run_id===process.env.GITHUB_RUN_ID,'Paired freeze requires this activated exact canonical channel push producer');
    archive(g,c);
    const stagePolicy=JSON.parse(readFileSync(new URL('../ci/'+context.channel+'-propagation.json',import.meta.url)));
    const testSelection=testSelectionPolicy(m)?loadProducerTestSelection(g,m,stagePolicy,c):undefined;
    const pair=freezeCampaign(g,m,stagePolicy,c,{testSelection,verificationOnly:process.env[publicationControls()[m.channel]]!=='true'});
    // No artifact is emitted and no initial handoff occurs unless BOTH plans
    // pass the shared observation/recheck boundary above.
    for(const [directory,p] of [[acceptanceDirectory,pair.acceptance],[stageDirectory,pair.stage]]){
      mkdirSync(directory,{recursive:true});writeFileSync(join(directory,'plan.json'),JSON.stringify(p,null,2)+'\n');
    }
    console.log(`Frozen ${context.channel} campaign ${pair.acceptance.plan_id} / ${pair.stage.plan_id}`);
  } else if(command==='prepare-dispatches') {
    requireThat(args.length===2,'prepare-dispatches requires producer run ID and output directory');
    requireThat(process.env.WSS_DEV_ACCEPTANCE_ENABLED==='true','Acceptance reconciliation is not enabled');
    const p=selected(loadPlan(g,args[0])),j=prepare(g,p,process.env.GITHUB_RUN_ID,Number(process.env.GITHUB_RUN_ATTEMPT));
    mkdirSync(args[1],{recursive:true});writeFileSync(join(args[1],'intent.json'),JSON.stringify(j,null,2)+'\n');
    appendFileSync(process.env.GITHUB_OUTPUT,`artifact_name=${journalName(p,j.run_id,j.attempt)}\n`);
  } else if(command==='dispatch') {
    requireThat(args.length===2,'dispatch requires producer run ID and saved intent.json');
    requireThat(process.env.WSS_DEV_ACCEPTANCE_ENABLED==='true' && process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main','Dispatch runs only enabled trusted default-branch workflow');
    requireThat(process.env.WSS_JOURNAL_READ_TOKEN,'Missing read-only journal token');
    const reader=new GitHub(process.env.WSS_JOURNAL_READ_TOKEN),p=selected(loadPlan(reader,args[0])),j=JSON.parse(readFileSync(args[1]));
    requireThat(j.run_id===process.env.GITHUB_RUN_ID && j.attempt===Number(process.env.GITHUB_RUN_ATTEMPT),'Intent is not from this workflow attempt');
    execute(g,reader,p,j);
  } else if(command==='verify' || command==='publish') {
    const [run,destination]=args;
    requireThat(args.length===2,'verify/publish requires producer run ID and output directory');
    if(command==='publish') requireThat(process.env.WSS_EVIDENCE_READ_TOKEN,'Publication requires a separate read-only evidence token');
    const reader=command==='publish'?new GitHub(process.env.WSS_EVIDENCE_READ_TOKEN):g;
    const p=selected(loadPlan(reader,run)),accepted=collectWithWorkflows(reader,p,collect);
    mkdirSync(destination,{recursive:true});
    writeFileSync(join(destination,'acceptance.json'),JSON.stringify(accepted.record,null,2)+'\n');
    if(command==='publish') {
      requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main','Publisher runs only trusted default-branch workflow');
      assertWorkflows(reader,p,'acceptance');
      const published=publish(guardedWriter(g,reader,p,'acceptance',{purpose:'publication',...(p.channel==='main'?{acceptedHeads:Object.fromEntries(Object.entries(accepted.record.targets).map(([id,t])=>[id,t.merge.sha]))}:{})}),p,accepted,resolve(destination),reader);
      assertWorkflows(reader,p,'acceptance');
      console.log(JSON.stringify(published));
    } else console.log(`All nine targets accepted plan ${p.plan_id}; no publication requested`);
  } else throw new Error('Expected inspect-tag-guard, freeze, prepare-dispatches, dispatch, verify or publish');
} catch(error) { console.error(error.message); process.exitCode=1; }
