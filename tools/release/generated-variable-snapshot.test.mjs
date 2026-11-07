import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join,dirname} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {expectedLock,sourceBytes} from '../ci/sync-tooling.mjs';
import {createSnapshotRequest,SNAPSHOT_WORKFLOW,snapshotName} from './local-test-variable-snapshot.mjs';
import {CORE} from './verify.mjs';
const sha=n=>String(n).repeat(40);
test('generated receiver capture runs from only its registered closure and enforces safe named vars',()=>{
 const root=mkdtempSync(join(tmpdir(),'wss-local-capture-')),repository='Python_WSS_Integration_Lib';
 try{
  const lock=expectedLock(repository,sha(1));
  assert.ok(lock.files[SNAPSHOT_WORKFLOW]);
  for(const [path,entry] of Object.entries(lock.files)){
   mkdirSync(dirname(join(root,path)),{recursive:true});writeFileSync(join(root,path),sourceBytes({origin:entry.origin,path:entry.source}));
  }
  const workflow=readFileSync(join(root,SNAPSHOT_WORKFLOW),'utf8');
  assert.doesNotMatch(workflow,/environment:|secrets\.|create-github-app-token|WSS_GITHUB_APP_PRIVATE_KEY|overwrite: true/);
  assert.deepEqual([...workflow.matchAll(/vars\.([A-Z_]+)/g)].map(m=>m[1]),['WSS_USE_PIN_TESTS','WSS_PIN_TEST_REPOSITORY','WSS_PIN_TEST_COMMIT']);
  const r=createSnapshotRequest({schema:'wss-local-test-variable-request/v1',channel:'dev',producer:{repository:CORE,commit:sha(1),run_id:'10',attempt:1},target:{id:'python-wrapper',repository:`cwru-non-academic/${repository}`},source:{ref:'refs/heads/dev',commit:sha(2),workflow:SNAPSHOT_WORKFLOW,workflow_blob:sha(3),actor_id:'123'},nonce:'a'.repeat(64)});
  const event=join(root,'event.json'),output=join(root,'output'),snapshot=join(root,'snapshot.json');writeFileSync(event,JSON.stringify({inputs:{request:JSON.stringify(r)}}));
  const env={...process.env,GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:r.target.repository,GITHUB_EVENT_NAME:'workflow_dispatch',GITHUB_REF:r.source.ref,GITHUB_SHA:sha(2),GITHUB_WORKFLOW_SHA:sha(2),GITHUB_WORKFLOW_REF:`${r.target.repository}/${SNAPSHOT_WORKFLOW}@${r.source.ref}`,GITHUB_ACTOR_ID:'123',GITHUB_RUN_ID:'20',GITHUB_RUN_ATTEMPT:'1',GITHUB_EVENT_PATH:event,GITHUB_OUTPUT:output,WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:r.target.repository,WSS_PIN_TEST_COMMIT:sha(4)};
  const cli=join(root,'.github/scripts/wss-ci/capture-test-variables.mjs');
  execFileSync(process.execPath,[cli,snapshot],{env});
  const captured=JSON.parse(readFileSync(snapshot));assert.equal(captured.settings.WSS_PIN_TEST_COMMIT,sha(4));assert.match(readFileSync(output,'utf8'),new RegExp(snapshotName(r)));
  assert.throws(()=>execFileSync(process.execPath,[cli,snapshot],{env,stdio:'pipe'}),/EEXIST/);
  assert.throws(()=>execFileSync(process.execPath,[cli,join(root,'later.json')],{env:{...env,GITHUB_RUN_ATTEMPT:'2'},stdio:'pipe'}),/attempt mismatch/);
  assert.throws(()=>execFileSync(process.execPath,[cli,join(root,'invalid.json')],{env:{...env,WSS_PIN_TEST_COMMIT:'$(touch injected)'},stdio:'pipe'}),/full lowercase/);
 }finally{rmSync(root,{recursive:true,force:true});}
});
