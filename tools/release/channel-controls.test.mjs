import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,writeFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {CORE} from './verify.mjs';
import {channelActivation,channelContext,controllerContext} from './channels.mjs';
const active=channelActivation({schema:'wss-ci-channel-activation/v1',dev:true,main:true});
const inactive=channelActivation({...active,main:false});
function environment(operation='verify'){
  return {WSS_CI_CHANNEL:'main',GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:CORE,GITHUB_REF:'refs/heads/main',GITHUB_SHA:'1'.repeat(40),GITHUB_WORKFLOW_SHA:'1'.repeat(40),GITHUB_EVENT_NAME:'workflow_dispatch',
    GITHUB_WORKFLOW_REF:`${CORE}/${operation.startsWith('stage-')?channelContext('main').stageWorkflow:channelContext('main').acceptanceWorkflow}@refs/heads/main`,WSS_DEV_ACCEPTANCE_ENABLED:'true',WSS_DEV_RELEASE_ENABLED:''};
}
test('temporary beta/stable publication alias is explicit and cannot grant activation',()=>{
  for(const channel of ['dev','main']){
    const c=channelContext(channel);assert.equal(c.acceptanceVariable,'WSS_DEV_ACCEPTANCE_ENABLED');assert.equal(c.publicationVariable,'WSS_DEV_RELEASE_ENABLED');
  }
  for(const op of ['prepare-dispatches','dispatch','publish','stage-dispatch'])assert.throws(()=>controllerContext({...environment(op),WSS_DEV_RELEASE_ENABLED:'true'},op,CORE,inactive),/not activated/);
  assert.equal(controllerContext(environment(),'verify',CORE,inactive).channel,'main');
  assert.equal(controllerContext(environment('stage-prepare'),'stage-prepare',CORE,inactive).channel,'main');
});
test('main publication still needs acceptance and release switches after activation',()=>{
  assert.throws(()=>controllerContext(environment('publish'),'publish',CORE,active),/Publication must/);
  assert.throws(()=>controllerContext({...environment('publish'),WSS_STABLE_RELEASE_ENABLED:'true'},'publish',CORE,active),/Publication must/);
  assert.throws(()=>controllerContext({...environment('publish'),WSS_DEV_RELEASE_ENABLED:'true',WSS_DEV_ACCEPTANCE_ENABLED:'false'},'publish',CORE,active),/Acceptance reconciliation/);
  // A pure configuration check; this performs no GitHub operation or release.
  assert.equal(controllerContext({...environment('publish'),WSS_DEV_RELEASE_ENABLED:'true'},'publish',CORE,active).kind,'stable');
});
test('main controller cannot select another workflow, branch, source, event or repository',()=>{
  for(const op of ['verify','dispatch','publish','stage-prepare','stage-dispatch'])for(const patch of [
    {WSS_CI_CHANNEL:'dev'},{WSS_CI_CHANNEL:'other'},{GITHUB_REPOSITORY:'other/Core'},{GITHUB_REF:'refs/heads/dev'},
    {GITHUB_WORKFLOW_REF:`${CORE}/.github/workflows/dev-release.yml@refs/heads/main`},{GITHUB_WORKFLOW_SHA:'2'.repeat(40)},
    {GITHUB_EVENT_NAME:'pull_request'},{GITHUB_ACTIONS:'false'},{GITHUB_WORKFLOW_REF:undefined}
  ])assert.throws(()=>controllerContext({...environment(op),WSS_DEV_RELEASE_ENABLED:'true',...patch},op,CORE,active));
});
test('activation configuration is closed, boolean and Core-owned',()=>{
  for(const value of [null,{}, {...active,main:'true'},{...active,dev:false},{...active,stable:true},{...active,schema:'unknown'}])assert.throws(()=>channelActivation(value));
});
test('actual main release CLI refuses disabled publication before any GitHub transport',()=>{
  const root=mkdtempSync(join(tmpdir(),'wss controller guard ')),preload=join(root,'no network.mjs');
  try{
    writeFileSync(preload,"import cp from 'node:child_process';import {syncBuiltinESMExports} from 'node:module';const original=cp.execFileSync;cp.execFileSync=(command,...args)=>{if(command==='gh')throw Error('Unexpected GitHub transport before controls');return original(command,...args);};syncBuiltinESMExports();\n");
    let failure;
    try{execFileSync(process.execPath,[fileURLToPath(new URL('./cli.mjs',import.meta.url)),'publish','11',join(root,'output')],{env:{...process.env,...environment('publish'),GH_TOKEN:'',GITHUB_TOKEN:'',NODE_OPTIONS:`--import=${pathToFileURL(preload).href}`},stdio:'pipe'});}catch(error){failure=error;}
    assert.ok(failure);assert.match(failure.stderr.toString(),/not activated|Publication must be explicitly enabled/);assert.doesNotMatch(failure.stderr.toString(),/Unexpected GitHub transport/);
  }finally{rmSync(root,{recursive:true,force:true});}
});
