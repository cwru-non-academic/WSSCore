import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const workflow=readFileSync(new URL('../../.github/workflows/artifacts.yml',import.meta.url),'utf8');
const cli=readFileSync(new URL('../release/cli.mjs',import.meta.url),'utf8');
test('producer durably uploads the selection before independent paired freeze and dispatch',()=>{
  const prepare=workflow.indexOf('prepare-test-selection'),persist=workflow.indexOf('name: wss-test-selection'),freeze=workflow.indexOf('Freeze exact candidate and channel plans'),dispatch=workflow.indexOf('Create Integration dispatch-only App token');
  assert.ok(prepare>0&&prepare<persist&&persist<freeze&&freeze<dispatch);
  assert.match(workflow,/if: steps\.test-selection\.outputs\.created == 'true'/);
  assert.match(workflow,/path: \$\{\{ runner.temp \}\}\/test-selection\/selection.json/);
  assert.doesNotMatch(workflow.slice(persist,freeze),/overwrite:\s*true/);
  assert.ok(cli.indexOf('loadProducerTestSelection(g,m,stagePolicy,c,')<cli.indexOf('freezeCampaign(g,m,stagePolicy,c'));
});
test('snapshot token uses existing Actions grant and durable intent precedes dispatch',()=>{
  const start=workflow.indexOf('Create existing Actions-write snapshot dispatch token'),end=workflow.indexOf('Prepare or reuse',start),block=workflow.slice(start,end);
  assert.match(block,/test_selection_enabled == 'true' && github.run_attempt == 1/);
  assert.match(block,/repositories: \$\{\{ steps.channel.outputs.test_selection_repositories \}\}/);
  assert.match(block,/permission-actions: write/);
  assert.match(block,/permission-contents: read/);
  assert.doesNotMatch(workflow,/permission-variables:/);
  assert.match(cli,/collectCampaignVariableSnapshots/);
  assert.ok(workflow.indexOf('name: wss-test-variable-intents')<workflow.indexOf('Dispatch local snapshots once'));
  assert.ok(start<workflow.indexOf('Prepare immutable local variable requests'));
});
test('both channels freeze and retain the paired plans before downstream writes',()=>{
  const propagation=workflow.split(/^  (?=[\w-]+:\r?$)/m).find(job=>job.startsWith('propagate-universal:'));
  assert.ok(propagation);
  const [header,...steps]=propagation.split(/^      - /m);
  assert.match(header,/github\.ref == 'refs\/heads\/dev' \|\| github\.ref == 'refs\/heads\/main'/);

  const freezeCommand='node tools/release/cli.mjs freeze-campaign ';
  const freeze=steps.findIndex(step=>step.includes(freezeCommand));
  assert.equal(steps.filter(step=>step.includes(freezeCommand)).length,1);
  assert.match(steps[freeze],/\$context\.targetPolicy "\$env:RUNNER_TEMP\/release-plan" "\$env:RUNNER_TEMP\/propagation-plan"/);
  assert.doesNotMatch(steps[freeze],/^        if:/m);
  assert.doesNotMatch(propagation,/^\s*node tools\/(?:ci\/dev-propagation|release\/cli)\.mjs freeze\s/m);
  assert.match(propagation,/if\(canonical\(policy\)!==canonical\(stage\)\)throw\b/);
  assert.match(cli,/requireThat\(!testSelectionPolicy\(m\),/);

  const token=steps.findIndex(step=>step.includes('id: integration-token'));
  const dispatch=steps.findIndex(step=>step.includes('node tools/ci/workflow-freeze.mjs dispatch-initial csharp "$env:RUNNER_TEMP/propagation-plan/plan.json"'));
  assert.ok(token>=0&&token<dispatch);
  for(const [name,directory] of [
    ['${{ steps.channel.outputs.stage_artifact }}','propagation-plan'],
    ['wss-propagation-plan','release-plan']
  ]) {
    const upload=steps.findIndex(step=>step.startsWith('uses: actions/upload-artifact@')&&
      step.includes(`name: ${name}`)&&step.includes(`path: \${{ runner.temp }}/${directory}/plan.json`));
    assert.ok(freeze<upload&&upload<token);
    assert.match(steps[upload],/if-no-files-found: error/);
    assert.doesNotMatch(steps[upload],/^        if:/m);
  }
});

test('capture source race check is inside paired freeze and before plan files',()=>{
 const freeze=readFileSync(new URL('../release/campaign-freeze.mjs',import.meta.url),'utf8');
 assert.ok(freeze.indexOf('if(captureDocument)assertSnapshotSourcesMatchPlans(captureDocument,pair,g)')<freeze.indexOf('return pair;'));
 assert.ok(cli.indexOf('loadProducerTestSelection(g,m,stagePolicy,c,')<cli.indexOf('freezeCampaign(g,m,stagePolicy,c'));
 assert.match(cli,/freezeCampaign\(g,m,stagePolicy,c,\{testSelection,captureDocument/);
});
