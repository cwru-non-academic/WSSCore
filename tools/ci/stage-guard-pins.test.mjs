import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync,mkdtempSync,mkdirSync,writeFileSync,rmSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {tmpdir} from 'node:os';
import {join,dirname,basename,resolve} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {inspectStageGuardPins,assertStageGuardPins} from './adapter-pins.mjs';
import {freezeStage} from './dev-propagation.mjs';
import {channels,source,pins,sha,candidate,readPolicies,readRoles,reviewedPolicies,expectedProjection,guardFixture} from '../release/test-fixtures/matlab-stage-guards.mjs';

const receiverHeads={dev:sha(101),main:sha(102)};
function fixture(options={}) {
  const f=guardFixture({heads:receiverHeads,...options});
  return {...f,args:{policies:reviewedPolicies(),roles:readRoles()}};
}
function rejected(f,pattern,{sourceInvalid=true}={}) {
  const report=inspectStageGuardPins(f.args,f.g);
  assert.ok(report.issues.length,JSON.stringify(report));
  if(pattern)assert.match(report.issues.join('\n'),pattern);
  if(sourceInvalid)assert.equal(report.projection,undefined,'Invalid source must not produce a usable projection');
  assert.deepEqual(f.calls.writes,[]);
  return report;
}
function patchObject(f,ref,path,patch) {
  const key=f.objectKey(source.repository,ref,path);
  f.objects.set(key,patch===null?null:{...f.objects.get(key),...patch});
}
test('closed stage registry records both actual immutable V8 owners and all three roles',()=>{
  const roles=readRoles();
  assert.equal(roles.stage_guards.length,3);
  for(const [role,pin] of Object.entries(pins))assert.deepEqual(roles.stage_guards.find(r=>r.role===role),{
    role,repository:source.repository,path:pin.path,policy_slot:role,channels:['dev','main'],reviewed_sources:source.reviewed_sources
  });
});
test('both policies accept the independently recorded V8 source and derive the same read-only projection',()=>{
  const f=fixture(),before=structuredClone(f.args);
  f.args.policies=readPolicies();
  const policyFiles=channels.map(c=>readFileSync(new URL('./'+c+'-propagation.json',import.meta.url),'utf8'));
  const report=assertStageGuardPins(f.args,f.g);
  assert.deepEqual(report.issues,[]);assert.deepEqual(report.projection,expectedProjection());
  assert.deepEqual(channels.map(c=>readFileSync(new URL('./'+c+'-propagation.json',import.meta.url),'utf8')),policyFiles);
  assert.deepEqual(f.args.roles,before.roles);assert.deepEqual(f.calls.writes,[]);
  for(const ref of [...Object.values(source.reviewed_sources),...Object.values(receiverHeads)]) {
    assert.equal(f.calls.get.filter(path=>path.endsWith('/git/commits/'+ref)).length,2,'Commit identity must be captured and reread');
    for(const {path} of Object.values(pins))assert.equal(f.calls.object.filter(args=>args[1]===ref&&args[2]===path).length,2,'Each object must be captured and reread');
  }
  report.projection.dev.promotion_contract_test.sha=sha(999);
  assert.deepEqual(f.args.policies.dev.libraries.find(t=>t.id==='matlab').promotion_contract_test,pins.promotion_contract_test);
});
test('all six independent policy mismatches are reported in one read-only pass',()=>{
  const f=fixture();
  for(const [channel,p] of Object.entries(f.args.policies))for(const role of Object.keys(pins))p.libraries.find(t=>t.id==='matlab')[role].sha=sha(channel==='dev'?900:901);
  const before=structuredClone(f.args),report=rejected(f,/reviewed/,{sourceInvalid:false});
  assert.deepEqual(report.projection,expectedProjection());
  for(const channel of channels)for(const role of Object.keys(pins))assert.ok(report.issues.some(i=>i.includes(channel)&&i.includes(role)));
  assert.equal(report.issues.length,6);assert.deepEqual(f.args,before);
  assert.throws(()=>assertStageGuardPins(f.args,f.g),error=>report.issues.every(issue=>error.message.includes(issue)));
});
for(const fixed of channels)test('updating only '+fixed+' cannot pass paired consistency',()=>{
  const f=fixture(),other=fixed==='dev'?'main':'dev';
  Object.assign(f.args.policies[other].libraries.find(t=>t.id==='matlab'),structuredClone(source.historical.pins));
  const report=rejected(f,new RegExp(other+'.*promotion_contract_test'),{sourceInvalid:false});
  assert.ok(report.issues.some(i=>i.includes(other)&&i.includes('merge_evidence_guard')));
  assert.deepEqual(report.projection,expectedProjection());
});
for(const kind of ['dev missing','main missing','extra policy','wrong channel','missing MATLAB','duplicate MATLAB','wrong repository'])test('paired policies reject '+kind,()=>{
  const f=fixture();
  if(kind.endsWith(' missing'))delete f.args.policies[kind.split(' ')[0]];
  else if(kind==='extra policy')f.args.policies.preview=f.args.policies.dev;
  else if(kind==='wrong channel')f.args.policies.dev.channel='main';
  else if(kind==='missing MATLAB')f.args.policies.dev.libraries=f.args.policies.dev.libraries.filter(t=>t.id!=='matlab');
  else if(kind==='duplicate MATLAB')f.args.policies.dev.libraries.push(structuredClone(f.args.policies.dev.libraries.find(t=>t.id==='matlab')));
  else f.args.policies.dev.libraries.find(t=>t.id==='matlab').repository='attacker/Matlab_WSS_Integration_Lib';
  rejected(f,/polic|ownership/i,{sourceInvalid:false});
});
for(const role of Object.keys(pins))for(const kind of ['missing','duplicate','repository','path','slot','channel','mutable owner','short owner','missing owner','extra field'])test(role+' registry rejects '+kind,()=>{
  const f=fixture(),row=f.args.roles.stage_guards.find(r=>r.role===role);
  if(kind==='missing')f.args.roles.stage_guards=f.args.roles.stage_guards.filter(r=>r.role!==role);
  else if(kind==='duplicate')f.args.roles.stage_guards.push(structuredClone(row));
  else if(kind==='repository')row.repository='attacker/Matlab_WSS_Integration_Lib';
  else if(kind==='path')row.path='.github/scripts/Unreviewed.ps1';
  else if(kind==='slot')row.policy_slot='promotion_contract_test';
  else if(kind==='channel')row.channels=['dev'];
  else if(kind==='mutable owner')row.reviewed_sources.dev='dev';
  else if(kind==='short owner')row.reviewed_sources.main=source.reviewed_sources.main.slice(0,7);
  else if(kind==='missing owner')delete row.reviewed_sources.main;
  else row.approved=true;
  // A slot corruption must actually differ even for the contract-test role.
  if(kind==='slot')row.policy_slot=role==='promotion_contract_test'?'merge_evidence_guard':'promotion_contract_test';
  rejected(f,/stage guard|registered/i);
});
for(const kind of ['absent','unknown role','extra duplicate unknown'])test('closed registry rejects '+kind,()=>{
  const f=fixture();
  if(kind==='absent')delete f.args.roles.stage_guards;
  else f.args.roles.stage_guards.push({...structuredClone(f.args.roles.stage_guards[0]),role:'arbitrary_guard'});
  if(kind==='extra duplicate unknown')f.args.roles.stage_guards.push({...structuredClone(f.args.roles.stage_guards[0]),role:'arbitrary_guard'});
  rejected(f,/guard role/i);
});
for(const channel of channels)for(const kind of ['null','tag','wrong identity','bad tree','exception'])test(channel+' owner rejects '+kind+' commit response',()=>{
  const f=fixture(),ref=source.reviewed_sources[channel];
  if(kind==='null')f.commits.set(ref,null);
  else if(kind==='tag')f.commits.set(ref,{sha:ref,type:'tag',object:{type:'commit',sha:ref}});
  else if(kind==='wrong identity')f.commits.set(ref,{sha:sha(999),tree:{sha:sha(998)}});
  else if(kind==='bad tree')f.commits.set(ref,{sha:ref,tree:{sha:'bad'}});
  else {const get=f.g.get;f.g.get=path=>{if(path.endsWith('/'+ref))throw Error('404');return get(path);};}
  rejected(f,/source|commit/i);
});
for(const channel of channels)for(const [role,pin] of Object.entries(pins))for(const kind of ['null','tree','gitlink','symlink','missing mode','short sha','wrong path'])test(channel+' '+role+' owner rejects '+kind+' object',()=>{
  const f=fixture(),patches={null:null,tree:{type:'tree'},gitlink:{type:'commit',mode:'160000'},symlink:{mode:'120000'},'missing mode':{mode:undefined},'short sha':{sha:'1234567'},'wrong path':{path:'.github/scripts/Other.ps1'}};
  patchObject(f,source.reviewed_sources[channel],pin.path,patches[kind]);rejected(f,/object|blob/i);
});
for(const mode of ['100644','100755'])test('regular executable owner/receiver blob mode '+mode+' is accepted',()=>{
  const f=fixture();
  for(const value of f.objects.values())value.mode=mode;
  assert.deepEqual(assertStageGuardPins(f.args,f.g).projection,expectedProjection());
});
for(const [role,pin] of Object.entries(pins))test(role+' owner copies must agree independently of both policies',()=>{
  const f=fixture();patchObject(f,source.reviewed_sources.main,pin.path,{sha:sha(800)});
  for(const p of Object.values(f.args.policies))p.libraries.find(t=>t.id==='matlab')[role].sha=sha(800);
  rejected(f,/owner guard bytes differ/i);
});
for(const branch of channels)for(const [role,pin] of Object.entries(pins))for(const badSha of [...new Set([source.historical.pins[role].sha,sha(999)])].filter(s=>s!==pin.sha))test(branch+' receiver rejects historical/arbitrary '+role+' '+badSha,()=>{
  const f=fixture();patchObject(f,receiverHeads[branch],pin.path,{sha:badSha});rejected(f,new RegExp(role+'.*'+branch));
});
for(const branch of channels)for(const kind of ['noncommit','null blob','wrong mode','wrong type'])test(branch+' receiver rejects '+kind,()=>{
  const f=fixture();
  if(kind==='noncommit')f.commits.set(receiverHeads[branch],{sha:receiverHeads[branch]});
  else patchObject(f,receiverHeads[branch],pins.native_evidence_guard.path,kind==='null blob'?null:kind==='wrong mode'?{mode:'120000'}:{type:'tree'});
  rejected(f,/source|object|blob/i);
});
test('every owner and receiver mismatch is retained rather than short-circuiting at the first',()=>{
  const f=fixture();
  for(const branch of channels)for(const [role,pin] of Object.entries(pins)){
    patchObject(f,receiverHeads[branch],pin.path,{sha:sha(700)});
    f.args.policies[branch].libraries.find(t=>t.id==='matlab')[role].sha=sha(701);
  }
  const report=rejected(f);
  for(const branch of channels)for(const role of Object.keys(pins)){
    assert.ok(report.issues.some(i=>i.includes(role)&&i.includes(branch)&&i.includes('policy')));
    assert.ok(report.issues.some(i=>i.includes(role)&&i.includes(branch)&&!i.includes('policy')));
  }
  assert.equal(report.issues.length,12);
});
for(const provided of [false,true])for(const branch of channels)test((provided?'captured':'live')+' '+branch+' movement rejects instead of refreshing',()=>{
  const f=fixture(),object=f.g.object;
  if(provided)f.args.heads=structuredClone(receiverHeads);
  f.g.object=(...args)=>{const value=object(...args);f.liveHeads[branch]=sha(999);return value;};
  rejected(f,new RegExp('moved on '+branch));
});
for(const target of ['owner','receiver'])for(const kind of ['commit identity','commit tree','commit null','blob sha','blob mode','blob null'])test(target+' post-capture '+kind+' mutation rejects',()=>{
  const f=fixture(),ref=target==='owner'?source.reviewed_sources.dev:receiverHeads.dev;
  let reads=0;
  if(kind.startsWith('commit')){
    const get=f.g.get;
    f.g.get=path=>{
      const value=get(path);
      if(path.endsWith('/git/commits/'+ref)&&++reads===2){
        if(kind==='commit null')return null;
        if(kind==='commit identity')value.sha=sha(999);else value.tree.sha=sha(999);
      }
      return value;
    };
  } else {
    const object=f.g.object;
    f.g.object=(repo,sourceRef,path)=>{
      const value=object(repo,sourceRef,path);
      if(sourceRef===ref&&path===pins.native_evidence_guard.path&&++reads===2){
        if(kind==='blob null')return null;
        if(kind==='blob sha')value.sha=sha(999);else value.mode='120000';
      }
      return value;
    };
  }
  rejected(f,/capture|recheck|source|object/i);
});
test('explicit candidate mode validates selected commits without reading or adopting branch tips',()=>{
  const f=fixture();f.args.heads=structuredClone(receiverHeads);f.args.deployed=false;
  f.g.head=()=>assert.fail('Candidate mode must not use branch tips');
  assert.deepEqual(assertStageGuardPins(f.args,f.g).projection,expectedProjection());
});
for(const heads of [undefined,{dev:receiverHeads.dev},{...receiverHeads,preview:sha(103)},{dev:'dev',main:receiverHeads.main}])test('candidate mode rejects absent, incomplete, extra or mutable sources '+JSON.stringify(heads),()=>{
  const f=fixture();f.args.heads=heads;f.args.deployed=false;
  f.g.head=()=>assert.fail('Invalid candidate mode must not fall back to live heads');
  rejected(f,/source|commits/i);
});
test('candidate heads never become reviewed authority even when both policies copy their arbitrary bytes',()=>{
  const f=fixture();f.args.heads=structuredClone(receiverHeads);f.args.deployed=false;
  for(const branch of channels){
    patchObject(f,receiverHeads[branch],pins.merge_evidence_guard.path,{sha:sha(999)});
    f.args.policies[branch].libraries.find(t=>t.id==='matlab').merge_evidence_guard.sha=sha(999);
  }
  rejected(f,/merge_evidence_guard/);
});
for(const channel of channels)test(channel+' freeze produces no plan or write when any guard fails after receiver collection',()=>{
  const f=guardFixture({channel,heads:receiverHeads}),object=f.g.object;
  f.g.object=(repo,ref,path)=>repo===source.repository&&ref===receiverHeads.main&&path===pins.merge_evidence_guard.path?{type:'blob',mode:'100644',sha:sha(999)}:object(repo,ref,path);
  let plan;
  assert.throws(()=>{plan=freezeStage(f.g,f.policy,candidate);},/merge_evidence_guard.*main/i);
  assert.equal(plan,undefined);assert.deepEqual(f.calls.writes,[]);
  for(const target of f.policy.libraries)assert.ok(f.calls.object.some(([repo])=>repo===target.repository),'All receiver collections completed before paired check');
});

for(const changed of ['policy','roles','heads'])test('post-collection caller '+changed+' mutation fails without silently adopting it',()=>{
  const f=fixture(),object=f.g.object;
  f.args.heads=structuredClone(receiverHeads);
  let changedOnce=false;
  f.g.object=(...args)=>{
    const value=object(...args);
    if(!changedOnce) {
      changedOnce=true;
      if(changed==='policy')f.args.policies.main.libraries.find(t=>t.id==='matlab').merge_evidence_guard.sha=sha(999);
      else if(changed==='roles')f.args.roles.stage_guards[0].reviewed_sources.main=sha(999);
      else f.args.heads.main=sha(999);
    }
    return value;
  };
  rejected(f,/changed|input/i);
});
for(const deployed of [null,'false',0,{}])test('non-boolean deployment mode rejects '+JSON.stringify(deployed),()=>{
  const f=fixture();f.args.deployed=deployed;rejected(f,/mode|boolean|deployed/i);
});
test('Core own shared CI runs live guard readiness and the complete adversarial suite',()=>{
  const workflow=readFileSync(new URL('../../.github/workflows/shared-ci.yml',import.meta.url),'utf8');
  assert.match(workflow,/run: node tools\/ci\/adapter-pins\.mjs check-guards/);
  assert.match(workflow,/node --test[^\r\n]*tools\/ci\/stage-guard-pins\.test\.mjs/);
});
test('actual CLI live/candidate checks are offline read-only and preparation rejects before writes',()=>{
  const root=mkdtempSync(join(tmpdir(),'wss-stage-guard-cli-'));
  const entry=fileURLToPath(new URL('./adapter-pins.mjs',import.meta.url));
  const preload=join(root,'mock-read-only-github.mjs'),headsFile=join(root,'selected-receivers.json');
  const target=join(root,'target'),targetFile=join(target,'.github/workflows/sentinel.yml');
  const callsFile=join(root,'observed-calls.json');
  try {
    mkdirSync(dirname(targetFile),{recursive:true});
    writeFileSync(targetFile,'name: Preserve this workflow\n');
    writeFileSync(headsFile,JSON.stringify(receiverHeads));
    writeFileSync(preload,`import assert from 'node:assert/strict';
import {writeFileSync} from 'node:fs';
import {GitHub} from ${JSON.stringify(new URL('../release/github.mjs',import.meta.url).href)};
import {guardFixture,source,pins,sha} from ${JSON.stringify(new URL('../release/test-fixtures/matlab-stage-guards.mjs',import.meta.url).href)};
const f=guardFixture({heads:${JSON.stringify(receiverHeads)}});
if(process.env.WSS_GUARD_TEST_FAILURE==='true')for(const branch of ['dev','main'])for(const pin of Object.values(pins))f.objects.get(f.objectKey(source.repository,${JSON.stringify(receiverHeads)}[branch],pin.path)).sha=sha(999);
if(process.env.WSS_GUARD_TEST_CANDIDATE==='true')f.g.head=()=>assert.fail('Candidate command read a live branch tip');
for(const method of ['get','object','head','command'])GitHub.prototype[method]=f.g[method];
process.on('exit',()=>writeFileSync(process.env.WSS_GUARD_TEST_CALLS,JSON.stringify(f.calls)));
`);
    const run=(args,{bad=false,candidateMode=false}={})=>{
      const result=spawnSync(process.execPath,['--import',pathToFileURL(preload).href,entry,...args],{
        encoding:'utf8',env:{...process.env,NODE_OPTIONS:'',WSS_GUARD_TEST_FAILURE:String(bad),WSS_GUARD_TEST_CANDIDATE:String(candidateMode),WSS_GUARD_TEST_CALLS:callsFile}
      });
      assert.ifError(result.error);
      const calls=JSON.parse(readFileSync(callsFile,'utf8'));
      assert.deepEqual(calls.writes,[]);
      assert.equal(readFileSync(targetFile,'utf8'),'name: Preserve this workflow\n');
      assert.deepEqual(readdirSync(target),['.github']);
      assert.deepEqual(readdirSync(join(target,'.github')),['workflows']);
      assert.deepEqual(readdirSync(dirname(targetFile)),['sentinel.yml']);
      return {...result,calls};
    };
    for(const [args,candidateMode] of [[['check-guards'],false],[['check-guard-candidates',headsFile],true]]){
      const success=run(args,{candidateMode});assert.equal(success.status,0,success.stderr);
      assert.deepEqual(JSON.parse(success.stdout).projection,expectedProjection());
      assert.equal(JSON.parse(success.stdout).deployed,!candidateMode);
      const failure=run(args,{bad:true,candidateMode});assert.equal(failure.status,1,failure.stderr);
      const report=JSON.parse(failure.stdout);assert.equal(report.projection,undefined);
      for(const branch of channels)for(const role of Object.keys(pins))assert.ok(report.issues.some(i=>i.includes(role)&&i.includes(branch)));
      if(candidateMode)assert.deepEqual(failure.calls.head,[]);
    }
    for(const operation of ['check','refresh']){
      const failure=run([operation,'Matlab_WSS_Integration_Lib',target],{bad:true});
      assert.equal(failure.status,1);assert.match(failure.stderr,/Stage guard consistency failed/);
      assert.doesNotMatch(failure.stderr,/ENOENT|source origin|Unexpected/);
      assert.ok(failure.calls.object.length>0);
    }
  } finally {
    // Remove only the direct temporary child created by this test.
    assert.equal(dirname(resolve(root)),resolve(tmpdir()));
    assert.ok(basename(root).startsWith('wss-stage-guard-cli-'));
    rmSync(root,{recursive:true,force:true});
  }
});