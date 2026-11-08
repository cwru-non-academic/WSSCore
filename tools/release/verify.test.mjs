import {execFileSync} from 'node:child_process';
import {resolveTestSelection} from '../ci/test-selection.mjs';
import {CAMPAIGN_TEST_POLICY,selectionRequest,selectionFields,targetTestEvidence,selectedTestArtifactName} from './campaign-test-selection.mjs';
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync,mkdtempSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {allClear,bytesDigest,canonical,CORE,CORE_JOBS,digest,plan,TARGETS} from './verify.mjs';
import {collect,freeze,GitHub,publish} from './github.mjs';
import {tagPolicy} from './policy.mjs';
import {channelContext,releaseFingerprint} from './channels.mjs';

const zipCache=new Map();
function candidateZip(candidate) {
 const text=JSON.stringify(candidate);if(zipCache.has(text))return zipCache.get(text);
 const dir=mkdtempSync(join(tmpdir(),'receipt-candidate-zip-'));
 try {writeFileSync(join(dir,'candidate.json'),text);writeFileSync(join(dir,'zip.ps1'),'param([string]$Source,[string]$Destination)\n$ErrorActionPreference="Stop"; Compress-Archive -LiteralPath $Source -DestinationPath $Destination');execFileSync('pwsh',['-NoProfile','-File',join(dir,'zip.ps1'),join(dir,'candidate.json'),join(dir,'proof.zip')]);const bytes=readFileSync(join(dir,'proof.zip'));zipCache.set(text,bytes);return bytes;}finally{rmSync(dir,{recursive:true,force:true});}
}
const sha=n=>n.toString(16).padStart(40,'0');
const successful=names=>names.map(name=>({name,status:'completed',conclusion:'success'}));
function tagRule() {return{id:123,target:'tag',enforcement:'active',source_type:'Repository',source:CORE,conditions:{ref_name:{include:['refs/tags/v*'],exclude:[]}},bypass_actors:[],rules:[{type:'creation'},{type:'update'},{type:'deletion'}]};}
function run(repo,workflow,n,head,event='workflow_dispatch') {
  return {id:n,repository:{full_name:repo},head_repository:{full_name:repo},path:workflow,head_sha:head,head_branch:'dev',event,status:'completed',conclusion:'success',run_attempt:1};
}
function fixture(channel='dev') {
  const context=channelContext(channel);
  const manifest=JSON.parse(readFileSync(new URL(`./targets.${channel}.json`,import.meta.url)));
  manifest.legacy_tag_guard={ruleset_id:'123',policy_sha256:digest(tagPolicy(tagRule()))};
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(Buffer.from('zip'))};
  const body={schema:context.planSchema,channel,candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:sha(2),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(3)]))}]))};
  const p={...body,plan_id:digest(body)};
  const e={producer:{run:run(CORE,'.github/workflows/artifacts.yml',1,sha(1),'push'),current_head:sha(1),jobs:successful(CORE_JOBS),artifact:{id:2,name:'universal-release-assets',expired:false,workflow_run:{id:1,head_sha:sha(1)}},archive_sha256:candidate.sha256},targets:{}};
  manifest.targets.forEach((t,i)=>{
    const merge=sha(10+i),head=sha(30+i),normal=100+i,emitter=200+i;
    const parents=Object.fromEntries(Object.keys(t.parents).map(id=>[id,sha(10+TARGETS.indexOf(id))]));
    const r={schema:context.receiptSchema,plan_id:p.plan_id,channel,target_id:t.id,candidate,mode:'merged-pr',
      merge:{pr_number:String(i+1),sha:merge,head_sha:head},normal_ci:{run_id:String(normal),attempt:1},
      preflights:[{repository:t.repository,run_id:String(300+i),source_sha:sha(2),workflow:'.github/workflows/wss-preflight.yml',attempt:1,jobs:[{id:'900',name:'Native required proof',status:'completed',conclusion:'success'}],artifacts:[{id:'901',name:'rich-proof',digest:'sha256:'+bytesDigest(Buffer.from('rich proof ZIP'))}],logs:[]}],
      parents,emitter:{run_id:String(emitter),attempt:1,source_sha:merge,workflow:'.github/workflows/release-receipt.yml'},coverage:t.coverage};
    e.targets[t.id]={receipt:r,receipt_bytes:Buffer.from(JSON.stringify(r)),pr:{number:i+1,merged:true,merged_at:'2026-10-29T00:00:00Z',user:{type:'Bot'},merged_by:{type:'User'},state:'closed',merge_commit_sha:merge,head:{sha:head,ref:'automation/candidate',repo:{full_name:t.repository}},base:{ref:'dev',repo:{full_name:t.repository}}},
      current_head:merge,merge_tree:sha(4),tested_tree:sha(4),trusted:p.target_snapshots[t.id].trusted,parent_gitlinks:parents,
      normal_run:{...run(t.repository,t.workflow,normal,merge),created_at:'2026-10-29T00:01:00Z',pull_requests:[]},normal_latest_run_id:String(normal),normal_jobs:successful(t.jobs),
      emitter_run:run(t.repository,r.emitter.workflow,emitter,merge),artifact:{id:500+i,name:`wss-acceptance-${t.id}-${p.plan_id}-a1`,expired:false,workflow_run:{id:emitter,head_sha:merge}},
      preflights:[{run:run(t.repository,r.preflights[0].workflow,300+i,sha(2)),candidate,jobs:r.preflights[0].jobs,artifacts:r.preflights[0].artifacts,logs:[]}]};
    e.targets[t.id].receipt_sha256=bytesDigest(e.targets[t.id].receipt_bytes);
  });
  e.producer.run.head_branch=channel;
  for(const target of Object.values(e.targets)){
    target.pr.base.ref=channel;target.normal_run.head_branch=channel;target.emitter_run.head_branch=channel;
    for(const preflight of target.preflights)preflight.run.head_branch=channel;
  }
  return {p,e};
}
function sync(e) {for(const t of Object.values(e.targets)){t.receipt_bytes=Buffer.from(JSON.stringify(t.receipt));t.receipt_sha256=bytesDigest(t.receipt_bytes);}}
test('all nine exact merged consumers generate an explicit beta acceptance record',()=>{
  const {p,e}=fixture(),r=allClear(p,e);
  assert.equal(r.kind,'beta');assert.equal(Object.keys(r.targets).length,9);assert.deepEqual(r.excluded,[]);
  assert.ok(r.targets['unity-gui'].coverage.includes('dotnet-harness-not-unity-editor'));
  assert.ok(r.targets['matlab-wrapper'].coverage.includes('packaged-macos-ble-load'));
  assert.equal(r.targets['matlab-cli'].parents['matlab-wrapper'],r.targets['matlab-wrapper'].merge.sha);
});
test('main requires all nine actual main merges and produces stable acceptance, not dev evidence',()=>{
  const f=fixture('main'),accepted=allClear(f.p,f.e);
  assert.equal(accepted.kind,'stable');assert.equal(accepted.channel,'main');assert.equal(accepted.schema,'wss-release-acceptance/v2');assert.equal(Object.keys(accepted.targets).length,9);
  for(const mutate of [g=>g.e.producer.run.head_branch='dev',g=>g.e.targets['python-cli'].pr.base.ref='dev',g=>g.e.targets['csharp-cli'].normal_run.head_branch='dev',g=>g.e.targets['unity-gui'].emitter_run.head_branch='dev',g=>g.e.targets['matlab-wrapper'].preflights[0].run.head_branch='dev',g=>delete g.e.targets['python-ros']]){const g=fixture('main');mutate(g);assert.throws(()=>allClear(g.p,g.e));}
});
test('successful synthetic PR run cannot substitute for actual postmerge normal CI',()=>{
  const {p,e}=fixture();const n=e.targets['csharp-cli'];n.normal_run.event='pull_request';n.normal_run.head_sha=n.receipt.merge.head_sha;
  assert.throws(()=>allClear(p,e),/post-merge/);
});
const cases={
  'missing consumer':({e})=>delete e.targets['python-ros'],
  'missing MATLAB wrapper':({e})=>delete e.targets['matlab-wrapper'],
  'missing MATLAB CLI':({e})=>delete e.targets['matlab-cli'],
  'MATLAB forged receipt':({e})=>e.targets['matlab-cli']=e.targets['csharp-cli'],
  'skipped MATLAB macOS':({e})=>e.targets['matlab-wrapper'].normal_jobs.find(j=>j.name==='Integration compatibility (macOS)').conclusion='skipped',
  'changed policy digest':({p})=>p.manifest.targets.pop(),
  'forged plan id':({p})=>p.plan_id='0'.repeat(64),
  'main channel':({p})=>p.channel='main',
  'failed Core producer':({e})=>e.producer.run.conclusion='failure',
  'Core manual producer':({e})=>e.producer.run.event='workflow_dispatch',
  'Core branch moved':({e})=>e.producer.current_head=sha(99),
  'Core skipped macOS':({e})=>e.producer.jobs.find(j=>j.name==='BLE consumer (macOS arm64)').conclusion='skipped',
  'Core skipped logical Mac check':({e})=>e.producer.jobs.find(j=>j.name==='BLE Consumer for Mac').conclusion='skipped',
  'Core missing logical Mac check':({e})=>e.producer.jobs=e.producer.jobs.filter(j=>j.name!=='BLE Consumer for Mac'),
  'Core duplicate logical Mac check':({e})=>e.producer.jobs.push(e.producer.jobs.find(j=>j.name==='BLE Consumer for Mac')),
  'Core duplicate job':({e})=>e.producer.jobs.push(e.producer.jobs[0]),
  'expired ZIP':({e})=>e.producer.artifact.expired=true,
  'different ZIP bytes':({e})=>e.producer.archive_sha256='b'.repeat(64),
  'wrong artifact owner run':({e})=>e.producer.artifact.workflow_run.id=999,
  'unmerged PR':({e})=>e.targets['csharp-wrapper'].pr.merged=false,
  'automatically merged dependency':({e})=>e.targets['csharp-wrapper'].pr.merged_by.type='Bot',
  'human migration masquerading as dependency promotion':({e})=>e.targets['unity-wrapper'].pr.user.type='User',
  'wrong actual merge':({e})=>e.targets['csharp-wrapper'].pr.merge_commit_sha=sha(99),
  'wrong destination':({e})=>e.targets['csharp-cli'].pr.base.ref='main',
  'fork PR':({e})=>e.targets['csharp-cli'].pr.head.repo.full_name='fork/test',
  'new target dev commit':({e})=>e.targets['csharp-wrapper'].current_head=sha(99),
  'different merged tree':({e})=>e.targets['csharp-wrapper'].merge_tree=sha(99),
  'new validator code':({e})=>e.targets['csharp-wrapper'].trusted={x:sha(99)},
  'old normal CI attempt':({e})=>e.targets['csharp-cli'].normal_run.run_attempt=2,
  'newer normal CI exists':({e})=>e.targets['csharp-cli'].normal_latest_run_id='999',
  'fork normal CI':({e})=>e.targets['csharp-cli'].normal_run.head_repository.full_name='fork/test',
  'missing required normal job':({e})=>e.targets['python-ros'].normal_jobs.pop(),
  'skipped native Humble':({e})=>e.targets['python-ros'].normal_jobs.find(j=>j.name==='ROS-specific compatibility (Humble)').conclusion='skipped',
  'normal CI tests PR head instead of merge':({e})=>e.targets['csharp-cli'].normal_run.head_sha=e.targets['csharp-cli'].receipt.merge.head_sha,
  'foreign parent pin':({e})=>e.targets['csharp-cli'].parent_gitlinks={'csharp-wrapper':sha(99)},
  'parent not accepted exact merge':({e})=>{e.targets['csharp-cli'].parent_gitlinks={'csharp-wrapper':sha(99)};e.targets['csharp-cli'].receipt.parents={'csharp-wrapper':sha(99)};},
  'wrong candidate in receipt':({e})=>e.targets['python-cli'].receipt.candidate={...e.producer,sha256:'b'.repeat(64)},
  'no-op cannot claim reviewed merge':({e})=>e.targets['unity-wrapper'].receipt.mode='no-op',
  'Unity claims untested Editor':({e})=>e.targets['unity-gui'].receipt.coverage=['UnityEditor'],
  'missing preflight':({e})=>e.targets['csharp-wrapper'].receipt.preflights=[],
  'unrelated preflight candidate':({e})=>e.targets['csharp-wrapper'].preflights[0].candidate={...e.targets['csharp-wrapper'].receipt.candidate,commit:sha(99)},
  'green preflight for a different frozen source':({e})=>{e.targets['csharp-wrapper'].receipt.preflights[0].source_sha=sha(99);e.targets['csharp-wrapper'].preflights[0].run.head_sha=sha(99);},
  'emitter PR-head manual dispatch':({e})=>e.targets['csharp-wrapper'].emitter_run.head_sha=sha(30),
  'failed receipt emitter':({e})=>e.targets['csharp-wrapper'].emitter_run.conclusion='failure',
  'untrusted workflow_run emitter':({e})=>e.targets['csharp-wrapper'].emitter_run.event='workflow_run',
  'receipt artifact wrong run':({e})=>e.targets['csharp-wrapper'].artifact.workflow_run.id=999,
  'receipt artifact expired':({e})=>e.targets['csharp-wrapper'].artifact.expired=true,
  'receipt bytes changed':({e})=>e.targets['csharp-wrapper'].receipt_sha256='b'.repeat(64)
};
for(const [name,mutate] of Object.entries(cases)) test(`reject ${name}`,()=>{
  const f=fixture();mutate(f);if(name!=='receipt bytes changed')sync(f.e);assert.throws(()=>allClear(f.p,f.e));
});
test('freeze refuses unregistered receiver rather than dropping target',()=>{
  const {p}=fixture();let seen=0;
  const g={head:()=>sha(1),object:()=>({sha:sha(2)}),get:path=>{if(path.includes('/rulesets/'))return tagRule();seen++;return{path:'.github/workflows/release-receipt.yml',state:'disabled_manually'};}};
  assert.throws(()=>freeze(g,p.manifest,p.candidate),/unavailable/);assert.equal(seen,1);
});
test('pagination never silently truncates a target proof list',()=>{
  const g=new GitHub();g.get=()=>({total_count:2,workflow_runs:[{id:1}]});assert.throws(()=>g.pages('fake','workflow_runs'),/Incomplete/);
});
test('publication never overwrites a tag without a matching complete release',()=>{
  const {p,e}=fixture();let mutations=0;
  const g={get:()=>tagRule(),pages:path=>path.includes('matching-refs')?[{ref:`refs/tags/dev-beta-1-${p.plan_id.slice(0,12)}`,object:{type:'commit',sha:p.candidate.commit}}]:[],command:()=>mutations++};
  assert.throws(()=>publish(g,p,{record:allClear(p,e),files:{}},'.'),/incomplete/);assert.equal(mutations,0);
});

function collectorFixture(f) {
  const {p,e}=f,entries=()=>Object.values(e.targets),target=repo=>entries().find(t=>t.normal_run.repository.full_name===repo);
  const richBytes=Buffer.from('rich proof ZIP'),zip=candidateZip(p.candidate);
  const artifacts=t=>[{id:901,name:'rich-proof',digest:'sha256:'+bytesDigest(richBytes),expired:false,workflow_run:{id:t.preflights[0].run.id,head_sha:t.preflights[0].run.head_sha}},{id:902,name:'wss-candidate-provenance',digest:'sha256:'+bytesDigest(zip),expired:false,workflow_run:{id:t.preflights[0].run.id,head_sha:t.preflights[0].run.head_sha}}];
  return {
    artifactBytes:(repo,id)=>id==='901'?richBytes:zip,
    head:(repo,branch='dev')=>{assert.equal(branch,p.channel,'Collector must read the selected branch explicitly');return repo===CORE?e.producer.current_head:target(repo).current_head;},
    run:(repo,id)=>repo===CORE?e.producer.run:[...entries().flatMap(t=>[t.normal_run,t.emitter_run,...t.preflights.map(x=>x.run)])].find(r=>r.repository.full_name===repo&&String(r.id)===id),
    jobs:(repo,r)=>repo===CORE?e.producer.jobs:r.path==='.github/workflows/wss-preflight.yml'?target(repo).preflights[0].jobs:target(repo).normal_jobs,
    artifact:(repo,id)=>repo===CORE?e.producer.artifact:target(repo).artifact,
    download:(repo,id,name)=>{
      if(repo===CORE)return{[p.candidate.archive]:Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${p.candidate.sha256}  ${p.candidate.archive}\n`)};
      if(name==='wss-candidate-provenance')return{'candidate.json':Buffer.from(JSON.stringify(p.candidate))};
      return{'receipt.json':target(repo).receipt_bytes};
    },
    pages:path=>{
      if(path.includes('/git/matching-refs/')||path.endsWith('/releases'))return [];
      const repo=path.split('/').slice(1,3).join('/'),t=target(repo);
      if(path.includes('/artifacts'))return path.includes('/runs/'+t.preflights[0].run.id+'/')?artifacts(t):[t.artifact];
      if(path.includes('/release-receipt.yml/'))return[t.emitter_run];
      return[t.normal_run];
    },
    get:path=>{const t=target(path.split('/').slice(1,3).join('/'));return path.includes('/actions/artifacts/')?artifacts(t).find(a=>String(a.id)===path.split('/').at(-1)):t.pr;},
    tree:(repo,ref)=>ref===target(repo).receipt.merge.sha?target(repo).merge_tree:target(repo).tested_tree,
    object:(repo,ref,path)=>{const t=target(repo);if(path in t.trusted)return{sha:t.trusted[path]};const policy=p.manifest.targets.find(x=>x.repository===repo),parent=Object.keys(policy.parents).find(x=>policy.parents[x]===path);return{sha:t.parent_gitlinks[parent],type:'commit',mode:'160000'};}
  };
}
test('collector resolves full GitHub-shaped evidence for nine actual postmerge commits',()=>{
  const f=fixture();const actual=collect(collectorFixture(f),f.p);
  assert.equal(Object.keys(actual.record.targets).length,9);assert.equal(actual.files[f.p.candidate.archive].toString(),'zip');
});
test('collector rejects PR-head evidence even when caller supplies a matching final merge tree',()=>{
  const f=fixture(),t=f.e.targets['csharp-cli'];t.normal_run.event='pull_request';t.normal_run.head_sha=t.receipt.merge.head_sha;
  assert.throws(()=>collect(collectorFixture(f),f.p),/post-merge/);
});
test('collector rejects a second normal push/manual run across event types',()=>{
  const f=fixture(),g=collectorFixture(f),pages=g.pages;
  g.pages=path=>{
    const runs=pages(path);
    if(path.includes('/wss-compatibility.yml/runs?branch=dev&head_sha='))
      return [...runs,{...runs[0],id:999,event:'push'}];
    return runs;
  };
  assert.throws(()=>collect(g,f.p),/Ambiguous committed-state CI/);
});
test('collector rejects a receipt selecting a different exact-head normal run',()=>{
  const f=fixture(),g=collectorFixture(f),pages=g.pages;
  g.pages=path=>{
    const runs=pages(path);
    return path.includes('/wss-compatibility.yml/runs?branch=dev&head_sha=')?
      runs.map(r=>({...r,id:999})):runs;
  };
  assert.throws(()=>collect(g,f.p),/different from receipt/);
});
for(const channel of ['dev','main'])for(const corrupt of [false,true]) test(`existing ${channel} release ${corrupt?'rejects altered ZIP without a mutation':'replays as verified no-op without creating another release'}`,()=>{
  const {p,e}=fixture(channel),tag=releaseFingerprint(channel,p.candidate.run_id,p.plan_id),record=allClear(p,e);
  const files={[p.candidate.archive]:Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${p.candidate.sha256}  ${p.candidate.archive}\n`)};
  const expected={...files,'plan.json':Buffer.from(canonical(p)+'\n'),'acceptance.json':Buffer.from(canonical({...record,tag,legacy_tag_guard:p.manifest.legacy_tag_guard})+'\n')};
  let mutations=0;
  const g={get:()=>tagRule(),pages:path=>path.includes('matching-refs')?[{ref:`refs/tags/${tag}`,object:{type:'commit',sha:p.candidate.commit}}]:[{tag_name:tag,prerelease:channel==='dev',draft:false,assets:Object.keys(expected).map(name=>({name}))}],command:args=>{
    if(args[0]==='release'&&args[1]==='download'){
      const directory=args[args.indexOf('--dir')+1];
      for(const [name,bytes]of Object.entries(expected))writeFileSync(join(directory,name),corrupt&&name===p.candidate.archive?Buffer.from('corrupt'):bytes);
    } else mutations++;
  }};
  if(corrupt)assert.throws(()=>publish(g,p,{record,files},'.'),/bytes mismatch/);
  else assert.deepEqual(publish(g,p,{record,files},'.'),{tag,state:'verified-existing'});
  assert.equal(mutations,0);
});

test('verification-only freeze accepts an explicitly unconfigured tag guard but cannot publish',()=>{
  const {p}=fixture();p.manifest.legacy_tag_guard={ruleset_id:null,policy_sha256:null};
  const g={head:()=>sha(1),object:()=>({sha:sha(2)}),get:path=>{
    assert.ok(!path.includes('/rulesets/'));
    return {path:'.github/workflows/release-receipt.yml',state:'active'};
  },command:()=>assert.fail('Verification must never publish')};
  const accepted=freeze(g,p.manifest,p.candidate,{verificationOnly:true});
  assert.equal(Object.keys(accepted.target_snapshots).length,9);
  assert.throws(()=>publish(g,accepted,{record:{},files:{}},'.'),/unconfigured/);
  assert.throws(()=>freeze(g,p.manifest,p.candidate),/unconfigured/);
});

test('verification mode does not ignore malformed tag protection configuration',()=>{
  const {p}=fixture();p.manifest.legacy_tag_guard={ruleset_id:null,policy_sha256:'a'.repeat(64)};
  assert.throws(()=>freeze({},p.manifest,p.candidate,{verificationOnly:true}),/unconfigured/);
});

for(const channel of ['dev','main'])test(`${channel} publisher recollects cross-repository proof with its separate reader`,()=>{
  const f=fixture(channel),reader=collectorFixture(f),get=reader.get;
  reader.get=path=>path.includes('/rulesets/')?tagRule():get(path);
  const accepted=collect(reader,f.p),writes=[];
  const writer={pages:path=>{
    assert.ok(path.startsWith(`repos/${CORE}/`),'Core publication token must not read downstream evidence');
    return [];
  },command:args=>{
    assert.deepEqual(args.slice(0,2),['release','create']);assert.ok(args.includes(CORE));assert.equal(args.includes('--prerelease'),channel==='dev');assert.ok(args[2].startsWith(channel==='dev'?'dev-beta-':'main-stable-'));writes.push(args);
  }};
  const directory=mkdtempSync(join(tmpdir(),'wss-publication-boundary-'));
  try {
    assert.equal(publish(writer,f.p,accepted,directory,reader).state,'created');
    assert.equal(writes.length,1);
  } finally {rmSync(directory,{recursive:true,force:true});}
});

function selectedFixture(channel='dev') {
  const f=fixture(channel),{p,e}=f;
  p.manifest.test_selection_policy={schema:CAMPAIGN_TEST_POLICY,targets:p.manifest.targets.map(t=>({id:t.id,repository:t.repository,default_test_repository:'cwru-non-academic/TestBundle',allowed_test_repositories:['cwru-non-academic/TestBundle']}))};
  const selection=resolveTestSelection(selectionRequest(p.manifest,p.candidate),{head:()=>sha(99)});
  Object.assign(p,selectionFields(p.manifest,p.candidate,selection));p.manifest_sha256=digest(p.manifest);
  const {plan_id,...body}=p;p.plan_id=digest(body);
  for(const t of p.manifest.targets) {
    const x=e.targets[t.id],evidence=targetTestEvidence(p,t.id);
    const proof=run=>({artifact:{id:900+Number(run.id),name:selectedTestArtifactName(t.id,run.run_attempt),expired:false,workflow_run:{id:run.id,head_sha:run.head_sha}},bytes:Buffer.from(JSON.stringify(evidence))});
    x.receipt.plan_id=p.plan_id;x.artifact.name=`wss-acceptance-${t.id}-${p.plan_id}-a1`;
    x.receipt.selected_tests=evidence;x.selected_tests=proof(x.normal_run);
    for(const preflight of x.preflights)preflight.selected_tests=proof(preflight.run);
  }
  sync(e);return f;
}
function selectedCollector(f) {
  const g=collectorFixture(f),artifact=g.artifact,download=g.download;
  const proof=(repo,id)=>{const t=Object.values(f.e.targets).find(x=>x.normal_run.repository.full_name===repo);return String(t.normal_run.id)===id?t.selected_tests:t.preflights.find(x=>String(x.run.id)===id).selected_tests;};
  g.artifact=(repo,id,name)=>name.startsWith('wss-selected-tests-')?proof(repo,id).artifact:artifact(repo,id,name);
  g.download=(repo,id,name,files)=>name.startsWith('wss-selected-tests-')?{'evidence.json':proof(repo,id).bytes}:download(repo,id,name,files);
  return g;
}
for(const channel of ['dev','main'])test(`${channel} collector requires authenticated normal and preflight selected-test artifacts`,()=>{
  const f=selectedFixture(channel),record=collect(selectedCollector(f),f.p).record;
  assert.equal(record.test_selection_id,f.p.test_selection_id);
  assert.equal(record.targets['python-cli'].selected_tests.test_commit,sha(99));
});
for(const [name,mutate] of Object.entries({
  'missing normal artifact':t=>delete t.selected_tests,
  'missing preflight artifact':t=>delete t.preflights[0].selected_tests,
  'foreign artifact owner':t=>t.selected_tests.artifact.workflow_run.id=999,
  'foreign artifact source':t=>t.selected_tests.artifact.workflow_run.head_sha=sha(88),
  'expired artifact':t=>t.selected_tests.artifact.expired=true,
  'stale attempt':t=>t.selected_tests.artifact.name=t.selected_tests.artifact.name.replace('-a1','-a2'),
  'receipt-only forged source':t=>t.receipt.selected_tests.test_commit=sha(88),
  'artifact wrong source':t=>{const data=JSON.parse(t.selected_tests.bytes);data.test_commit=sha(88);t.selected_tests.bytes=Buffer.from(JSON.stringify(data));},
  'artifact wrong selection id':t=>{const data=JSON.parse(t.selected_tests.bytes);data.selection_id='b'.repeat(64);t.selected_tests.bytes=Buffer.from(JSON.stringify(data));},
  'preflight wrong selection id':t=>{const data=JSON.parse(t.preflights[0].selected_tests.bytes);data.selection_id='b'.repeat(64);t.preflights[0].selected_tests.bytes=Buffer.from(JSON.stringify(data));},
  'old product check still required':t=>t.merge_tree=sha(88),
  'old job check still required':t=>t.normal_jobs.pop()
}))test(`selected tests reject ${name}`,()=>{
  const f=selectedFixture();mutate(f.e.targets['python-cli']);sync(f.e);assert.throws(()=>allClear(f.p,f.e));
});

for(const channel of ['dev','main'])for(const id of TARGETS)test(`independent Core ${channel}/${id} rejects plausible receiver proof from another frozen source`,()=>{
  const {p,e}=fixture(channel),t=e.targets[id];
  t.receipt.preflights[0].source_sha=sha(99);t.preflights[0].run.head_sha=sha(99);sync(e);
  assert.throws(()=>allClear(p,e),/Preflight source differs/);
});

for(const channel of ['dev','main'])for(const id of TARGETS)for(const mutation of ['attempt','jobs','artifacts','logs'])test(`independent ${id}/${channel} rejects replaced native ${mutation}`,()=>{
 const {p,e}=fixture(channel),a=e.targets[id].preflights[0];
 if(mutation==='attempt')a.run.run_attempt=2;else a[mutation]=mutation==='logs'?[{job_id:'900',sha256:'2'.repeat(64)}]:[];
 assert.throws(()=>allClear(p,e));
});
for(const channel of ['dev','main'])test(`${channel} collector rejects native attempt movement after other receivers were collected`,()=>{
 const f=fixture(channel),g=collectorFixture(f),run=g.run;let firstReads=0;
 g.run=(repo,id)=>{const r=run(repo,id);return id==='300'&&++firstReads>=3?{...r,run_attempt:2}:r;};
 assert.throws(()=>collect(g,f.p),/attempt/);
});
import {freezeWorkflows,collectWithWorkflows,guardedWriter} from '../ci/workflow-freeze.mjs';
// All reads and writes below are synthetic. Return detached metadata so a late
// mutation cannot accidentally update an earlier evidence observation by alias.
function boundaryFixture(channel,selected=false) {
 const f=selected?selectedFixture(channel):fixture(channel),g=selected?selectedCollector(f):collectorFixture(f),get=g.get,object=g.object,head=g.head;
 let freezing=true;
 g.get=path=>path.includes('/rulesets/')?tagRule():path===`repos/${CORE}/contents/${channelContext(channel).targetPolicy}?ref=${f.p.candidate.commit}`?{type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(f.p.manifest)).toString('base64')}:get(path);
 g.object=(repo,ref,path)=>path==='.github/workflows'?{type:'tree',sha:sha(777)}:object(repo,ref,path);
 g.head=(repo,branch)=>repo===CORE?head(repo,branch):branch!==channel?sha(500):freezing?sha(2):head(repo,branch);
 Object.assign(f.p,freezeWorkflows(g,f.p,'acceptance'));freezing=false;
 for(const [id,t]of Object.entries(f.e.targets)){t.receipt.plan_id=f.p.plan_id;t.artifact.name=`wss-acceptance-${id}-${f.p.plan_id}-a1`;}
 const logs=Buffer.from('synthetic native log');
 for(const t of Object.values(f.e.targets)) {t.receipt.preflights[0].logs=[{job_id:'900',sha256:bytesDigest(logs)}];t.preflights[0].logs=t.receipt.preflights[0].logs;}
 sync(f.e);g.jobLogBytes=()=>logs;
 for(const name of ['run','jobs','pages','get','artifact']){const read=g[name];g[name]=(...args)=>structuredClone(read(...args));}
 return {...f,g};
}
const lateChanges={
 target:f=>f.e.targets['python-cli'].current_head=sha(999),
 parent:f=>f.e.targets['csharp-wrapper'].current_head=sha(999),
 Core:f=>f.e.producer.current_head=sha(999),
 attempt:f=>f.e.targets['csharp-wrapper'].normal_run.run_attempt=2,
 status:f=>f.e.targets['csharp-wrapper'].normal_run.status='in_progress',
 jobs:f=>f.e.targets['csharp-wrapper'].normal_jobs[0].conclusion='failure',
 producer:f=>f.e.producer.run.conclusion='failure',
 producerJobs:f=>f.e.producer.jobs[0].conclusion='failure',
 id:f=>{const pages=f.g.pages;f.g.pages=path=>path.includes(`/${f.p.manifest.targets[0].workflow.split('/').at(-1)}/runs?`)?pages(path).map(r=>({...r,id:999})):pages(path);},
 ambiguous:f=>{const pages=f.g.pages;f.g.pages=path=>path.includes(`/${f.p.manifest.targets[0].workflow.split('/').at(-1)}/runs?`)?[...pages(path),{...pages(path)[0],id:999}]:pages(path);}
};
function lateHook(f,phase,mutate) {
 let fired=false,count=0;
 const last=f.p.manifest.targets.at(-1).repository;
 const trigger=()=>{if(!fired){fired=true;mutate(f);}};
 const name=phase==='native-artifact'?'artifactBytes':phase==='native-log'?'jobLogBytes':phase==='receiver'?'download':phase==='workflow'?'object':'jobs';
 const read=f.g[name];
 f.g[name]=(...args)=>{
  const value=read(...args);
  if(phase==='native-artifact'&&args[0]===last&&args[1]==='901'&&++count===2||
     phase==='native-log'&&args[0]===last&&++count===2||
     phase==='receiver'&&args[0]===last||
     phase==='workflow'&&args[0]===last&&args[2]==='.github/workflows'||
     phase==='metadata'&&args[0]===last&&args[1].path!=='.github/workflows/wss-preflight.yml')trigger();
  return value;
 };
 return ()=>assert.equal(fired,true,'Late mutation hook must execute');
}
for(const channel of ['dev','main'])for(const phase of ['native-artifact','native-log','receiver'])for(const [change,mutate]of Object.entries(lateChanges))test(`final admission ${channel} rejects ${change} during ${phase}`,()=>{
 const f=boundaryFixture(channel),fired=lateHook(f,phase,mutate);
 assert.throws(()=>collect(f.g,f.p));fired();
});
for(const channel of ['dev','main'])for(const phase of ['workflow','metadata'])for(const [change,mutate]of Object.entries(lateChanges).filter(([name])=>phase==='workflow'||['target','parent','Core'].includes(name)))test(`publication admission ${channel} rejects ${change} during final ${phase} reads`,()=>{
 const f=boundaryFixture(channel),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[];
 const writer=guardedWriter({command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedHeads:Object.fromEntries(Object.entries(accepted.record.targets).map(([id,t])=>[id,t.merge.sha])),acceptedAdmission:accepted});
 const fired=lateHook(f,phase,mutate);
 const args=['release','create',releaseFingerprint(channel,f.p.candidate.run_id,f.p.plan_id),'--repo',CORE,'--target',f.p.candidate.commit,...(channel==='dev'?['--prerelease']:[]),'--title','fixture','--notes','synthetic'];
 assert.throws(()=>writer.command(args));fired();assert.equal(writes.length,0);
});
for(const channel of ['dev','main'])test(`publication admission ${channel} accepts merged heads different from frozen baselines`,()=>{
 const f=boundaryFixture(channel),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[];
 const writer=guardedWriter({pages:()=>[],command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedHeads:Object.fromEntries(Object.entries(accepted.record.targets).map(([id,t])=>[id,t.merge.sha])),acceptedAdmission:accepted});
 const directory=mkdtempSync(join(tmpdir(),'final-admission-'));
 try{assert.equal(publish(writer,f.p,accepted,directory,f.g).state,'created');assert.equal(writes.length,1);}finally{rmSync(directory,{recursive:true,force:true});}
});
for(const channel of ['dev','main'])for(const change of ['target','Core','parent','attempt'])test(`publication recollection ${channel} rejects late native ${change} with zero writes`,()=>{
 const f=boundaryFixture(channel),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[];
 const writer=guardedWriter({pages:()=>[],command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const fired=lateHook(f,'native-artifact',lateChanges[change]),directory=mkdtempSync(join(tmpdir(),'final-native-admission-'));
 try {assert.throws(()=>publish(writer,f.p,accepted,directory,f.g));fired();assert.equal(writes.length,0);}finally{rmSync(directory,{recursive:true,force:true});}
});
for(const channel of ['dev','main'])test(`final admission ${channel} retains one independent end-of-collection native pass`,()=>{
 const f=boundaryFixture(channel),read=f.g.artifactBytes,counts=new Map();
 f.g.artifactBytes=(repo,id,...args)=>{if(id==='901')counts.set(repo,(counts.get(repo)||0)+1);return read(repo,id,...args);};
 collect(f.g,f.p);
 assert.deepEqual([...counts.values()],Array(9).fill(2));
});
for(const channel of ['dev','main'])for(const change of ['attempt','status','jobs','id','ambiguous'])test(`publication admission ${channel} rejects normal ${change} changing within its metadata reads`,()=>{
 const f=boundaryFixture(channel),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[],jobs=f.g.jobs;
 let fired=false;
 f.g.jobs=(repo,r)=>{const value=jobs(repo,r);if(!fired&&repo===f.p.manifest.targets[0].repository){fired=true;lateChanges[change](f);return jobs(repo,r);}return value;};
 const writer=guardedWriter({command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const args=['release','create',releaseFingerprint(channel,f.p.candidate.run_id,f.p.plan_id),'--repo',CORE,'--target',f.p.candidate.commit,...(channel==='dev'?['--prerelease']:[]),'--title','fixture','--notes','synthetic'];
 assert.throws(()=>writer.command(args));assert.equal(fired,true);assert.equal(writes.length,0);
});
for(const channel of ['dev','main'])test(`publication admission ${channel} retains captured producer attempt despite client aliasing`,()=>{
 const f=boundaryFixture(channel),read=f.g.run;
 f.g.run=(repo,id)=>repo===CORE?f.e.producer.run:read(repo,id);
 const accepted=collectWithWorkflows(f.g,f.p,collect),writes=[];
 f.e.producer.run.run_attempt=2;
 assert.equal(accepted.admission.producer.run.run_attempt,1);
 const writer=guardedWriter({command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const args=['release','create',releaseFingerprint(channel,f.p.candidate.run_id,f.p.plan_id),'--repo',CORE,'--target',f.p.candidate.commit,...(channel==='dev'?['--prerelease']:[]),'--title','fixture','--notes','synthetic'];
 assert.throws(()=>writer.command(args),/Producer attempt/);assert.equal(writes.length,0);
});
for(const channel of ['dev','main'])for(const field of ['run_attempt','status'])test(`publication admission ${channel} rejects producer ${field} moving during final artifact metadata`,()=>{
 const f=boundaryFixture(channel),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[],artifact=f.g.artifact;
 let fired=false;
 f.g.artifact=(repo,...args)=>{const value=artifact(repo,...args);if(repo===CORE&&!fired){fired=true;f.e.producer.run[field]=field==='run_attempt'?2:'in_progress';}return value;};
 const writer=guardedWriter({command:args=>writes.push(args)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const args=['release','create',releaseFingerprint(channel,f.p.candidate.run_id,f.p.plan_id),'--repo',CORE,'--target',f.p.candidate.commit,...(channel==='dev'?['--prerelease']:[]),'--title','fixture','--notes','synthetic'];
 assert.throws(()=>writer.command(args),/Producer attempt|Run repository\/workflow\/status/);assert.equal(fired,true);assert.equal(writes.length,0);
});
for(const channel of ['dev','main'])for(const field of ['run_attempt','status'])test(`final collection ${channel} rejects producer ${field} moving during admission artifact metadata`,()=>{
 const f=boundaryFixture(channel),artifact=f.g.artifact;let reads=0,fired=false;
 f.g.artifact=(repo,...args)=>{const value=artifact(repo,...args);if(repo===CORE&&++reads===2){fired=true;f.e.producer.run[field]=field==='run_attempt'?2:'in_progress';}return value;};
 assert.throws(()=>collect(f.g,f.p),/Producer attempt|Run repository\/workflow\/status/);assert.equal(fired,true);
});

const completeChanges={
 emitterAttempt:f=>f.e.targets['csharp-wrapper'].emitter_run.run_attempt++,
 emitterStatus:f=>f.e.targets['csharp-wrapper'].emitter_run.conclusion='failure',
 receiptExpired:f=>f.e.targets['csharp-wrapper'].artifact.expired=true,
 receiptReplaced:f=>f.e.targets['csharp-wrapper'].artifact.id++,
 preflightAttempt:f=>f.e.targets['csharp-wrapper'].preflights[0].run.run_attempt++,
 preflightStatus:f=>f.e.targets['csharp-wrapper'].preflights[0].run.conclusion='failure',
 preflightJobs:f=>f.e.targets['csharp-wrapper'].preflights[0].jobs[0].conclusion='failure',
 nativeExpired:f=>{const get=f.g.get;f.g.get=p=>{const v=get(p);return p.endsWith('/actions/artifacts/901')?{...v,expired:true}:v;};},
 tagRule:f=>{const get=f.g.get;f.g.get=p=>{const v=get(p);return p.includes('/rulesets/')?{...v,enforcement:'disabled'}:v;};},
 tagAppears:f=>{const pages=f.g.pages;f.g.pages=p=>p.includes('/git/matching-refs/')?[{ref:`refs/tags/${releaseFingerprint(f.p.channel,f.p.candidate.run_id,f.p.plan_id)}`}]:pages(p);},
 releaseAppears:f=>{const pages=f.g.pages;f.g.pages=p=>p.endsWith('/releases')?[{tag_name:releaseFingerprint(f.p.channel,f.p.candidate.run_id,f.p.plan_id)}]:pages(p);}
};
for(const channel of ['dev','main'])for(const selected of [false,true])for(const [name,mutate]of Object.entries({...lateChanges,...completeChanges}))test(`V7 complete publication ${channel} selected=${selected} rejects late ${name}`,()=>{
 const f=boundaryFixture(channel,selected),pages=f.g.pages;f.g.pages=p=>p.includes('/git/matching-refs/')||p.endsWith('/releases')?[]:pages(p);
 const accepted=collectWithWorkflows(f.g,f.p,collect),writes=[],writer=guardedWriter({pages:()=>[],command:a=>writes.push(a)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const fired=lateHook(f,'workflow',mutate),dir=mkdtempSync(join(tmpdir(),'v7-complete-'));
 try{assert.throws(()=>publish(writer,f.p,accepted,dir,f.g));fired();assert.equal(writes.length,0);}finally{rmSync(dir,{recursive:true,force:true});}
});

for(const channel of ['dev','main'])for(const selected of [false,true])for(const change of ['stable','nativeDuplicate','nativeReplaced','nativeDigest','receiptDuplicate','competingEmitter','normalSelectedExpired','preflightSelectedExpired','normalSelectedReplaced','preflightSelectedReplaced','unrelatedArtifacts','historicalCandidate','historicalPR','metadataRunChange','nativeAbsent','receiptAbsent','apiError','paginationError'].filter(change=>selected||!change.includes('Selected')))test(`V7 full ${channel} selected=${selected} ${change}`,()=>{
 const f=boundaryFixture(channel,selected),accepted=collectWithWorkflows(f.g,f.p,collect),writes=[];
 const writer=guardedWriter({pages:()=>[],command:a=>writes.push(a)},f.g,f.p,'acceptance',{purpose:'publication',acceptedAdmission:accepted});
 const positive=['stable','unrelatedArtifacts','historicalCandidate','historicalPR'].includes(change);
 const fired=lateHook(f,'workflow',()=>{
   const t=f.e.targets['csharp-wrapper'],get=f.g.get,pages=f.g.pages,artifact=f.g.artifact;
   if(change==='nativeReplaced'||change==='nativeDigest')f.g.get=p=>{const a=get(p);return p.endsWith('/actions/artifacts/901')?{...a,...(change==='nativeReplaced'?{id:999}:{digest:'sha256:'+'0'.repeat(64)})}:a;};
   if(change==='nativeDuplicate'||change==='unrelatedArtifacts')f.g.pages=p=>{const a=pages(p);return p.includes('/runs/300/artifacts')?[...a,change==='nativeDuplicate'?a[0]:{id:999,name:'unrelated'}]:a;};
   if(change==='nativeAbsent')f.g.pages=p=>p.includes('/runs/300/artifacts')?pages(p).filter(a=>a.name!=='rich-proof'):pages(p);
   if(change==='apiError'||change==='paginationError')f.g.pages=p=>{if(p.includes('/release-receipt.yml/runs?'))throw Error(change==='apiError'?'API unavailable':'Incomplete GitHub pagination');return pages(p);};
   if(change==='receiptDuplicate'||change==='receiptAbsent')f.g.artifact=(repo,id,name)=>{if(repo===t.normal_run.repository.full_name&&name.startsWith('wss-acceptance-'))throw Error('duplicate receipt artifact');return artifact(repo,id,name);};
   if(change==='competingEmitter')f.g.pages=p=>p.includes('/release-receipt.yml/runs?')&&p.includes(t.normal_run.repository.full_name)?[...pages(p),{...t.emitter_run,id:999}]:pages(p);
   if(change.includes('Selected')){const proof=change.startsWith('normal')?t.selected_tests:t.preflights[0].selected_tests;if(change.endsWith('Expired'))proof.artifact.expired=true;else proof.artifact.id++;}
   if(change==='historicalCandidate')f.g.get=p=>{const a=get(p);return p.endsWith('/actions/artifacts/902')?{...a,expired:true}:a;};
   if(change==='historicalPR'){t.pr.title='unrelated';t.pr.body='new commentary';}
   if(change==='metadataRunChange'){const jobs=f.g.jobs;f.g.jobs=(repo,r)=>{const a=jobs(repo,r);if(String(r.id)==='300')t.preflights[0].run.run_attempt++;return a;};}
   // No body acquisition is allowed after the final workflow observation.
   for(const name of ['download','artifactBytes','jobLogBytes'])f.g[name]=()=>assert.fail('Final refresh downloaded bytes');
 });
 const dir=mkdtempSync(join(tmpdir(),'v7-full-'));
 try{if(positive){assert.equal(publish(writer,f.p,accepted,dir,f.g).state,'created');assert.equal(writes.length,1);}else{assert.throws(()=>publish(writer,f.p,accepted,dir,f.g));assert.equal(writes.length,0);}fired();}finally{rmSync(dir,{recursive:true,force:true});}
});
