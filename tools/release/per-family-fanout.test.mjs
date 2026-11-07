import {resolveTestSelection} from '../ci/test-selection.mjs';
import {CAMPAIGN_TEST_POLICY,selectionRequest,selectionFields,targetTestEvidence,selectedTestArtifactName} from './campaign-test-selection.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {prepareStage,executeStage,assertFamilyBarrier} from '../ci/dev-propagation.mjs';
import {CORE,digest} from './verify.mjs';
import {freezeWorkflows} from '../ci/workflow-freeze.mjs';
import {channelContext,integrationConfiguration} from './channels.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
function fixture(perFamily=true,channel='dev'){
  const context=channelContext(channel);
  const policy=JSON.parse(readFileSync(new URL(`../ci/${channel}-propagation.json`,import.meta.url)));
  if(perFamily)policy.application_fanout='per-integration';else {delete policy.application_fanout;delete policy.workflow_snapshot_policy;}
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const receivers=Object.fromEntries(policy.libraries.map((t,i)=>[t.id,{heads:{main:sha(10+i),dev:sha(20+i)},trusted:Object.fromEntries(['main','dev'].map(branch=>[branch,{'.github/scripts':sha(5),...(channel==='main'?{[integrationConfiguration(t.id)]:sha(5),'.github/workflows':sha(5)}:{})}]))}]));
  // These stage-unit fixtures assume loadStage has already verified the pair;
  // paired artifact identity and freeze races have their own production tests.
  const body={schema:context.stagePlanSchema,...(channel==='main'?{channel,acceptance_plan_id:'a'.repeat(64)}:{}),candidate,policy,policy_sha256:digest(policy),receivers};
  const p={...body,plan_id:digest(body)},state={merged:new Set(channel==='dev'?['python']:[]),failed:new Set(),missing:new Set(),heads:{},saved:null},commands=[];
  const target=repo=>policy.libraries.find(t=>t.repository===repo);
  const head=t=>state.heads[t.id]||sha(30+policy.libraries.indexOf(t));
  const pr=t=>({number:1,merged:true,state:'closed',merged_at:'2026-01-01T00:00:00Z',merge_commit_sha:channel==='main'?sha(30+policy.libraries.indexOf(t)):head(t),user:{type:'Bot'},merged_by:{type:'User'},base:{ref:channel,repo:{full_name:t.repository}},head:{sha:sha(8),repo:{full_name:t.repository}}});
  const g={
    head:(repo,branch='dev')=>repo===CORE?candidate.commit:channel==='dev'?(branch==='main'?receivers[target(repo).id].heads.main:head(target(repo))):branch==='dev'?receivers[target(repo).id].heads.dev:state.merged.has(target(repo).id)?head(target(repo)):receivers[target(repo).id].heads.main,
    object:()=>({type:'tree',sha:sha(5)}),
    get(path){
      if(path===`repos/${CORE}/contents/${context.stagePolicy}?ref=${candidate.commit}`)return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(policy)).toString('base64')};
      const t=policy.libraries.find(t=>path.startsWith(`repos/${t.repository}/`));assert.ok(t,'Unexpected lookup '+path);
      if(path.includes('/contents/'))return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(state.merged.has(t.id)?{candidate}:{})).toString('base64')};
      if(path.endsWith('/pulls/1'))return pr(t);throw Error('Unexpected lookup '+path);
    },
    pages(path){
      if(path.startsWith(`repos/${CORE}/`))return [];
      const t=policy.libraries.find(t=>path.startsWith(`repos/${t.repository}/`));assert.ok(t,'Unexpected pages '+path);
      if(path.includes('/commits/'))return [pr(t)];
      if(path.includes(`/actions/workflows/${t.normal}/runs`))return state.missing.has(t.id)?[]:[{id:100+policy.libraries.indexOf(t),run_attempt:1,event:'push',created_at:'2026-01-01T00:01:00Z',repository:{full_name:t.repository},head_repository:{full_name:t.repository},head_sha:head(t),head_branch:channel,path:`.github/workflows/${t.normal}`,status:'completed',conclusion:state.failed.has(t.id)?'failure':'success'}];
      return [];
    },
    jobs:(repo)=>target(repo).jobs.map(name=>({name,status:'completed',conclusion:'success'})),
    download:()=>({'intent.json':JSON.stringify(state.saved)}),command:args=>commands.push(args)
  };
  const frozen=freezeWorkflows(g,p,'stage');if(channel==='main')state.merged.add('python');
  return {p:frozen,g,state,commands,python:policy.libraries.find(t=>t.id==='python')};
}
test('a ready integration authorizes only its linked applications before other integrations merge',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);
  assert.equal(j.barrier.schema,'wss-integration-barrier/v2');
  assert.deepEqual(Object.keys(j.barrier.integrations),['python']);
  assert.deepEqual(j.actions.map(a=>[a.target,a.phase]),[['python','applications']]);
});
test('a failed family does not delay another ready family or authorize its own applications',()=>{
  const f=fixture();f.state.merged.add('csharp');f.state.failed.add('csharp');
  const j=prepareStage(f.g,f.p,'9',1);
  assert.deepEqual(j.actions.map(a=>a.target),['python']);assert.equal(j.blocked[0].target,'csharp');
  f.state.failed.add('python');assert.equal(prepareStage(f.g,f.p,'9',1).actions.length,0);
});
test('a changed default-main nested workflow blocks only its own integration family',()=>{
  const f=fixture();f.state.merged.add('csharp');const t=f.p.policy.libraries.find(t=>t.id==='csharp'),head=f.g.head,object=f.g.object;
  f.g.head=(repo,branch='dev')=>repo===t.repository&&branch==='main'?sha(999):head(repo,branch);
  f.g.object=(repo,ref,path)=>repo===t.repository&&ref===sha(999)&&path==='.github/workflows'?{type:'tree',sha:sha(998)}:object(repo,ref,path);
  const j=prepareStage(f.g,f.p,'9',1);assert.deepEqual(j.actions.map(a=>a.target),['python']);assert.ok(j.blocked.some(b=>b.target==='csharp'&&b.reason.includes('Workflow tree changed')));
  f.state.saved=j;executeStage(f.g,f.g,f.p,j);assert.equal(f.commands.length,1);assert.ok(f.commands[0].some(x=>x.includes(f.python.repository)));
});
test('committed CI can be requested for one merged family while other integrations remain pending',()=>{
  const f=fixture();f.state.missing.add('python');const j=prepareStage(f.g,f.p,'9',1);
  assert.deepEqual(j.actions.map(a=>[a.target,a.phase]),[['python','normal-ci']]);assert.equal(j.barrier,undefined);
});
test('another family becoming ready does not invalidate saved authorization for an unchanged family',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;f.state.merged.add('unity');
  executeStage(f.g,f.g,f.p,j);assert.equal(f.commands.length,1);assert.ok(f.commands[0].some(x=>x.includes(f.python.repository)));
});
test('the authorized family moving or a foreign barrier cannot use saved authorization',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;f.state.heads.python=sha(99);
  assert.throws(()=>executeStage(f.g,f.g,f.p,j),/authorized barrier|Integration moved/);assert.equal(f.commands.length,0);
  for(const change of [{plan_id:'0'.repeat(64)},{schema:'wss-integration-barrier/v1'},{emitter:{run_id:'8',attempt:1,workflow:'.github/workflows/dev-propagation.yml'}}])assert.throws(()=>assertFamilyBarrier(f.p,{...j.barrier,...change},j.barrier,f.python,'9',1),/authorized barrier/);
});
test('older frozen all-integration plans keep their original barrier policy',()=>{
  const f=fixture(false),j=prepareStage(f.g,f.p,'9',1);assert.equal(j.state,'waiting-for-integrations');assert.equal(j.actions.length,0);
});
test('main runs its ready family from accepted merge M with frozen authority S',()=>{
  const f=fixture(true,'main'),j=prepareStage(f.g,f.p,'9',1),a=j.actions[0],accepted=j.barrier.integrations.python;
  assert.equal(j.barrier.schema,'wss-integration-barrier/v3');assert.equal(j.barrier.channel,'main');
  assert.deepEqual(Object.keys(j.barrier.integrations),['python']);
  assert.equal(a.ref,'main');assert.equal(a.source_sha,accepted.head_sha);
  assert.notEqual(a.source_sha,f.p.receivers.python.heads.main);
  assert.equal(a.authority_source_sha,f.p.receivers.python.heads.main);
  assert.equal(accepted.execution.source_sha,a.source_sha);
  assert.deepEqual(Object.keys(accepted.execution.trusted).sort(),['.github/python-ci.json','.github/scripts','.github/workflows'].sort());
  f.state.saved=j;executeStage(f.g,f.g,f.p,j);assert.equal(f.commands.length,1);
  assert.ok(f.commands[0].includes('inputs[channel]=main'));
});
test('main rejects old-source execution, changed authority, and a later identical-tooling head',()=>{
  for(const path of ['.github/scripts','.github/python-ci.json','.github/workflows']){
    const f=fixture(true,'main'),object=f.g.object;
    f.g.object=(repo,ref,p)=>repo===f.python.repository&&ref===f.g.head(repo,'main')&&p===path?{type:'tree',sha:sha(999)}:object(repo,ref,p);
    const j=prepareStage(f.g,f.p,'9',1);assert.equal(j.actions.length,0);assert.ok(j.blocked.some(x=>x.target==='python'));
  }
  const f=fixture(true,'main'),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;
  j.actions[0].source_sha=f.p.receivers.python.heads.main;
  assert.throws(()=>executeStage(f.g,f.g,f.p,j),/Invalid staged action/);assert.equal(f.commands.length,0);
  const later=fixture(true,'main'),saved=prepareStage(later.g,later.p,'9',1);later.state.saved=saved;later.state.heads.python=sha(999);
  assert.throws(()=>executeStage(later.g,later.g,later.p,saved),/authorized barrier|Integration moved/);assert.equal(later.commands.length,0);
});
test('main rechecks exact execution source after final concurrent-run lookup before POST',()=>{
  const f=fixture(true,'main'),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;
  const pages=f.g.pages;let lookups=0;
  f.g.pages=path=>{const result=pages(path);if(path.includes(`/actions/workflows/${f.python.fanout}/runs`)&&++lookups===2)f.state.heads.python=sha(999);return result;};
  assert.throws(()=>executeStage(f.g,f.g,f.p,j),/execution source moved/);assert.equal(f.commands.length,0);
});

for(const channel of ['dev','main'])test(`${channel} stage fanout requires authenticated frozen selected-test evidence`,()=>{
  const f=fixture(true,channel),manifest=JSON.parse(readFileSync(new URL(`./targets.${channel}.json`,import.meta.url)));
  f.p.policy.test_selection_policy={schema:CAMPAIGN_TEST_POLICY,targets:manifest.targets.map(t=>({id:t.id,repository:t.repository,default_test_repository:'cwru-non-academic/TestBundle',allowed_test_repositories:['cwru-non-academic/TestBundle']}))};
  const selection=resolveTestSelection(selectionRequest(f.p.policy,f.p.candidate),{head:()=>sha(99)});
  Object.assign(f.p,selectionFields(f.p.policy,f.p.candidate,selection));f.p.policy_sha256=digest(f.p.policy);
  const {plan_id,...body}=f.p;f.p.plan_id=digest(body);
  const normal=f.g.pages(`repos/${f.python.repository}/actions/workflows/${f.python.normal}/runs`)[0];
  const evidence=targetTestEvidence(f.p,'python-wrapper');
  const artifact={id:900,name:selectedTestArtifactName('python-wrapper',1),expired:false,workflow_run:{id:normal.id,head_sha:normal.head_sha}};
  f.g.artifact=()=>artifact;
  f.g.download=()=>({'evidence.json':Buffer.from(JSON.stringify(evidence))});
  const j=prepareStage(f.g,f.p,'9',1);assert.equal(j.actions.length,1);assert.deepEqual(j.barrier.integrations.python.selected_tests,evidence);
  for(const corrupt of ['owner','source','selection']) {
    const old=structuredClone(artifact),oldEvidence=structuredClone(evidence);
    if(corrupt==='owner')artifact.workflow_run.id=999;
    if(corrupt==='source')evidence.test_commit=sha(88);
    if(corrupt==='selection')evidence.selection_id='b'.repeat(64);
    const blocked=prepareStage(f.g,f.p,'9',1);assert.equal(blocked.actions.length,0);assert.ok(blocked.blocked.some(b=>b.target==='python'));
    Object.assign(artifact,old);Object.assign(evidence,oldEvidence);
  }
});
