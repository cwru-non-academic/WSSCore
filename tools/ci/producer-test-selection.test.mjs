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
  assert.ok(cli.indexOf('loadProducerTestSelection(g,m,stagePolicy,c)')<cli.indexOf('freezeCampaign(g,m,stagePolicy,c'));
});
test('snapshot token uses existing Actions grant and durable intent precedes dispatch',()=>{
  const start=workflow.indexOf('Create existing Actions-write snapshot dispatch token'),end=workflow.indexOf('Prepare or reuse',start),block=workflow.slice(start,end);
  assert.match(block,/test_selection_enabled == 'true' && github.run_attempt == 1/);
  assert.match(block,/repositories: \$\{\{ steps.channel.outputs.test_selection_repositories \}\}/);
  assert.match(block,/permission-actions: write/);
  assert.match(block,/permission-contents: read/);
  assert.doesNotMatch(workflow,/permission-variables:/);
  assert.match(cli,/collectCampaignVariableSnapshots/);
  assert.ok(workflow.indexOf('name: wss-test-variable-intents')<start);
});
test('legacy channel flow remains separate and selected campaigns require both plans',()=>{
  assert.match(workflow,/Main or selected-test campaign requires both frozen acceptance and stage plans/);
  assert.match(workflow,/node tools\/ci\/dev-propagation.mjs freeze/);
  assert.match(cli,/Selected tests require paired freeze with the durable artifact/);
  assert.match(workflow,/Campaign test policies differ/);
});
