// Preserve the legacy job names: pinned downstream validators still require them.
export const BLE_GROUPS = Object.freeze({
  Windows: ['BLE consumer (Windows)'],
  Linux: ['BLE consumer (Linux)'],
  Mac: ['BLE consumer (macOS arm64)', 'BLE consumer (macOS x86_64)'],
});

export function bleSummary(platform, jobs, runId, headSha) {
  if (!Object.hasOwn(BLE_GROUPS, platform) || !Array.isArray(jobs))
    throw new Error('Unknown BLE platform or missing attempt job evidence');
  const rows = BLE_GROUPS[platform].map(name => {
    const matches = jobs.filter(job => job.name === name);
    const job = matches[0];
    const valid = matches.length === 1 && String(job.run_id) === String(runId) &&
      job.head_sha === headSha && job.status === 'completed' && job.conclusion === 'success';
    return {name, passed: valid, outcome: matches.length === 0 ? 'missing' :
      matches.length !== 1 ? 'duplicate' :
      String(job.run_id) !== String(runId) || job.head_sha !== headSha ? 'wrong run/source' :
      `${job.status}/${job.conclusion ?? 'none'}`};
  });
  return {passed: rows.every(row => row.passed), rows};
}

export async function summarizeBle({github, context, core, platform, attempt}) {
  if (!Number.isSafeInteger(attempt) || attempt < 1) throw new Error('Invalid run attempt');
  const {data: run} = await github.rest.actions.getWorkflowRunAttempt({
    ...context.repo, run_id: context.runId, attempt_number: attempt});
  if (String(run.id) !== String(context.runId) || run.run_attempt !== attempt ||
      !/^[a-f0-9]{40}$/.test(run.head_sha)) throw new Error('Run attempt identity mismatch');
  // The attempt-specific endpoint prevents a previous successful leg from masking
  // a skipped/missing leg in this attempt. Never use filter=all/latest here.
  const jobs = await github.paginate(
    'GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs',
    {...context.repo, run_id: context.runId, attempt_number: attempt, per_page: 100});
  // PR context.sha can be the synthetic merge SHA; jobs use the run's head SHA.
  const result = bleSummary(platform, jobs, context.runId, run.head_sha);
  await core.summary.addHeading(`BLE Consumer for ${platform}`)
    .addTable([
      [{data: 'Required architecture/platform job', header: true}, {data: 'Current attempt outcome', header: true}],
      ...result.rows.map(row => [row.name, `${row.passed ? 'PASS' : 'FAIL'}: ${row.outcome}`]),
    ])
    .addRaw('\nPackaged backend/ABI and hardware-free contracts only; physical BLE and host permissions are not validated.\n')
    .write();
  for (const row of result.rows) core.info(`${row.name}: ${row.outcome}`);
  if (!result.passed) throw new Error(`BLE Consumer for ${platform} requires every unique current-attempt job to complete successfully`);
}
