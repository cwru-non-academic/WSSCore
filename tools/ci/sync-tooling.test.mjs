import {test} from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,rmSync,readFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {expectedLock,check,sourceBytes} from './sync-tooling.mjs';

test('synchronized consumer rejects code drift and a forged tooling lock',()=>{
  const root=mkdtempSync(join(tmpdir(),'wss-tooling-'));
  const repository='Python_WSS_Integration_Lib';
  const core=new URL('../..',import.meta.url);
  const sourceRoot=fileURLToPath(core);
  const git=(...args)=>execFileSync('git',['-C',sourceRoot,...args]);
  try {
    execFileSync('git',['init',root],{stdio:'ignore'});
    const commit=git('rev-parse','HEAD').toString().trim();
    const lock=expectedLock(repository,commit);
    for(const [path,entry] of Object.entries(lock.files)) {
      mkdirSync(dirname(join(root,path)),{recursive:true});writeFileSync(join(root,path),sourceBytes({origin:entry.origin,path:entry.source}));
    }
    const lockPath=join(root,'.github/scripts/wss-ci.lock.json');
    writeFileSync(lockPath,JSON.stringify(lock));
    check(repository,root);
    const path=join(root,'.github/scripts/wss-ci/verify.mjs'),original=readFileSync(path);
    writeFileSync(path,Buffer.concat([original,Buffer.from('\nthrow Error("drift");\n')]));
    assert.throws(()=>check(repository,root),/Shared CI drift/);
    writeFileSync(path,original);
    lock.origins.integration.commit='0'.repeat(40);
    writeFileSync(lockPath,JSON.stringify(lock));
    assert.throws(()=>check(repository,root),/Tooling lock differs/);
    lock.origins.integration.commit=expectedLock(repository,commit).origins.integration.commit;
    lock.files['.github/scripts/wss-ci/promotion.mjs'].origin='core';
    writeFileSync(lockPath,JSON.stringify(lock));
    assert.throws(()=>check(repository,root),/Tooling lock differs/);
    lock.files['.github/scripts/wss-ci/promotion.mjs'].origin='integration';
    lock.files['.github/scripts/wss-ci/verify.mjs'].sha256='0'.repeat(64);
    writeFileSync(lockPath,JSON.stringify(lock));
    assert.throws(()=>check(repository,root),/Tooling lock differs/);
    assert.throws(()=>expectedLock('unrelated-repository',commit),/Unknown WSS/);
    assert.throws(()=>sourceBytes({origin:'unreviewed',path:'unexpected'}),/Unknown canonical CI origin/);
  } finally {rmSync(root,{recursive:true,force:true});}
});
