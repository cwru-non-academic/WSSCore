import {execFileSync} from 'node:child_process';
import {lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync} from 'node:fs';
import {dirname, join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {refreshWorkflowPins} from './adapter-pins.mjs';
import {renderReceiptFromLock} from './channel-workflows.mjs';
import {retainEvidence} from './ci-readiness.mjs';
import {expectedLock, sourceBytes} from './sync-tooling.mjs';
import {channelContext} from '../release/channels.mjs';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {requireThat} from '../release/verify.mjs';

const core = fileURLToPath(new URL('../..', import.meta.url));
const lockPath = '.github/scripts/wss-ci.lock.json';
const workflowDirectory = '.github/workflows';
const git = (root, ...args) => execFileSync('git', ['-C', root, ...args], {encoding:'utf8'}).trim();

function localFile(root, path) {
  const file = join(root, path);
  let ancestor = file;
  while (!lstatSync(ancestor, {throwIfNoEntry:false})) ancestor = dirname(ancestor);
  requireThat(realpathSync(ancestor) === ancestor, `Expected a local managed file: ${file}`);
  return file;
}

async function prepare(workspace, repository, channel, originWorkspace, adapter) {
  const commit = git(core, 'rev-parse', 'HEAD');
  requireThat(!git(core, 'status', '--porcelain', '--untracked-files=all', '--', 'tools/ci', 'tools/release'),
    'Commit the reviewed Core tooling before generating receiver files.');
  const lock = expectedLock(repository, commit, originWorkspace);
  const profiles = JSON.parse(sourceBytes({origin:'integration', path:'.github/scripts/wss-ci/receipt-targets.json'}, originWorkspace));
  const profile = Object.values(profiles).find(item => item.target.repository === lock.repository);
  requireThat(profile, `No receiver profile for ${repository}`);

  let adapterLock;
  if (profile.role === 'wrapper') {
    requireThat(adapter === undefined, 'A library uses the shared receipt engine and needs no wrapper pin.');
  } else {
    requireThat(/^[a-f0-9]{40}$/.test(adapter ?? ''), 'An application requires the explicitly reviewed wrapper commit.');
    const owner = profiles[profile.wrapper].target.repository.split('/')[1];
    const ownerRoot = join(workspace, owner);
    requireThat(git(ownerRoot, 'rev-parse', `${adapter}^{commit}`) === adapter, 'Reviewed wrapper commit is unavailable.');
    adapterLock = JSON.parse(git(ownerRoot, 'show', `${adapter}:${lockPath}`));
    const expected = expectedLock(owner, commit, originWorkspace);
    for (const [path, entry] of Object.entries(expected.files)) {
      const actual = execFileSync('git', ['-C', ownerRoot, 'show', `${adapter}:${path}`]);
      requireThat(actual.equals(sourceBytes({origin:entry.origin, path:entry.source}, originWorkspace, commit)),
        `Reviewed wrapper ${owner}@${adapter} has stale managed bytes at ${path}`);
    }
  }

  const target = join(workspace, repository);
  const files = Object.fromEntries(readdirSync(join(target, workflowDirectory))
    .filter(file => /\.ya?ml$/.test(file))
    .map(file => [file, readFileSync(localFile(target, `${workflowDirectory}/${file}`), 'utf8')]));
  for (const [path, entry] of Object.entries(lock.files)) {
    if (!path.startsWith(workflowDirectory+'/')) continue;
    const file = path.slice(workflowDirectory.length+1);
    files[file] ??= sourceBytes({origin:entry.origin,path:entry.source}, originWorkspace, commit).toString('utf8');
  }
  const workflowFiles = refreshWorkflowPins({repository:lock.repository, profiles, lock, files, adapter, adapterLock, channel});
  const receipt = await renderReceiptFromLock(repository, lock, originWorkspace, adapter);
  const proposed = new Map([
    ...Object.entries(lock.files).map(([path, file]) => [path,
      sourceBytes({origin:file.origin, path:file.source}, originWorkspace, commit)]),
    [lockPath, Buffer.from(JSON.stringify(lock, null, 2)+'\n')],
    ...Object.entries(workflowFiles).map(([file,text]) => [file, retainEvidence(text, file)])
      .filter(([file,text]) => text !== files[file])
      .map(([file,text]) => [`${workflowDirectory}/${file}`, Buffer.from(text)]),
    [receipt.path, Buffer.from(receipt.text)]
  ]);
  // Git's clean filter handles CRLF in a checkout without rewriting identical files.
  const changes = [];
  for (const [path, bytes] of proposed) {
    const file = localFile(target, path);
    const hash = input => execFileSync('git', ['-C', target, 'hash-object', '--stdin', `--path=${path}`], {input, encoding:'utf8'}).trim();
    if (!lstatSync(file, {throwIfNoEntry:false}) || hash(readFileSync(file)) !== hash(bytes)) changes.push({path, file, bytes});
  }
  return changes;
}

async function main() {
  const [mode, workspace, repository, channel, originWorkspace, adapter, ...extra] = process.argv.slice(2);
  requireThat(['check','apply'].includes(mode) && workspace && repository && originWorkspace && !extra.length,
    'Usage: receiver-ci.mjs check|apply <workspace> <repository> <dev|main> <origins-workspace> [reviewed-wrapper-commit]');
  const changes = await prepare(realpathSync(resolve(workspace)), repository, channelContext(channel).branch,
    realpathSync(resolve(originWorkspace)), adapter);
  for (const change of changes) {
    if (mode === 'apply') {
      mkdirSync(dirname(change.file), {recursive:true});
      writeFileSync(change.file, change.bytes);
    }
    console.log(`${repository}/${change.path}`);
  }
  if (mode === 'check' && changes.length) {
    console.error(`${changes.length} generated CI files differ. Run the same command with apply and review the diff.`);
    process.exitCode = 1;
  } else {
    console.log(`${repository}/${channel}: ${changes.length ? `updated ${changes.length} generated CI files` : 'generated CI is current'}`);
  }
}

if (isEntrypoint(import.meta.url)) {
  try { await main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
