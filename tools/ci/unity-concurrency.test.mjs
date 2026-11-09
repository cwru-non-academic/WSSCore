// One Core-owned contract, executed here and against each receiver's actual YAML.
// Receiver CI sets WSS_CONCURRENCY_TARGET_ROOT; missing files fail, never skip.
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,realpathSync} from 'node:fs';
import {resolve,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {runInNewContext} from 'node:vm';
import {parseDocument} from './vendor/yaml/dist/index.js';

const families={
  Unity_WSS_Integration_Lib:{file:'unity-compatibility.yml',name:'WSS integration compatibility',prefix:'unity-wrapper',role:'wrapper',source:'wrapper_sha',promotion:'promote-wss.yml'},
  GUI_Unity_WSS_Application:{file:'unity-ci.yml',name:'GUI CI',prefix:'unity-gui',role:'gui',source:'gui_sha',promotion:'promote-integration.yml'}
};
const sha=n=>String(n).repeat(40);
function yaml(text,label){
  const document=parseDocument(text,{uniqueKeys:true});
  assert.deepEqual(document.errors,[],`${label}: invalid YAML`);
  return document.toJS();
}
function expression(body,context){
  // Only these actual concurrency expressions are supported, not an Actions
  // interpreter. For string event names, == and &&/|| have the same semantics.
  const token=/github\.(?:event\.repository\.name|workflow|event_name|ref_name|ref|sha|run_id)\b|inputs\.(?:correlation_id|plan_id)\b|'(?:pull_request|latest|Unity_WSS_Integration_Lib|GUI_Unity_WSS_Application)'|\b(?:true|false)\b|==|&&|\|\||[()]/g;
  assert.equal(body.replace(token,'').replace(/\s/g,''),'','Unsupported concurrency expression');
  return runInNewContext(body,{github:{...context.github},inputs:{...context.inputs}},{timeout:100});
}
function scalar(value,context){
  if(typeof value==='boolean')return value;
  assert.equal(typeof value,'string','Concurrency value must be a scalar');
  const whole=value.match(/^\$\{\{\s*([\s\S]*?)\s*\}\}$/);
  if(whole)return expression(whole[1],context);
  const result=value.replace(/\$\{\{\s*([\s\S]*?)\s*\}\}/g,(_,body)=>String(expression(body,context)));
  assert.ok(!result.includes('${{'),'Unresolved concurrency expression');
  return result;
}
function identity(workflow,context){
  assert.ok(workflow.concurrency,'Workflow concurrency is required');
  const group=scalar(workflow.concurrency.group,context);
  const cancel=scalar(workflow.concurrency['cancel-in-progress'],context);
  assert.equal(typeof group,'string');assert.ok(group.length>0);
  assert.equal(typeof cancel,'boolean');
  // GitHub treats concurrency-group names as case insensitive.
  return {group:group.toLowerCase(),cancel};
}
function context(workflow,event='push',branch='dev',changes={},inputs={}){
  return {github:{workflow:workflow.name,event_name:event,ref:`refs/heads/${branch}`,ref_name:branch,sha:sha(1),run_id:'101',...changes},inputs};
}
function checkNormal(workflow,family){
  assert.deepEqual(Object.keys(workflow.on).sort(),['pull_request','push','workflow_dispatch']);
  for(const event of ['push','pull_request'])assert.deepEqual([...workflow.on[event].branches].sort(),['dev','main']);
  assert.deepEqual(workflow.on.workflow_dispatch||{}, {},'Normal dispatch has no candidate/intent inputs');
  assert.equal(workflow.jobs.validate.with.role,family.role);
  assert.equal(workflow.jobs.validate.with.mode,'committed');
  assert.equal(workflow.jobs.validate.with[family.source],'${{ github.sha }}');
  for(const job of Object.values(workflow.jobs))assert.equal(job.concurrency,undefined,'Do not reintroduce cancellation below the caller');
  const base=context(workflow,'pull_request','dev',{ref:'refs/pull/17/merge'});
  const current=identity(workflow,base);
  const latest=identity(workflow,context(workflow,'pull_request','dev',{ref:'refs/pull/17/merge',sha:sha(2),run_id:'102'}));
  assert.equal(current.cancel,true,'Stale PR checks should be superseded');
  assert.deepEqual(latest,current,'New heads of one PR share its cancellation group');
  assert.notEqual(current.group,identity(workflow,context(workflow,'pull_request','dev',{ref:'refs/pull/18/merge'})).group,'Different PRs must not collide');
  for(const branch of ['dev','main'])for(const event of ['push','workflow_dispatch']){
    const run=context(workflow,event,branch);
    const own=identity(workflow,run);
    assert.equal(own.cancel,false,'Committed proof must not be cancelled');
    assert.notEqual(own.group,current.group,'PR checks cannot preempt committed proof');
    for(const changes of [{sha:sha(2)},{run_id:'102'},{workflow:'Other CI'},{event_name:event==='push'?'workflow_dispatch':'push'}]){
      assert.notEqual(own.group,identity(workflow,{...run,github:{...run.github,...changes}}).group,'Source, request, workflow and event identities must remain isolated');
    }
    assert.deepEqual(identity(workflow,{...run,github:{...run.github,run_attempt:2}}),own,'A same-run recovery retains identity without cancelling itself');
    const duplicates=['101','102','103'].map(run_id=>identity(workflow,context(workflow,event,branch,{run_id})).group);
    assert.equal(new Set(duplicates).size,3,'Same-source requests must not replace an already pending request');
  }
}
function checkCandidate(normal,candidate,family){
  assert.deepEqual(Object.keys(candidate.on.workflow_dispatch.inputs).sort(),['candidate_json','correlation_id','gui_sha','wrapper_sha']);
  assert.equal(candidate.jobs.validate.with.role,family.role);
  assert.equal(candidate.jobs.validate.with.mode,'candidate');
  for(const input of ['wrapper_sha','gui_sha','candidate_json'])assert.equal(candidate.jobs.validate.with[input],`\${{ inputs.${input} }}`);
  const committed=identity(normal,context(normal,'workflow_dispatch'));
  const first=context(candidate,'workflow_dispatch','dev',{}, {correlation_id:'intent-a',wrapper_sha:sha(1),gui_sha:sha(2),candidate_json:'{"run_id":"10"}'});
  const other={...first,inputs:{...first.inputs,correlation_id:'intent-b',candidate_json:'{"run_id":"11"}'}};
  assert.equal(identity(candidate,first).cancel,false);
  assert.notEqual(identity(candidate,first).group,committed.group,'Candidate work is not normal committed evidence');
  assert.notEqual(identity(candidate,first).group,identity(candidate,other).group,'Distinct candidate intents on one source remain isolated');
}
function fixture(family){
  return yaml(`name: ${family.name}
on:
  push: {branches: [dev, main]}
  pull_request: {branches: [dev, main]}
  workflow_dispatch:
concurrency:
  group: ${family.prefix}-\${{ github.workflow }}-\${{ github.event_name }}-\${{ github.event_name == 'pull_request' && github.ref || github.sha }}-\${{ github.event_name == 'pull_request' && 'latest' || github.run_id }}
  cancel-in-progress: \${{ github.event_name == 'pull_request' }}
jobs:
  validate:
    with:
      role: ${family.role}
      mode: committed
      ${family.source}: \${{ github.sha }}
`,'Core concurrency fixture');
}
for(const [repository,family] of Object.entries(families)){
  test(`${repository}: reference policy preserves committed work and supersedes stale PR checks`,()=>checkNormal(fixture(family),family));
  test(`${repository}: old cancellation and pending-replacement regressions are detected`,()=>{
    const safe=fixture(family);
    for(const concurrency of [
      {group:`${family.prefix}-\${{ github.ref }}`,'cancel-in-progress':true},
      {group:`${family.prefix}-\${{ github.ref }}`,'cancel-in-progress':false},
      {...safe.concurrency,group:safe.concurrency.group.replace("\${{ github.event_name == 'pull_request' && 'latest' || github.run_id }}",'shared')},
      {...safe.concurrency,group:safe.concurrency.group.replace("\${{ github.event_name == 'pull_request' && github.ref || github.sha }}",'${{ github.sha }}')},
      {...safe.concurrency,group:safe.concurrency.group.replace('${{ github.workflow }}','shared')},
      {...safe.concurrency,group:safe.concurrency.group.replace('${{ github.event_name }}','shared')},
      {...safe.concurrency,'cancel-in-progress':true}
    ])assert.throws(()=>checkNormal({...safe,concurrency},family));
  });
}
test('canonical shared workflow owns the test registration and selects exactly the Unity consumers',()=>{
  const registry=JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url),'utf8'));
  assert.deepEqual(registry.files['.github/workflows/shared-ci.yml'],{origin:'core',path:'tools/ci/shared/shared-ci.yml'});
  const shared=yaml(readFileSync(new URL('./shared/shared-ci.yml',import.meta.url),'utf8'),'Canonical shared CI');
  const steps=shared.jobs.identical.steps;
  const entries=steps.filter(step=>step.run==='node --test .wss-core/tools/ci/unity-concurrency.test.mjs');
  assert.equal(entries.length,1,'One owner-sourced regression registration is required');
  const entry=entries[0];
  assert.equal(entry.env.WSS_CONCURRENCY_TARGET_ROOT,'${{ github.workspace }}');
  assert.equal(entry['continue-on-error'],undefined);
  const checker=steps.findIndex(step=>step.run?.startsWith('node .wss-core/tools/ci/sync-tooling.mjs check '));
  assert.ok(checker>=0&&steps.indexOf(entry)>checker,'Generated source identity must be checked before testing');
  for(const name of [...registry.repositories,'WSSCore','UnrelatedRepository']){
    assert.equal(scalar(entry.if,{github:{event:{repository:{name}}},inputs:{}}),Object.hasOwn(families,name),name);
  }
});
// Core runs the verifier's own regression cases. A receiver must opt into its
// actual YAML explicitly; losing that environment binding must not go green.
if(process.env.WSS_CONCURRENCY_TARGET_ROOT===undefined){
  assert.equal(realpathSync(process.cwd()),realpathSync(fileURLToPath(new URL('../..',import.meta.url))),
    'Set WSS_CONCURRENCY_TARGET_ROOT when running outside the Core checkout');
}
if(process.env.WSS_CONCURRENCY_TARGET_ROOT!==undefined){
  const root=resolve(process.env.WSS_CONCURRENCY_TARGET_ROOT);
  assert.ok(process.env.WSS_CONCURRENCY_TARGET_ROOT.trim(),'Receiver root must be explicit');
  const lock=JSON.parse(readFileSync(join(root,'.github/scripts/wss-ci.lock.json'),'utf8'));
  const repository=lock.repository.replace(/^cwru-non-academic\//,'');
  const family=families[repository];assert.ok(family,'Only the two Unity consumers are supported');
  const read=file=>yaml(readFileSync(join(root,'.github/workflows',file),'utf8'),file);
  test(`${repository}: actual normal workflow preserves committed CI`,()=>checkNormal(read(family.file),family));
  test(`${repository}: actual candidate, promotion and receipt intents stay separate`,()=>{
    const normal=read(family.file);checkCandidate(normal,read('wss-preflight.yml'),family);
    const ordinary=identity(normal,context(normal,'workflow_dispatch'));
    for(const file of [family.promotion,'release-receipt.yml']){
      const sibling=read(file),actual=identity(sibling,context(sibling,'workflow_dispatch','dev',{}, {plan_id:'plan-a'}));
      assert.equal(actual.cancel,false);assert.notEqual(actual.group,ordinary.group);
    }
    if(family.role==='wrapper')for(const file of ['_unity-validation.yml','_unity-platform-validation.yml']){
      const reusable=read(file);assert.equal(reusable.concurrency,undefined,'Reused jobs must not cancel their caller');
      for(const job of Object.values(reusable.jobs))assert.equal(job.concurrency,undefined);
    }
  });
}
