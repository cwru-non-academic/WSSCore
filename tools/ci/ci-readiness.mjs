import {appendFileSync, readFileSync} from 'node:fs';
import {classifyWorkflowSources, inspectStageGuardPins, stageGuardPolicies} from './adapter-pins.mjs';
import {isMap, isSeq, parseDocument} from './vendor/yaml/dist/index.js';
import {channelActivation, publicationControls} from './channel-controls.mjs';
import {renderedCoreWorkflows} from './channel-workflows.mjs';
import {policy as stagingPolicy} from './dev-propagation.mjs';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {channelContext} from '../release/channels.mjs';
import {testSelectionPolicy} from '../release/campaign-test-selection.mjs';
import {GitHub} from '../release/github.mjs';
import {checkTagGuard} from '../release/policy.mjs';
import {canonical, CORE, CORE_JOBS, manifest, requireThat} from '../release/verify.mjs';

const root = new URL('../../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');
export const evidenceRetentionDays = 90;
const preflightProof = /^(wss-candidate-provenance|wss-macos-(cli|wrapper)-preflight-.+|(python|unity)-preflight-(evidence|platform-.+)|matlab-preflight-(evidence|compatibility-macOS))$/;

function workflowJobs(text) {
  const doc = parseDocument(text);
  requireThat(!doc.errors.length, doc.errors.map(error => error.message).join('; '));
  const jobs = doc.get('jobs');
  requireThat(isMap(jobs), 'Expected workflow jobs.');
  return jobs;
}

function evidenceUploads(text, file) {
  const uploads = [];
  for (const {value:job} of workflowJobs(text).items) {
    const steps = job?.get('steps');
    if (!isSeq(steps)) continue;
    for (const step of steps.items) {
      if (!isMap(step) || !/^actions\/upload-artifact@/.test(step.get('uses') ?? '')) continue;
      const settings = step.get('with');
      if (!isMap(settings)) continue;
      const name = settings.get('name');
      if (typeof name !== 'string' || !(
        preflightProof.test(name) ||
        name.includes("'unity-preflight-evidence'") || name.includes("'python-preflight-evidence'") ||
        file === 'release-receipt.yml' && name.includes('steps.receipt.outputs.artifact_name') ||
        file === 'artifacts.yml' && ['universal-release-assets', 'wss-propagation-plan', '${{ steps.channel.outputs.stage_artifact }}'].includes(name)
      )) continue;
      if (settings.get('retention-days') !== evidenceRetentionDays) uploads.push({name, settings});
    }
  }
  return uploads;
}

function coreJobNames(text) {
  return Object.values(workflowJobs(text).toJSON()).flatMap(job => {
    const variable = job.name?.match(/\$\{\{ matrix\.(\w+) \}\}/);
    if (!variable) return [job.name];
    const matrix = job.strategy?.matrix;
    const values = matrix?.[variable[1]] ?? matrix?.include?.map(row => row[variable[1]]);
    return Array.isArray(values) ? values.map(value => job.name.replace(variable[0], value)) : [];
  });
}

export function retentionIssues(text, file) {
  return evidenceUploads(text, file).map(({name}) => `${file}: upload ${name} needs retention-days: ${evidenceRetentionDays}`);
}

export function retainEvidence(text, file) {
  const newline = text.includes('\r\n') ? '\r\n' : '\n';
  const edits = evidenceUploads(text, file).map(({settings}) => {
    const current = settings.get('retention-days', true);
    if (current) return {from:current.range[0], to:current.range[1], value:String(evidenceRetentionDays)};
    requireThat(!settings.flow, `${file}: expected a block mapping for upload options.`);
    const keyStart = settings.items.find(pair => pair.key.value === 'name').key.range[0];
    const start = text.lastIndexOf('\n', keyStart) + 1;
    return {from:start, to:start, value:`${text.slice(start, keyStart)}retention-days: ${evidenceRetentionDays}${newline}`};
  });
  for (const {from,to,value} of edits.sort((a,b) => b.from - a.from)) text = text.slice(0, from) + value + text.slice(to);
  return text;
}

export function inspectReadiness({readFile=read, channel, github, env=process.env} = {}) {
  const issues = [];
  function collect(label, inspect) {
    try { inspect(); } catch (error) { issues.push(`${label}: ${error.message}`); }
  }
  collect('Generated Core workflows', () => {
    for (const expected of renderedCoreWorkflows()) collect(expected.path, () => {
      requireThat(readFile(expected.path).replace(/\r\n/g, '\n') === expected.text,
        'Differs from its template. Run node tools/ci/channel-workflows.mjs write.');
    });
  });
  collect('Core artifact workflow', () => {
    const workflow = readFile('.github/workflows/artifacts.yml');
    issues.push(...retentionIssues(workflow, 'artifacts.yml'));
    const names = coreJobNames(workflow);
    for (const name of CORE_JOBS) {
      if (names.filter(actual => actual === name).length !== 1) {
        issues.push(`artifacts.yml: expected one required job named ${name}.`);
      }
    }
  });
  collect('Core artifact workflow source bindings', () => {
    const roles = JSON.parse(readFile('tools/ci/executable-pin-roles.json'));
    roles.source_catalogue = roles.source_catalogue.filter(item => item.target === CORE && item.workflow === 'artifacts.yml');
    classifyWorkflowSources({repository:CORE, files:{'artifacts.yml':readFile('.github/workflows/artifacts.yml')}, roles});
  });
  const staged = new Map();
  for (const branch of ['dev','main']) {
    collect(`Core ${branch} policies`, () => {
      const context = channelContext(branch);
      const release = JSON.parse(readFile(context.targetPolicy));
      const stage = JSON.parse(readFile(context.stagePolicy));
      manifest(release);
      stagingPolicy(stage);
      requireThat(stage.channel === branch && release.channel === branch, 'Policy file has the wrong channel.');
      requireThat(canonical(testSelectionPolicy(stage)) === canonical(testSelectionPolicy(release)), 'Stage and acceptance test selections differ.');
      const stagedRepos = stage.libraries.flatMap(item => [item.repository, ...item.applications]).sort();
      requireThat(canonical(stagedRepos) === canonical(release.targets.map(item => item.repository).sort()), 'Stage and acceptance target repositories differ.');
      staged.set(branch, {context, stage, release});
    });
  }
  if (!github || !staged.has(channel)) return issues;

  const {context, stage, release} = staged.get(channel);
  collect(`Core ${channel} activation`, () => requireThat(channelActivation(JSON.parse(readFile('tools/ci/channel-activation.json')))[channel],
    'The promotion channel is disabled.'));
  const switches = publicationControls();
  for (const key of new Set([switches.acceptance, switches[channel]])) {
    collect(`Core variable ${key}`, () => requireThat(['','true','false'].includes(env[key] ?? ''), 'Expected unset, true, or false.'));
  }
  if (channel === 'main' || testSelectionPolicy(release)) {
    collect(`Core variable ${switches.acceptance}`, () => requireThat(env[switches.acceptance] === 'true',
      'The candidate needs stage and acceptance plans. Set this to true.'));
  }
  if (env[switches[channel]] === 'true') collect(`Core ${channel} publication`, () => checkTagGuard(github, release));

  const registrations = new Map();
  function add(repository, ...workflows) {
    if (!registrations.has(repository)) registrations.set(repository, new Set());
    for (const workflow of workflows) registrations.get(repository).add(workflow);
  }
  add(CORE, context.stageWorkflow, context.acceptanceWorkflow, ...['unity','python','matlab'].map(family => `.github/workflows/propagate-${family}.yml`));
  for (const target of release.targets) add(target.repository, target.workflow, '.github/workflows/wss-preflight.yml', '.github/workflows/release-receipt.yml');
  for (const library of stage.libraries) {
    add(library.repository, ...[library.normal, library.fanout, 'promote-wss.yml', ...(stage.application_fanout === 'per-integration' ? ['notify-core-stage.yml'] : [])]
      .map(file => `.github/workflows/${file}`));
    for (const repository of library.applications) add(repository, '.github/workflows/promote-integration.yml');
  }
  for (const [repository, workflows] of registrations) {
    collect(`${repository} registered workflows`, () => {
      const entries = github.pages(`repos/${repository}/actions/workflows`, 'workflows');
      for (const path of workflows) {
        if (entries.filter(item => item.path === path && item.state === 'active').length !== 1) {
          issues.push(`${repository}/${path}: expected one active workflow on the default branch.`);
        }
      }
    });
    if (repository !== CORE) collect(`${repository}/${channel}/.github/workflows/wss-preflight.yml`, () => {
      const file = github.get(`repos/${repository}/contents/.github/workflows/wss-preflight.yml?ref=${channel}`);
      requireThat(file.type === 'file' && file.encoding === 'base64', 'Expected a deployed candidate workflow.');
      const text = Buffer.from(file.content, 'base64').toString('utf8');
      issues.push(...retentionIssues(text, 'wss-preflight.yml').map(issue => `${repository}/${channel}/.github/workflows/${issue}`));
    });
  }
  collect('Reviewed MATLAB workflow pins', () => {
    issues.push(...inspectStageGuardPins({policies:stageGuardPolicies()}, github).issues);
  });
  for (const repository of release.targets.filter(item => item.id.startsWith('matlab-')).map(item => item.repository)) {
    for (const branch of ['dev','main']) collect(`${repository}/${branch}/.github/matlab-ci.json`, () => {
      const file = github.get(`repos/${repository}/contents/.github/matlab-ci.json?ref=${branch}`);
      requireThat(file.type === 'file' && file.encoding === 'base64', 'Expected deployed MATLAB CI configuration.');
      const config = JSON.parse(Buffer.from(file.content, 'base64'));
      for (const key of ['baseline_ready','candidate_preflight_ready']) {
        if (config[key] !== true) issues.push(`${repository}/${branch}/.github/matlab-ci.json: set ${key} to true.`);
      }
    });
  }
  return issues;
}

function main() {
  const [mode, workspace, ...extra] = process.argv.slice(2);
  requireThat(!extra.length && (['local','live','repositories'].includes(mode) && !workspace || mode === 'retention' && workspace),
    'Usage: ci-readiness.mjs local | live | repositories | retention <checkout>');
  if (mode === 'repositories') {
    const context = channelContext(process.env.WSS_CI_CHANNEL);
    const release = JSON.parse(read(context.targetPolicy));
    manifest(release);
    console.log(`repositories=${[CORE, ...release.targets.map(target => target.repository)].map(repository => repository.split('/')[1]).join(',')}`);
    return;
  }
  const channel = mode === 'live' ? channelContext(process.env.WSS_CI_CHANNEL).branch : undefined;
  const issues = mode === 'retention' ? [] : inspectReadiness({channel, github:channel ? new GitHub() : undefined});
  if (mode === 'retention') {
    for (const file of ['wss-preflight.yml','_python-validation.yml','_python-platform-validation.yml','_unity-validation.yml','_unity-platform-validation.yml','release-receipt.yml']) {
      try { issues.push(...retentionIssues(readFileSync(`${workspace}/.github/workflows/${file}`, 'utf8'), file)); }
      catch (error) { if (error.code !== 'ENOENT') issues.push(`${file}: ${error.message}`); }
    }
  }
  if (issues.length) {
    const summary = issues.map(issue => `- ${issue}`).join('\n');
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## CI configuration issues\n\n${summary}\n`);
    console.error(summary);
    process.exitCode = 1;
  } else console.log('CI configuration checks passed.');
}

if (isEntrypoint(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
