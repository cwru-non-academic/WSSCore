import test from 'node:test';
import assert from 'node:assert/strict';
import {renderChannelWorkflow,renderInitialWorkflow} from '../ci/channel-workflows.mjs';
import {readFileSync} from 'node:fs';
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
      assert.match(r.text,/false && \(false\) && needs.reconcile.result/);
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
