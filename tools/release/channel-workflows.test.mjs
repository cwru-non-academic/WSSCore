import test from 'node:test';
import assert from 'node:assert/strict';
import {renderChannelWorkflow,renderInitialWorkflow} from '../ci/channel-workflows.mjs';
import {readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {join} from 'node:path';
import {INITIAL_HANDOFF_PROFILES} from '../ci/dev-propagation.mjs';
import {publicationControls} from '../ci/channel-controls.mjs';
const activation=main=>({schema:'wss-ci-channel-activation/v1',dev:true,main});
test('initial workflows have one implementation, closed scopes, explicit channels and activation filters',()=>{
  for(const enabled of [false,true])for(const [family,profile] of Object.entries(INITIAL_HANDOFF_PROFILES)){
    const r=renderInitialWorkflow(family,{activation:activation(enabled)});
    assert.equal(r.path,`.github/workflows/propagate-${family}.yml`);
    assert.ok(r.text.includes(`branches: [${enabled?'dev, main':'dev'}]`));
    assert.ok(r.text.includes('WSS_CI_CHANNEL: ${{ github.event.workflow_run.head_branch }}'));
    assert.ok(r.text.includes(`node tools/ci/dev-propagation.mjs initial ${family}`));
    assert.ok(r.text.includes(`repositories: ${profile.repository.split('/')[1]}`));
    assert.equal(r.text.match(/permission-actions: write/g).length,1);
    assert.ok(r.text.indexOf('check-initial')<r.text.indexOf('actions/create-github-app-token'));
    assert.ok(r.text.includes('WSS_WORKFLOW_READ_TOKEN: ${{ github.token }}'));
    assert.ok(r.text.includes('ref: ${{ github.sha }}'));
    assert.doesNotMatch(r.text,/gh api|prepare-python-handoff|prepare-unity-handoff/);
    if(!enabled)assert.doesNotMatch(r.text,/head_branch == 'main'/);
  }
  assert.throws(()=>renderInitialWorkflow('csharp'));assert.throws(()=>renderInitialWorkflow('other'));
});
test('Common CI runs for every actual producer and generated controller entrypoint',()=>{
  const text=readFileSync(new URL('../../.github/workflows/shared-ci.yml',import.meta.url),'utf8');
  for(const file of ['propagate-python','propagate-unity','propagate-matlab','dev-propagation','main-propagation','dev-release','main-release','artifacts'])
    assert.equal(text.split(`'.github/workflows/${file}.yml'`).length-1,2,`Both PR and push must cover ${file}`);
});
test('main bootstrap exposes only read-only manual verification, never scheduled writes',()=>{
  for(const operation of ['propagation','acceptance']){
    const r=renderChannelWorkflow(operation,'main',{activation:activation(false)});
    assert.match(r.text,/WSS_CI_CHANNEL: main/);assert.match(r.text,/default: verify/);
    assert.doesNotMatch(r.text,/^  (workflow_run|schedule):/m);assert.match(r.text,/^  workflow_dispatch:/m);
    if(operation==='propagation')assert.match(r.text,/false && needs.prepare.outputs.actions/);
    else{
      for(const name of ['Prepare read-only reconciliation intents','Retain dispatch intent before any cross-repository request','Create receipt reconciliation Actions token','Reconcile existing merges with fresh normal CI and read-only receipts'])
        assert.ok(r.text.includes(`- name: ${name}\n        if: false`));
      assert.match(r.text,/false && \(false\) && github.run_attempt == 1/);
    }
  }
});
test('activated main and dev render the same engines and keep publication behind explicit controls',()=>{
  for(const channel of ['dev','main'])for(const operation of ['propagation','acceptance']){
    const r=renderChannelWorkflow(operation,channel,{activation:activation(true)});
    assert.match(r.text,new RegExp(`branches: \\[${channel}\\]`));assert.match(r.text,/^  schedule:/m);
    assert.ok(r.text.includes("github.event_name != 'workflow_dispatch' || inputs.mode == 'reconcile'"));
    if(operation==='propagation'){
      assert.ok(r.text.includes('node tools/ci/dev-propagation.mjs prepare'));assert.ok(r.text.includes('node tools/ci/dev-propagation.mjs dispatch'));
      assert.doesNotMatch(r.text,/node tools\/ci\/main-propagation.mjs/);
    }else{
      assert.ok(r.text.includes('node tools/release/cli.mjs publish'));
      assert.ok(r.text.includes("vars.WSS_DEV_ACCEPTANCE_ENABLED == 'true'"));assert.ok(r.text.includes("vars.WSS_DEV_RELEASE_ENABLED == 'true'"));
      assert.doesNotMatch(r.text,/WSS_STABLE_RELEASE_ENABLED|gh release create/);
      assert.ok(r.text.includes('ref: ${{ github.sha }}'),'Trusted-main event source is immutable, not a floating checkout');
      assert.ok(r.text.includes('WSS_EVIDENCE_READ_TOKEN:'));
    }
  }
});
test('workflow generation refuses arbitrary channels, operations and activation fields',()=>{
  assert.throws(()=>renderChannelWorkflow('acceptance','other'));
  assert.throws(()=>renderChannelWorkflow('custom-publication','main'));
  assert.throws(()=>renderChannelWorkflow('acceptance','main',{activation:{...activation(false),allow_publish:true}}));
});
test('Core-only control mapping changes the main binding without a second publisher',()=>{
  const controls=publicationControls({...publicationControls(),main:'WSS_MAIN_RELEASE_ENABLED'});
  const main=renderChannelWorkflow('acceptance','main',{activation:activation(true),controls});
  const dev=renderChannelWorkflow('acceptance','dev',{activation:activation(true),controls});
  assert.ok(main.text.includes('WSS_MAIN_RELEASE_ENABLED: ${{ vars.WSS_MAIN_RELEASE_ENABLED }}'));
  assert.ok(main.text.includes("vars.WSS_MAIN_RELEASE_ENABLED == 'true'"));
  assert.ok(dev.text.includes('WSS_DEV_RELEASE_ENABLED: ${{ vars.WSS_DEV_RELEASE_ENABLED }}'));
  assert.ok(main.text.includes('node tools/release/cli.mjs publish'));
});

function acceptanceJob(text,id){
  const match=text.match(new RegExp(`^  ${id}:\\n([\\s\\S]*?)(?=^  [a-z]+:|$(?![\\s\\S]))`,'m'));
  assert.ok(match,`Missing job ${id}`);return match[1];
}
function publicationCondition(job,values){
  const match=job.match(/^    if: >-\n((?:      .+\n)+)/m);assert.ok(match);
  const expression=match[1].trim().replace(/\b(?:github\.event_name|github\.run_attempt|inputs\.mode|needs\.reconcile\.result|needs\.verify\.result|vars\.WSS_DEV_ACCEPTANCE_ENABLED|vars\.WSS_DEV_RELEASE_ENABLED)\b/g,key=>JSON.stringify(values[key]));
  assert.doesNotMatch(expression,/github\.|inputs\.|needs\.|vars\./,'Unrecognized gate must not escape coverage');
  assert.match(expression,/^[\s\w"'()=!&|.-]+$/);
  return Function(`"use strict";return (${expression});`)();
}
test('acceptance verification is a separate read-only job bound to the original producer',()=>{
  for(const channel of ['dev','main']){
    const text=renderChannelWorkflow('acceptance',channel,{activation:activation(true)}).text;
    const reconcile=acceptanceJob(text,'reconcile'),verify=acceptanceJob(text,'verify'),publish=acceptanceJob(text,'publish');
    assert.match(reconcile,/producer_run_id: \$\{\{ steps.producer.outputs.run_id \}\}/);
    assert.doesNotMatch(reconcile,/cli\.mjs verify|accepted\/acceptance\.json/);
    assert.match(verify,/needs: reconcile\n    if: needs.reconcile.result == 'success'/);
    assert.match(verify,/permissions:\n      contents: read\n      actions: read/);
    assert.doesNotMatch(verify,/write|receipt-token|WSS_JOURNAL_READ_TOKEN|select-producer|REQUESTED_RUN|cli\.mjs (dispatch|prepare-dispatches|publish)/);
    assert.match(verify,/permission-contents: read/);assert.match(verify,/permission-actions: read/);assert.match(verify,/permission-pull-requests: read/);
    assert.equal(verify.match(/PRODUCER_RUN_ID: \$\{\{ needs.reconcile.outputs.producer_run_id \}\}/g)?.length,2);
    assert.match(verify,/run: node tools\/release\/cli.mjs verify "\$PRODUCER_RUN_ID" '\$\{\{ runner.temp \}\}\/accepted'/);
    assert.match(verify,/ref: \$\{\{ github.sha \}\}/);
    assert.ok(verify.indexOf('Require the original selected producer')<verify.indexOf('Collect actual merge receipts'));
    assert.match(verify,new RegExp(`name: ${channel}-release-acceptance-\\$\\{\\{ needs.reconcile.outputs.producer_run_id \\}\\}-a\\$\\{\\{ github.run_attempt \\}\\}`));
    assert.match(verify,/if-no-files-found: error/);assert.doesNotMatch(verify,/overwrite:|continue-on-error:|if: always\(\)/);
    assert.match(publish,/needs: \[reconcile, verify\]/);
    assert.match(publish,/needs.reconcile.result == 'success' && needs.verify.result == 'success'/);
  }
});
test('actual generated publication expression rejects every check-only rerun and failed prerequisite',()=>{
  for(const enabled of [false,true])for(const channel of ['dev','main']){
    const job=acceptanceJob(renderChannelWorkflow('acceptance',channel,{activation:activation(enabled)}).text,'publish');
    for(const event of ['workflow_dispatch','schedule','workflow_run'])for(const mode of ['verify','reconcile'])
    for(const acceptance of ['','false','true'])for(const release of ['','false','true'])for(const attempt of [1,2,3])
    for(const reconcile of ['success','failure','skipped','cancelled'])for(const verify of ['success','failure','skipped','cancelled']){
      const actual=publicationCondition(job,{'github.event_name':event,'inputs.mode':mode,'github.run_attempt':attempt,
        'vars.WSS_DEV_ACCEPTANCE_ENABLED':acceptance,'vars.WSS_DEV_RELEASE_ENABLED':release,
        'needs.reconcile.result':reconcile,'needs.verify.result':verify});
      const expected=(channel==='dev'||enabled)&&(event!=='workflow_dispatch'||mode==='reconcile')&&attempt===1&&acceptance==='true'&&release==='true'&&reconcile==='success'&&verify==='success';
      assert.equal(actual,expected,JSON.stringify({channel,enabled,event,mode,acceptance,release,attempt,reconcile,verify}));
    }
  }
});

test('actual verification shell boundary rejects missing or malformed prerequisite outputs',()=>{
  const bash=process.platform==='win32'?join(process.env.ProgramFiles,'Git','bin','bash.exe'):'bash';
  for(const channel of ['dev','main']){
    const job=acceptanceJob(renderChannelWorkflow('acceptance',channel,{activation:activation(true)}).text,'verify');
    const match=job.match(/name: Require the original selected producer[\s\S]*?run: \|\n((?:          .+\n)+)/);assert.ok(match);
    const script=match[1].replace(/^          /gm,'');
    for(const value of [undefined,'','0','-1','abc','1 2','1\n2','$(echo unexpected)','42258265327; echo unexpected','42258265327']){
      const env={...process.env};delete env.PRODUCER_RUN_ID;if(value!==undefined)env.PRODUCER_RUN_ID=value;
      const result=spawnSync(bash,['-e','-c',script],{env,encoding:'utf8'});assert.ifError(result.error);
      assert.equal(result.status,value==='42258265327'?0:1,String(value));assert.equal(result.stdout,'');
      if(result.status!==0)assert.match(result.stderr,/Missing or invalid original reconciliation producer output/);
    }
  }
});
