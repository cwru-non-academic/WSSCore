import assert from 'node:assert/strict';
import test from 'node:test';
import {EvidenceGitHub, retryGitHubRead} from './github-read-retry.mjs';

const gatewayError = () => Object.assign(new Error('gh api failed'), {
  stderr: 'gh: Server Error (HTTP 502)\n',
});

test('retries temporary GitHub 502, 503 and 504 responses and returns the successful read', () => {
  const delays = [];
  const result = {jobs: [{id: 15}]};
  let attempts = 0;
  const response = retryGitHubRead(() => {
    attempts++;
    if (attempts === 1) throw gatewayError();
    if (attempts === 2) throw Object.assign(new Error('gh api failed'), {stderr: Buffer.from('gh: HTTP 503\n')});
    if (attempts === 3) throw Object.assign(new Error('gh api failed'), {
      stderr: 'gh: Gateway failed\nPlease retry (HTTP 504)\n',
    });
    return result;
  }, delay => delays.push(delay));
  assert.equal(response, result);
  assert.equal(attempts, 4);
  assert.deepEqual(delays, [2_000, 8_000, 20_000]);
});

test('persistent 502s exhaust a bounded budget and surface the last error', () => {
  const delays = [];
  const errors = Array.from({length: 4}, gatewayError);
  let attempts = 0;
  assert.throws(() => retryGitHubRead(() => {
    throw errors[attempts++];
  }, delay => delays.push(delay)), error => error === errors[3]);
  assert.equal(attempts, 4);
  assert.deepEqual(delays, [2_000, 8_000, 20_000]);
});

test('does not retry a permission error, missing result, or invalid JSON', () => {
  for (const error of [
    Object.assign(new Error('forbidden'), {stderr: 'gh: Forbidden (HTTP 403)\n'}),
    Object.assign(new Error('not found'), {stderr: 'gh: Not Found (HTTP 404)\n'}),
    new SyntaxError('Invalid JSON'),
  ]) {
    assert.throws(() => retryGitHubRead(() => { throw error; }, () => {
      assert.fail('Unexpected retry');
    }), failure => failure === error);
  }
});

test('exact attempt job discovery retries the same read and checks pagination', () => {
  const client = new EvidenceGitHub();
  const requests = [];
  client.command = args => {
    requests.push(args);
    if (requests.length === 1) throw gatewayError();
    return '{"total_count":2,"jobs":[{"id":15}]}';
  };
  assert.throws(() => client.jobs('org/app', {id: 42, run_attempt: 1}), /Incomplete GitHub pagination/);
  const request = ['api', 'repos/org/app/actions/runs/42/attempts/1/jobs?per_page=100&page=1'];
  assert.deepEqual(requests, [request, request]);
});
