import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {inspectInstallation,metadataClient} from './test-installation-scope.mjs';
const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url),'utf8'));
const workflow=readFileSync(new URL('../../.github/workflows/test-installation-scope.yml',import.meta.url),'utf8').replace(/\r\n/g,'\n');
const options={installationId:'160160114',appSlug:'cwru-wss-release-automation',policy};
const row=(id,name=`repo-${id}`,visibility='public')=>({id,full_name:`cwru-non-academic/${name}`,visibility,private:visibility!=='public'});
const response=rows=>({total_count:rows.length,repositories:rows});
const run=get=>inspectInstallation({...options,get});
const rejects=async get=>assert.rejects(run(get),error=>{
  assert.equal(error.message,'Installation scope verification failed; no private response was published');return true;
});
test('two complete paginated snapshots disclose public names and only nonpublic counts',async()=>{
  const rows=Array.from({length:100},(_,i)=>row(i+1));
  rows.push(row(101,'secret-project','private'),row(102,'internal-project','internal'),row(103,'CSharp_WSS_Integration_Lib'));
  const paths=[];
  const result=await run(async path=>{paths.push(path);const page=Number(path.split('page=').at(-1));return {total_count:103,repositories:rows.slice((page-1)*100,page*100)};});
  assert.deepEqual(paths,[1,2,1,2].map(p=>`installation/repositories?per_page=100&page=${p}`));
  assert.equal(result.installation_id,'160160114');assert.equal(result.repository_selection,'unknown');
  assert.equal(result.total_count,103);assert.equal(result.nonpublic_count,2);assert.equal(result.public_repositories.length,101);
  assert.doesNotMatch(JSON.stringify(result),/secret-project|internal-project/);
  assert.equal(result.known_targets.length,9);assert.equal(result.known_targets.filter(t=>t.present).length,1);
});
test('empty and exact page-sized installation inventories are complete',async()=>{
  for(const size of [0,100,200]){
    const rows=Array.from({length:size},(_,i)=>row(i+1));let calls=0;
    const result=await run(async path=>{calls++;const page=Number(path.split('page=').at(-1));return {total_count:size,repositories:rows.slice((page-1)*100,page*100)};});
    assert.equal(result.total_count,size);assert.equal(calls,2*Math.max(1,size/100));
  }
});
test('wrong identity or target registry fails before any API read',async()=>{
  for(const patch of [{installationId:'1'},{installationId:160160114},{appSlug:'other-app'},{policy:{...policy,targets:policy.targets.slice(1)}}]){
    let called=false;await assert.rejects(inspectInstallation({...options,...patch,get(){called=true;}}));assert.equal(called,false);
  }
});
test('missing, incomplete, malformed, duplicate and conflicting identity responses fail closed',async()=>{
  for(const value of [null,{}, {repositories:[]},{total_count:-1,repositories:[]},{total_count:1,repositories:[]},{total_count:0,repositories:[row(1)]},response([row(1),row(1)]),response([row(1),row(2,'REPO-1')]),response([{...row(1),id:'1'}]),response([{...row(1),full_name:'other/private-project'}]),response([{...row(1),visibility:'unknown'}]),response([{...row(1),private:true}]),response([{...row(1),visibility:undefined}])])await rejects(async()=>value);
});
test('changing total across pages or identity across complete snapshots cannot establish scope',async()=>{
  let calls=0;await rejects(async()=>++calls===1?{total_count:101,repositories:Array.from({length:100},(_,i)=>row(i+1))}:{total_count:102,repositories:[row(101),row(102)]});
  calls=0;await rejects(async()=>response([row(++calls)]));
});
test('transport, JSON and HTTP failures never expose response data or secrets',async()=>{
  await rejects(async()=>{throw Error('hidden-private-name secret-token');});
  for(const fetchImpl of [async()=>{throw Error('secret-token');},async()=>({ok:false,json(){throw Error('secret-body');}}),async()=>({ok:true,json(){throw Error('secret-json');}})]){
    await assert.rejects(metadataClient('secret-token',fetchImpl)('installation/repositories?per_page=100&page=1'),error=>{assert.doesNotMatch(error.message,/secret-token|secret-body|secret-json/);return true;});
  }
});
test('metadata transport permits only fixed-origin repository inventory GETs without redirects',async()=>{
  const calls=[];const get=metadataClient('token',async(url,init)=>{calls.push({url,init});return {ok:true,json:async()=>response([])};});
  await get('installation/repositories?per_page=100&page=1');
  assert.equal(calls[0].url,'https://api.github.com/installation/repositories?per_page=100&page=1');
  assert.equal(calls[0].init.method,'GET');assert.equal(calls[0].init.redirect,'error');assert.equal(calls[0].init.body,undefined);
  for(const path of ['app/installations/160160114','repos/cwru-non-academic/WSSCore/actions/variables','https://evil.example','installation/repositories?per_page=100&page=1&extra=1'])await assert.rejects(get(path));
  assert.equal(calls.length,1);
});
test('workflow has only explicit metadata grant, unrestricted existing installation inventory, and no PR secrets',()=>{
  assert.match(workflow,/workflow_dispatch:/);assert.doesNotMatch(workflow,/pull_request|workflow_run/);
  assert.match(workflow,/branches: \[dev, main\]/);assert.match(workflow,/github\.repository == 'cwru-non-academic\/WSSCore'/);
  assert.match(workflow,/github\.ref == 'refs\/heads\/dev'.*github\.ref == 'refs\/heads\/main'/);
  assert.match(workflow,/actions\/create-github-app-token@fee1f7d63c2ff003460e3d139729b119787bc349/);
  assert.deepEqual([...workflow.matchAll(/permission-([a-z-]+):\s*(\w+)/g)].map(m=>m.slice(1)),[['metadata','read']]);
  assert.match(workflow,/owner: cwru-non-academic/);assert.match(workflow,/permissions:\n  contents: read/);
  assert.match(workflow,/persist-credentials: false/);
  assert.match(workflow,/WSS_INSTALLATION_ID: \$\{\{ steps.installation.outputs.installation-id \}\}/);
  assert.match(workflow,/WSS_APP_SLUG: \$\{\{ steps.installation.outputs.app-slug \}\}/);
  assert.doesNotMatch(workflow,/repositories:|INPUT_PERMISSION|permission-variables|write|upload-artifact|continue-on-error|skip-token-revoke|test-variable-readiness/);
});
