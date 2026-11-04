import {test} from 'node:test';
import assert from 'node:assert/strict';
import {BLE_GROUPS, bleSummary, summarizeBle} from '../ci/ble-summary.mjs';

const head = 'a'.repeat(40);
const jobs = platform => BLE_GROUPS[platform].map(name =>
  ({name, run_id: 123, head_sha: head, status: 'completed', conclusion: 'success'}));

test('logical BLE checks retain legacy names and require every Mac architecture', () => {
  assert.deepEqual(BLE_GROUPS.Mac, ['BLE consumer (macOS arm64)', 'BLE consumer (macOS x86_64)']);
  for (const platform of Object.keys(BLE_GROUPS))
    assert.equal(bleSummary(platform, jobs(platform), 123, head).passed, true);
  for (const index of [0, 1]) {
    assert.equal(bleSummary('Mac', jobs('Mac').filter((_, i) => i !== index), 123, head).passed, false);
    for (const conclusion of ['failure', 'cancelled', 'skipped', 'neutral', 'timed_out', null]) {
      const actual = jobs('Mac'); actual[index].conclusion = conclusion;
      assert.equal(bleSummary('Mac', actual, 123, head).passed, false, `${index}: ${conclusion}`);
    }
    for (const change of [{status: 'in_progress'}, {run_id: 124}, {head_sha: 'b'.repeat(40)}]) {
      const actual = jobs('Mac'); Object.assign(actual[index], change);
      assert.equal(bleSummary('Mac', actual, 123, head).passed, false);
    }
    assert.equal(bleSummary('Mac', [...jobs('Mac'), jobs('Mac')[index]], 123, head).passed, false);
  }
  assert.throws(() => bleSummary('macOS', [], 123, head));
  assert.throws(() => bleSummary('Mac', null, 123, head));
});

test('summary fetches only the current attempt and reports both architecture outcomes before failing', async () => {
  const actual = jobs('Mac'); actual[1].conclusion = 'skipped';
  const tables = []; let wrote = false; let calls = 0;
  const core = {info() {}, summary: {
    addHeading() { return this; }, addTable(value) { tables.push(value); return this; },
    addRaw() { return this; }, async write() { wrote = true; },
  }};
  const github = {rest: {actions: {async getWorkflowRunAttempt(params) {
    assert.equal(params.attempt_number, 2);
    return {data: {id: 123, run_attempt: 2, head_sha: head}};
  }}}, async paginate(route, params) {
    calls++;
    assert.equal(route, 'GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs');
    assert.deepEqual(params, {owner: 'example', repo: 'Core', run_id: 123, attempt_number: 2, per_page: 100});
    return actual;
  }};
  const args = {github, core, context: {repo: {owner: 'example', repo: 'Core'}, runId: 123, sha: 'b'.repeat(40)}, platform: 'Mac', attempt: 2};
  await assert.rejects(summarizeBle(args), /every unique current-attempt/);
  assert.equal(wrote, true);
  assert.equal(tables[0].length, 3);
  assert.match(tables[0][2][1], /FAIL: completed\/skipped/);
  assert.equal(calls, 1);
  actual[1].conclusion = 'success';
  await summarizeBle(args);
  assert.equal(calls, 2);
  await assert.rejects(summarizeBle({...args, attempt: 0}), /Invalid run attempt/);
  assert.equal(calls, 2);
  const wrongAttempt = {...github, rest: {actions: {async getWorkflowRunAttempt() {
    return {data: {id: 123, run_attempt: 1, head_sha: head}};
  }}}};
  await assert.rejects(summarizeBle({...args, github: wrongAttempt}), /Run attempt identity mismatch/);
  assert.equal(calls, 2);
});
