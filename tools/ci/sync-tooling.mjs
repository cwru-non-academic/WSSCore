import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {readFileSync,writeFileSync,mkdirSync,existsSync,realpathSync} from 'node:fs';
import {dirname,resolve,relative,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {canonical,requireThat} from '../release/verify.mjs';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const manifest=JSON.parse(readFileSync(resolve(root,'tools/ci/sync-tooling.json'),'utf8'));
const lockPath='.github/scripts/wss-ci.lock.json';
const git=(...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim();
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const origins=manifest.origins||{};
const immutableBytes=new Map();
const sourceDescriptor=value=>typeof value==='string'?{origin:'core',path:value}:value;
export function sourceBytes(value,workspace=resolve(root,'.ci-origins'),coreRevision) {
  const entry=sourceDescriptor(value);
  requireThat(entry && typeof entry.path==='string' && (entry.origin==='core'||Object.hasOwn(origins,entry.origin)),'Unknown canonical CI origin');
  const origin=entry.origin==='core'?null:origins[entry.origin];
  const sourceRoot=origin?safePath(workspace,origin.workspace):root;
  const revision=origin?origin.commit:(coreRevision||git('rev-parse','HEAD'));
  requireThat(/^[a-f0-9]{40}$/.test(revision),'Source bytes require an immutable commit');
  if(origin)requireThat(origin.repository===`cwru-non-academic/${origin.workspace}` && /^[a-f0-9]{40}$/.test(revision) && origin.ref===`refs/heads/ci/tooling/${revision}`,'Invalid canonical CI origin');
  safePath(sourceRoot,entry.path);
  const key=JSON.stringify([sourceRoot,revision,entry.path]);
  if(!immutableBytes.has(key))immutableBytes.set(key,execFileSync('git',['-C',sourceRoot,'show',`${revision}:${entry.path}`],{maxBuffer:32*1024*1024}));
  return Buffer.from(immutableBytes.get(key));
}
const blobId=bytes=>createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
const same=(a,b)=>canonical(a)===canonical(b);

function safePath(base,path) {
  requireThat(typeof path==='string' && !path.includes('\\') && !path.split('/').includes('..') && !path.startsWith('/'),'Invalid managed path');
  const full=resolve(base,path),rel=relative(base,full);
  requireThat(rel && !rel.startsWith(`..${sep}`) && !rel.includes(':'),'Managed path escapes checkout');
  // Never follow a preexisting reparse point outside its target checkout.
  let ancestor=full;
  while(!existsSync(ancestor)) ancestor=dirname(ancestor);
  const real=realpathSync(ancestor),relReal=relative(realpathSync(base),real);
  requireThat(!relReal.startsWith('..') && !relReal.includes(':'),'Managed path follows an external link');
  return full;
}

export function expectedLock(repository,commit,workspace=resolve(root,'.ci-origins')) {
  requireThat(manifest.repositories.includes(repository),'Unknown WSS target repository');
  const entries=Object.entries(manifest.files).filter(([,value])=>{
    const scope=sourceDescriptor(value).repositories;
    requireThat(scope===undefined || Array.isArray(scope)&&scope.length>0&&new Set(scope).size===scope.length&&scope.every(repo=>manifest.repositories.includes(repo)),'Invalid shared CI target scope');
    return scope===undefined||scope.includes(repository);
  });
  return {schema:manifest.schema,repository:`cwru-non-academic/${repository}`,source_repository:'cwru-non-academic/WSSCore',source_commit:commit,
    source_ref:`refs/heads/ci/tooling/${commit}`,
    origins:structuredClone(origins),
    files:Object.fromEntries(entries.map(([target,value])=>{const entry=sourceDescriptor(value);return [target,{origin:entry.origin,source:entry.path,sha256:hash(sourceBytes(entry,workspace,commit))}];}))};
}

export function check(repository,targetRoot,workspace=resolve(root,'.ci-origins')) {
  const actual=JSON.parse(readFileSync(safePath(targetRoot,lockPath),'utf8'));
  const commit=git('rev-parse','HEAD');
  requireThat(same(actual,expectedLock(repository,commit,workspace)),`Tooling lock differs from immutable CI sources at Core ${commit}`);
  const entries=Object.entries(actual.files);
  for(const [path] of entries){safePath(targetRoot,path);requireThat(!/[\r\n]/.test(path),'Invalid managed Git input path');}
  // One Git invocation preserves its clean filters for every managed path.
  const blobs=execFileSync('git',['-C',targetRoot,'hash-object','--stdin-paths'],{input:entries.map(([path])=>path).join('\n')+'\n',encoding:'utf8'}).trim().split(/\r?\n/);
  requireThat(blobs.length===entries.length,'Incomplete managed source hashes');
  entries.forEach(([path,entry],index)=>requireThat(blobs[index]===blobId(sourceBytes({origin:entry.origin,path:entry.source},workspace,commit)),`Shared CI drift: ${repository}/${path}`));
  console.log(`Verified ${repository}: ${Object.keys(actual.files).length} identical common files from ${commit}`);
}

function copy(workspace,originsWorkspace=workspace) {
  requireThat(git('status','--porcelain','--untracked-files=all','--','tools/ci','tools/release')==='','Commit canonical tooling before synchronization');
  const commit=git('rev-parse','HEAD');
  for(const repository of manifest.repositories) {
    const targetRoot=safePath(workspace,repository);
    requireThat(existsSync(resolve(targetRoot,'.git')),`Missing target checkout: ${repository}`);
    const lock=expectedLock(repository,commit,originsWorkspace);
    for(const [path,entry] of Object.entries(lock.files)) {
      const target=safePath(targetRoot,path);mkdirSync(dirname(target),{recursive:true});
      writeFileSync(target,sourceBytes({origin:entry.origin,path:entry.source},originsWorkspace,commit));
    }
    writeFileSync(safePath(targetRoot,lockPath),JSON.stringify(lock,null,2)+'\n');
    check(repository,targetRoot,originsWorkspace);
  }
}

if(isEntrypoint(import.meta.url)) {
  const [command,arg,target,originsWorkspace]=process.argv.slice(2);
  if(command==='copy'&&arg&&!originsWorkspace)copy(resolve(arg),target?resolve(target):resolve(arg));
  else if(command==='check'&&arg&&target)check(arg,resolve(target),originsWorkspace?resolve(originsWorkspace):undefined);
  else throw new Error('Usage: sync-tooling.mjs copy <workspace> [origins-workspace] | check <repository> <checkout> [origins-workspace]');
}
