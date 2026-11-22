import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import test from 'node:test';
import {inspectReadiness, retainEvidence, retentionIssues} from '../ci/ci-readiness.mjs';

test('candidate and receipt evidence outlive temporary runner artifacts', () => {
  const text = [
    'jobs:',
    '  verify:',
    '    steps:',
    '      - uses: actions/upload-artifact@v4',
    '        with:',
    '          name: verified-wss-universal',
    '          retention-days: 1',
    '      - uses: actions/upload-artifact@v4',
    '        with:',
    '          name: wss-candidate-provenance',
    '          retention-days: 1 # review window',
    '      - uses: actions/upload-artifact@v4',
    '        with:',
    '          name: python-preflight-platform-macOS',
    ''
  ].join('\r\n');
  const file = 'wss-preflight.yml';
  assert.equal(retentionIssues(text,file).length, 2);
  const updated = retainEvidence(text,file);
  assert.match(updated, /name: verified-wss-universal\r\n {10}retention-days: 1\r\n/);
  assert.match(updated, /retention-days: 90 # review window\r\n/);
  assert.deepEqual(retentionIssues(updated,file), []);
  assert.equal(retainEvidence(updated,file), updated);
});

test('readiness reports multiple unrelated CI errors together', () => {
  const readFile = path => {
    const source = readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');
    if (path === '.github/workflows/dev-release.yml') return source + '# drift\n';
    if (path === '.github/workflows/artifacts.yml') {
      return source.replaceAll('retention-days: 90','retention-days: 14').replace('platform: [Windows, Linux, Mac]', 'platform: [Windows, Linux]');
    }
    return source;
  };
  const issues = inspectReadiness({readFile});
  assert(issues.some(issue => issue.includes('dev-release.yml: Differs from its template')));
  assert(issues.some(issue => issue.includes('universal-release-assets needs retention-days: 90')));
  assert(issues.some(issue => issue.includes('wss-propagation-plan needs retention-days: 90')));
  assert(issues.some(issue => issue.includes('expected one required job named BLE Consumer for Mac')));
  assert.deepEqual(inspectReadiness(), []);
});
