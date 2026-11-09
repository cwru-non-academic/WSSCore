import {test} from 'node:test';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,rmSync,readFileSync,renameSync,symlinkSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
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
    assert.ok(lock.files['.github/workflows/notify-core-stage.yml'],'Integration-only notifier must be copied to integrations');
    assert.equal(expectedLock('CLI_Python_WSS_Application',commit).files['.github/workflows/notify-core-stage.yml'],undefined,'Integration-only notifier must not become an application workflow');
    for(const [path,entry] of Object.entries(lock.files)) {
      mkdirSync(dirname(join(root,path)),{recursive:true});writeFileSync(join(root,path),sourceBytes({origin:entry.origin,path:entry.source}));
    }
    const lockPath=join(root,'.github/scripts/wss-ci.lock.json');
    writeFileSync(lockPath,JSON.stringify(lock));
    check(repository,root);
    // Exercise only the registered output: no source checkout or untracked
    // dependency can accidentally make the receiver import closure succeed.
    assert.ok(lock.files['.github/scripts/wss-ci/channels.mjs']);
    assert.ok(lock.files['.github/scripts/wss-ci/Channel.ps1']);
    const module=name=>pathToFileURL(join(root,'.github/scripts/wss-ci',name)).href;
    for(const name of ['channels.mjs','entrypoint.mjs','job-log.mjs','producer-jobs.mjs','verify.mjs','github.mjs','receipt.mjs','promotion.mjs','staging.mjs','preflight.mjs'])
      execFileSync(process.execPath,['--input-type=module','-e',`await import(${JSON.stringify(module(name))})`],{cwd:root,stdio:'pipe'});
    assert.equal(execFileSync(process.execPath,['--input-type=module','-e',`await import(${JSON.stringify(module('verify.mjs'))})`,'wss-policy-assertion-only'],{cwd:root,encoding:'utf8'}),'','Importing through an assertion sentinel must not enter the channel CLI');
    const alias=join(root,'copied tooling alias');
    symlinkSync(join(root,'.github/scripts/wss-ci'),alias,process.platform==='win32'?'junction':'dir');
    const producer=join(alias,'producer-jobs.mjs'),context=join(root,'producer context.json');
    writeFileSync(context,JSON.stringify({jobs:[],contract:'macos-ble-v1'}));
    assert.throws(()=>execFileSync(process.execPath,[producer,context],{cwd:root,stdio:'pipe'}),error=>{
      assert.equal(error.status,1);assert.match(error.stderr.toString(),/Required unique successful job missing/);return true;
    });
    assert.equal(execFileSync(process.execPath,['--input-type=module','--eval','await import('+JSON.stringify(module('producer-jobs.mjs'))+')','--',producer],{cwd:root,encoding:'utf8'}),'');
    const bridgeTest=join(root,'channel invocation.ps1');
    writeFileSync(bridgeTest,`param([string]$Bridge)\n$ErrorActionPreference='Stop'\n. $Bridge\nforeach($channel in @('dev','main')){if((Get-WssCiChannelContext $channel).channel -cne $channel){throw 'Wrong copied channel'}}\n`);
    execFileSync('pwsh',['-NoProfile','-NonInteractive','-File',bridgeTest,join(root,'.github/scripts/wss-ci/Channel.ps1')],{cwd:root,stdio:'pipe'});
    const channelPath=join(root,'.github/scripts/wss-ci/channels.mjs');
    renameSync(channelPath,channelPath+'.missing');
    assert.throws(()=>check(repository,root),/ENOENT|no such file|could not open/i);
    assert.throws(()=>execFileSync(process.execPath,['--input-type=module','-e',`await import(${JSON.stringify(module('verify.mjs'))})`],{cwd:root,stdio:'pipe'}));
    assert.throws(()=>execFileSync('pwsh',['-NoProfile','-NonInteractive','-File',bridgeTest,join(root,'.github/scripts/wss-ci/Channel.ps1')],{cwd:root,stdio:'pipe'}));
    renameSync(channelPath+'.missing',channelPath);
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

test('retained receipt owner imports the exact same Core module bytes as its distributed closure',()=>{
  const manifest=JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)));
  const modules=Object.entries(manifest.files).filter(([path,entry])=>entry.origin==='core'&&path.endsWith('.mjs'));
  assert.ok(modules.length>0);
  for(const [path,entry] of modules)assert.deepEqual(sourceBytes({origin:'integration',path}),sourceBytes(entry),`Immutable owner/Core helper drift: ${path}`);
});


test('candidate locks reuse canonical source definitions with explicit immutable mapped byte reads',()=>{
  const calls=[],bytes=Buffer.from('safe synthetic canonical bytes\n');
  const actual=expectedLock('Python_WSS_Integration_Lib','1'.repeat(40),'unused-noncanonical-directory',(entry,workspace,commit)=>{calls.push({entry,workspace,commit});return bytes;});
  assert.ok(calls.length>0);assert.ok(calls.some(c=>c.entry.origin==='core'));assert.ok(calls.some(c=>c.entry.origin==='integration'));
  assert.ok(calls.every(c=>c.workspace==='unused-noncanonical-directory'&&c.commit==='1'.repeat(40)));
  for(const value of Object.values(actual.files))assert.equal(value.sha256,createHash('sha256').update(bytes).digest('hex'));
});


test('CSharp root and restored inventory readers share the one canonical owner',()=>{
  const manifest=JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)));
  const root=manifest.files['.github/scripts/Universal-WssFiles.ps1'];
  const restored=manifest.files['.github/scripts/wss-ci/restored-assets/Universal-WssFiles.ps1'];
  assert.deepEqual(root,{origin:'core',path:restored.path,repositories:['CSharp_WSS_Integration_Lib']});
  assert.equal(restored.origin,'core');
  assert.deepEqual(sourceBytes(root),sourceBytes(restored));
  const commit=execFileSync('git',['-C',fileURLToPath(new URL('../..',import.meta.url)),'rev-parse','HEAD'],{encoding:'utf8'}).trim();
  const lock=expectedLock('CSharp_WSS_Integration_Lib',commit);
  assert.equal(lock.files['.github/scripts/Universal-WssFiles.ps1'].sha256,lock.files['.github/scripts/wss-ci/restored-assets/Universal-WssFiles.ps1'].sha256);
  assert.equal(expectedLock('CLI_CSharp_WSS_Application',commit).files['.github/scripts/Universal-WssFiles.ps1'],undefined);
});
