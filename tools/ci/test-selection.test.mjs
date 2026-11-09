import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawn} from 'node:child_process';
import {digest} from '../release/verify.mjs';
import {resolveTestSelection,verifyTestSelection,verifySelectedTestEvidence,resolveOrReuseTestSelection} from './test-selection.mjs';
const sha=n=>String(n).repeat(40),owner='cwru-non-academic/Python_WSS_Integration_Lib',fixture='cwru-non-academic/CSharp_WSS_Integration_Lib';
const input=(variables={},channel='dev')=>({channel,campaign:{repository:'cwru-non-academic/WSSCore',commit:sha(1),run_id:'42'},targets:[{id:'python-wrapper',repository:owner,default_test_repository:fixture,allowed_test_repositories:[fixture,owner],variables}]});
const g={head:(_repo,branch)=>branch==='dev'?sha(2):sha(3),get:path=>({sha:path.split('/').at(-1)})};
const pin={WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:owner,WSS_PIN_TEST_COMMIT:sha(4)};
const rejectNetwork={head:()=>{throw Error('Unexpected branch resolution');},get:()=>{throw Error('Unexpected commit lookup');}};
function resign(value){const {selection_id,...body}=value;return {...body,selection_id:digest(body)};}

test('unset and false select latest from the matching dev/main branch',()=>{
  for(const channel of ['dev','main']) for(const variables of [{},{WSS_USE_PIN_TESTS:''},{WSS_USE_PIN_TESTS:'false'}]) {
    const r=resolveTestSelection(input(variables,channel),g),s=r.targets['python-wrapper'];
    assert.equal(s.mode,'latest');assert.equal(s.tests.repository,fixture);
    assert.equal(s.requested_ref,`refs/heads/${channel}`);assert.equal(s.tests.commit,channel==='dev'?sha(2):sha(3));
  }
});
test('disabled pin ignores stale repository/commit settings',()=>{
  const r=resolveTestSelection(input({WSS_USE_PIN_TESTS:'false',WSS_PIN_TEST_REPOSITORY:'not/a/repository',WSS_PIN_TEST_COMMIT:'main'}),g);
  assert.equal(r.targets['python-wrapper'].tests.commit,sha(2));
});
test('enabled pin checks existence at configured allowed repository and exact SHA',()=>{
  const paths=[];const r=resolveTestSelection(input(pin),{...rejectNetwork,get:path=>{paths.push(path);return {sha:sha(4)};}});
  assert.deepEqual(paths,[`repos/${owner}/git/commits/${sha(4)}`]);
  assert.deepEqual(r.targets['python-wrapper'].tests,{repository:owner,commit:sha(4)});
});
test('pin true never falls back for missing, malformed or unapproved settings',()=>{
  for(const changes of [{WSS_PIN_TEST_REPOSITORY:undefined},{WSS_PIN_TEST_COMMIT:undefined},{WSS_PIN_TEST_REPOSITORY:'foreign/repo'},{WSS_PIN_TEST_COMMIT:'dev'},{WSS_PIN_TEST_COMMIT:'abc1234'},{WSS_PIN_TEST_COMMIT:'A'.repeat(40)}]) {
    const variables={...pin,...changes};for(const k of Object.keys(variables))if(variables[k]===undefined)delete variables[k];
    assert.throws(()=>resolveTestSelection(input(variables),rejectNetwork),/Pinned test repository|full immutable commit/);
  }
});
test('malformed flags do not silently enable or disable tests',()=>{
  for(const flag of ['TRUE','False','1',' true ','yes'])assert.throws(()=>resolveTestSelection(input({WSS_USE_PIN_TESTS:flag}),rejectNetwork),/must be true/);
});
test('failed lookup, mutable SHA response and inaccessible pins fail closed',()=>{
  assert.throws(()=>resolveTestSelection(input(pin),{...g,get:()=>{throw Error('404');}}),/404/);
  assert.throws(()=>resolveTestSelection(input(pin),{...g,get:()=>({sha:sha(5)})}),/requested immutable/);
  assert.throws(()=>resolveTestSelection(input(),{...g,head:()=> 'dev'}),/requested immutable/);
});
test('shared latest source is resolved exactly once across campaign targets',()=>{
  const r=input();r.targets.push({...r.targets[0],id:'python-cli',repository:'cwru-non-academic/CLI_Python_WSS_Application'});
  let calls=0;const s=resolveTestSelection(r,{...g,head:()=>sha(++calls+1)});
  assert.equal(calls,1);assert.equal(s.targets['python-wrapper'].tests.commit,s.targets['python-cli'].tests.commit);
});
test('shared pinned source is verified only once',()=>{
  const r=input(pin);r.targets.push({...r.targets[0],id:'python-cli',repository:'cwru-non-academic/CLI_Python_WSS_Application'});
  let calls=0;resolveTestSelection(r,{...g,get:()=>{calls++;return {sha:sha(4)};}});assert.equal(calls,1);
});
test('resolver preserves the request and selected product/campaign identity',()=>{
  const r=input(pin),before=structuredClone(r),s=resolveTestSelection(r,g);
  assert.deepEqual(r,before);assert.deepEqual(s.campaign,r.campaign);assert.equal(s.targets['python-wrapper'].repository,owner);
  assert.notEqual(s.campaign.commit,s.targets['python-wrapper'].tests.commit);
});
test('unsupported channels, duplicate targets and malformed trusted input fail before network',()=>{
  for(const [mutate,expected] of [
    [r=>r.channel='feature',/CI channel must be exactly/],
    [r=>r.campaign.run_id=42,/Invalid immutable campaign identity/],
    [r=>r.targets.push({...r.targets[0]}),/Invalid or duplicate test target/],
    [r=>r.targets[0].allowed_test_repositories=[],/explicit reviewed repository allowlist/],
    [r=>r.targets[0].variables.UNKNOWN='x',/Invalid repository variable snapshot/],
    [r=>r.targets[0].default_test_repository='foreign/repo',/explicit reviewed repository allowlist/],
    [r=>r.campaign.unexpected=true,/Invalid immutable campaign identity/]
  ]) {
    const r=input(),calls=[];mutate(r);
    const network=Object.fromEntries(['head','get'].map(name=>[name,()=>{calls.push(name);throw Error('Forbidden network access');}]));
    assert.throws(()=>resolveTestSelection(r,network),expected);
    assert.deepEqual(calls,[],'Invalid trusted input must reject before any network call');
  }
});
test('consumers authenticate frozen selection identity, not a self-reported digest',()=>{
  const r=input(),s=resolveTestSelection(r,g);
  assert.equal(verifyTestSelection(s,r,s.selection_id),s);
  assert.throws(()=>verifyTestSelection(s,r,undefined),/frozen campaign/);
  const changed=resign({...s,targets:{...s.targets,'python-wrapper':{...s.targets['python-wrapper'],tests:{repository:fixture,commit:sha(8)}}}});
  assert.throws(()=>verifyTestSelection(changed,r,s.selection_id),/frozen campaign/);
});
test('branch movement and variable edits do not change a frozen campaign',()=>{
  const r=input(),s=resolveTestSelection(r,g),changed=input(pin);
  assert.equal(verifyTestSelection(s,changed,s.selection_id),s);
});
test('different campaign, channel, target set or allowed source cannot reuse a lock',()=>{
  const r=input(),s=resolveTestSelection(r,g);
  for(const mutate of [v=>v.campaign.run_id='43',v=>v.campaign.commit=sha(5),v=>v.channel='main',v=>v.targets[0].id='python-cli',v=>v.targets[0].allowed_test_repositories=[owner]]) {
    const changed=structuredClone(r);mutate(changed);assert.throws(()=>verifyTestSelection(s,changed,s.selection_id));
  }
});
test('malformed selected data is rejected even if an external caller recomputes its digest',()=>{
  const r=input(),s=resolveTestSelection(r,g);
  for(const mutate of [v=>v.targets['python-wrapper'].tests.commit='main',v=>v.targets['python-wrapper'].mode='skip',v=>v.targets['python-wrapper'].requested_ref='refs/heads/main',v=>v.targets['python-wrapper'].tests.repository=owner,v=>v.targets['python-wrapper'].extra=true,v=>v.extra=true]) {
    const changed=structuredClone(s);mutate(changed);const signed=resign(changed);assert.throws(()=>verifyTestSelection(signed,r,signed.selection_id));
  }
});
test('evidence must report exact source, target and selection id',()=>{
  const r=input(),s=resolveTestSelection(r,g),id='python-wrapper',e={selection_id:s.selection_id,target_id:id,repository:owner,test_repository:fixture,test_commit:sha(2)};
  assert.equal(verifySelectedTestEvidence(s,r,s.selection_id,id,e),e);
  for(const change of [{test_commit:sha(3)},{test_repository:owner},{target_id:'python-cli'},{repository:'foreign/repo'},{selection_id:'a'.repeat(64)},{extra:true}])assert.throws(()=>verifySelectedTestEvidence(s,r,s.selection_id,id,{...e,...change}));
});
test('create-only CLI storage reuses existing selection without network or rereading settings',()=>{
  const dir=mkdtempSync(join(tmpdir(),'wss-selection-'));try {
    const path=join(dir,'selection.json'),r=input(),s=resolveOrReuseTestSelection(r,g,path),bytes=readFileSync(path,'utf8');
    assert.deepEqual(resolveOrReuseTestSelection(input(pin),rejectNetwork,path),s);assert.equal(readFileSync(path,'utf8'),bytes);
    assert.throws(()=>resolveOrReuseTestSelection({...r,channel:'main'},rejectNetwork,path),/another campaign/);
    writeFileSync(path,'{}');assert.throws(()=>resolveOrReuseTestSelection(r,g,path),/Unsupported test selection/);
  } finally {rmSync(dir,{recursive:true,force:true});}
});

test('a concurrent first writer wins without changing the frozen source',()=>{
  const dir=mkdtempSync(join(tmpdir(),'wss-selection-race-'));try {
    const path=join(dir,'selection.json'),r=input(),first=resolveTestSelection(r,g);
    const second=resolveOrReuseTestSelection(r,{...g,head:()=>{writeFileSync(path,JSON.stringify(first));return sha(9);}},path);
    assert.deepEqual(second,first);assert.deepEqual(JSON.parse(readFileSync(path,'utf8')),first);
  } finally {rmSync(dir,{recursive:true,force:true});}
});
test('ordinary git refs cannot be substituted for immutable evidence',()=>{
  const r=input(),s=resolveTestSelection(r,g),e={selection_id:s.selection_id,target_id:'python-wrapper',repository:owner,test_repository:fixture,test_commit:'dev'};
  assert.throws(()=>verifySelectedTestEvidence(s,r,s.selection_id,'python-wrapper',e),/frozen test source/);
});

test('overlapping processes atomically publish and reuse one complete selection',{timeout:30000},async()=>{
  const dir=mkdtempSync(join(tmpdir(),'wss-selection-processes-')),children=[];
  try {
    const r=input();
    r.targets=Array.from({length:150},(_,i)=>({...r.targets[0],id:`target-${i}`,repository:`cwru-non-academic/target_${i}`}));
    const requestPath=join(dir,'request.json'),outputPath=join(dir,'selection.json'),barrier=join(dir,'go');
    writeFileSync(requestPath,JSON.stringify(r));
    const code=`
      import {readFileSync,writeFileSync,existsSync} from 'node:fs';
      import {resolveOrReuseTestSelection} from ${JSON.stringify(new URL('./test-selection.mjs',import.meta.url).href)};
      const [request,output,ready,barrier,sha]=process.argv.slice(1);
      const g={head:()=>{
        writeFileSync(ready,'ready');
        const deadline=Date.now()+20000;
        while(!existsSync(barrier)){if(Date.now()>deadline)throw Error('Timed out waiting for overlap barrier');Atomics.wait(new Int32Array(new SharedArrayBuffer(4)),0,0,10);}
        return sha;
      }};
      console.log(resolveOrReuseTestSelection(JSON.parse(readFileSync(request,'utf8')),g,output).selection_id);
    `;
    const readyFiles=[];
    const results=Array.from({length:4},(_,i)=>new Promise((resolve,reject)=>{
      const ready=join(dir,`ready-${i}`);readyFiles.push(ready);
      const child=spawn(process.execPath,['--input-type=module','-e',code,requestPath,outputPath,ready,barrier,sha(i+4)],{stdio:['ignore','pipe','pipe']});
      children.push(child);let stdout='',stderr='';
      child.stdout.on('data',x=>stdout+=x);child.stderr.on('data',x=>stderr+=x);
      child.on('error',reject);child.on('close',status=>status===0?resolve(stdout.trim()):reject(Error(`Concurrent selector failed (${status}): ${stderr}`)));
    }));
    // Attach rejection handlers before awaiting the readiness barrier.
    const completed=Promise.all(results);completed.catch(()=>{});
    const deadline=Date.now()+15000;
    while(!readyFiles.every(path=>{try{return readFileSync(path,'utf8')==='ready';}catch{return false;}})) {
      assert.ok(Date.now()<deadline,'All creators must reach source resolution before publication');
      await new Promise(resolve=>setTimeout(resolve,10));
    }
    writeFileSync(barrier,'go');
    const ids=await completed,selected=JSON.parse(readFileSync(outputPath,'utf8'));
    assert.equal(new Set(ids).size,1);assert.equal(ids[0],selected.selection_id);
    verifyTestSelection(selected,r,ids[0]);
  } finally {
    for(const child of children)if(child.exitCode===null)child.kill();
    rmSync(dir,{recursive:true,force:true});
  }
});
