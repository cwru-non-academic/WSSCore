import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {readFileSync,writeFileSync,mkdirSync,existsSync,realpathSync} from 'node:fs';
import {dirname,resolve,relative,sep} from 'node:path';
import {fileURLToPath} from 'node:url';
import {canonical,requireThat} from '../release/verify.mjs';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const manifest=JSON.parse(readFileSync(resolve(root,'tools/ci/sync-tooling.json'),'utf8'));
const lockPath='.github/scripts/wss-ci.lock.json';
const git=(...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8'}).trim();
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const sourceBytes=path=>execFileSync('git',['-C',root,'show',`HEAD:${path}`],{maxBuffer:32*1024*1024});
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

export function expectedLock(repository,commit) {
  requireThat(manifest.repositories.includes(repository),'Unknown WSS target repository');
  return {schema:manifest.schema,repository:`cwru-non-academic/${repository}`,source_repository:'cwru-non-academic/WSSCore',source_commit:commit,
    files:Object.fromEntries(Object.entries(manifest.files).map(([source,target])=>[target,{source,sha256:hash(sourceBytes(source))}]))};
}

export function check(repository,targetRoot) {
  const actual=JSON.parse(readFileSync(safePath(targetRoot,lockPath),'utf8'));
  const commit=git('rev-parse','HEAD');
  requireThat(same(actual,expectedLock(repository,commit)),`Tooling lock differs from immutable Core ${commit}`);
  for(const [path,entry] of Object.entries(actual.files)) {
    // Git's clean filters own LF/CRLF; compare canonical blobs, not OS checkout bytes.
    const target=safePath(targetRoot,path);
    const actualBlob=execFileSync('git',['-C',targetRoot,'hash-object',`--path=${path}`,target],{encoding:'utf8'}).trim();
    requireThat(actualBlob===blobId(sourceBytes(entry.source)),`Shared CI drift: ${repository}/${path}`);
  }
  console.log(`Verified ${repository}: ${Object.keys(actual.files).length} identical common files from ${commit}`);
}

function copy(workspace) {
  requireThat(git('status','--porcelain','--untracked-files=all','--','tools/ci','tools/release')==='','Commit canonical tooling before synchronization');
  const commit=git('rev-parse','HEAD');
  for(const repository of manifest.repositories) {
    const targetRoot=safePath(workspace,repository);
    requireThat(existsSync(resolve(targetRoot,'.git')),`Missing target checkout: ${repository}`);
    const lock=expectedLock(repository,commit);
    for(const [path,entry] of Object.entries(lock.files)) {
      const target=safePath(targetRoot,path);mkdirSync(dirname(target),{recursive:true});
      writeFileSync(target,sourceBytes(entry.source));
    }
    writeFileSync(safePath(targetRoot,lockPath),JSON.stringify(lock,null,2)+'\n');
    check(repository,targetRoot);
  }
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const [command,arg,target]=process.argv.slice(2);
  if(command==='copy' && arg && !target) copy(resolve(arg));
  else if(command==='check' && arg && target) check(arg,resolve(target));
  else throw new Error('Usage: sync-tooling.mjs copy <workspace> | check <repository> <checkout>');
}
