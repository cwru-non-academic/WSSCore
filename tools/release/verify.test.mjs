import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {allClear,bytesDigest,canonical,CORE,CORE_JOBS,digest,plan,TARGETS} from './verify.mjs';
import {collect,freeze,GitHub,publish} from './github.mjs';
import {tagPolicy} from './policy.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
const successful=names=>names.map(name=>({name,status:'completed',conclusion:'success'}));
function tagRule() {return{id:123,target:'tag',enforcement:'active',source_type:'Repository',source:CORE,conditions:{ref_name:{include:['refs/tags/v*'],exclude:[]}},bypass_actors:[],rules:[{type:'creation'},{type:'update'},{type:'deletion'}]};}
function run(repo,workflow,n,head,event='workflow_dispatch') {
  return {id:n,repository:{full_name:repo},head_repository:{full_name:repo},path:workflow,head_sha:head,head_branch:'dev',event,status:'completed',conclusion:'success',run_attempt:1};
}
function fixture() {
  const manifest=JSON.parse(readFileSync(new URL('./targets.dev.json',import.meta.url)));
  manifest.legacy_tag_guard={ruleset_id:'123',policy_sha256:digest(tagPolicy(tagRule()))};
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(Buffer.from('zip'))};
  const body={schema:'wss-propagation-plan/v1',channel:'dev',candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:sha(2),trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,sha(3)]))}]))};
  const p={...body,plan_id:digest(body)};
  const e={producer:{run:run(CORE,'.github/workflows/artifacts.yml',1,sha(1),'push'),current_head:sha(1),jobs:successful(CORE_JOBS),artifact:{id:2,name:'universal-release-assets',expired:false,workflow_run:{id:1,head_sha:sha(1)}},archive_sha256:candidate.sha256},targets:{}};
  manifest.targets.forEach((t,i)=>{
    const merge=sha(10+i),head=sha(30+i),normal=100+i,emitter=200+i;
    const parents=Object.fromEntries(Object.keys(t.parents).map(id=>[id,sha(10+TARGETS.indexOf(id))]));
    const r={schema:'wss-acceptance-receipt/v1',plan_id:p.plan_id,channel:'dev',target_id:t.id,candidate,mode:'merged-pr',
      merge:{pr_number:String(i+1),sha:merge,head_sha:head},normal_ci:{run_id:String(normal),attempt:1},
      preflights:[{repository:t.repository,run_id:String(300+i),source_sha:sha(2),workflow:'.github/workflows/wss-preflight.yml'}],
      parents,emitter:{run_id:String(emitter),attempt:1,source_sha:merge,workflow:'.github/workflows/release-receipt.yml'},coverage:t.coverage};
    e.targets[t.id]={receipt:r,receipt_bytes:Buffer.from(JSON.stringify(r)),pr:{number:i+1,merged:true,user:{type:'Bot'},merged_by:{type:'User'},state:'closed',merge_commit_sha:merge,head:{sha:head,ref:'automation/candidate',repo:{full_name:t.repository}},base:{ref:'dev',repo:{full_name:t.repository}}},
      current_head:merge,merge_tree:sha(4),tested_tree:sha(4),trusted:p.target_snapshots[t.id].trusted,parent_gitlinks:parents,
      normal_run:{...run(t.repository,t.workflow,normal,merge),pull_requests:[]},normal_latest_run_id:String(normal),normal_jobs:successful(t.jobs),
      emitter_run:run(t.repository,r.emitter.workflow,emitter,merge),artifact:{id:500+i,name:`wss-acceptance-${t.id}-${p.plan_id}-a1`,expired:false,workflow_run:{id:emitter,head_sha:merge}},
      preflights:[{run:run(t.repository,r.preflights[0].workflow,300+i,sha(2)),candidate}]};
    e.targets[t.id].receipt_sha256=bytesDigest(e.targets[t.id].receipt_bytes);
  });
  return {p,e};
}
function sync(e) {for(const t of Object.values(e.targets)){t.receipt_bytes=Buffer.from(JSON.stringify(t.receipt));t.receipt_sha256=bytesDigest(t.receipt_bytes);}}
test('all seven exact merged consumers generate an explicit beta acceptance record',()=>{
  const {p,e}=fixture(),r=allClear(p,e);
  assert.equal(r.kind,'beta');assert.equal(Object.keys(r.targets).length,7);assert.equal(r.excluded.every(x=>x.dispatch===false),true);
  assert.ok(r.targets['unity-gui'].coverage.includes('dotnet-harness-not-unity-editor'));
});
test('successful synthetic PR run cannot substitute for actual postmerge normal CI',()=>{
  const {p,e}=fixture();const n=e.targets['csharp-cli'];n.normal_run.event='pull_request';n.normal_run.head_sha=n.receipt.merge.head_sha;
  assert.throws(()=>allClear(p,e),/post-merge/);
});
const cases={
  'missing consumer':({e})=>delete e.targets['python-ros'],
  'MATLAB silently added':({e})=>e.targets['matlab-cli']=e.targets['csharp-cli'],
  'changed policy digest':({p})=>p.manifest.targets.pop(),
  'forged plan id':({p})=>p.plan_id='0'.repeat(64),
  'main channel':({p})=>p.channel='main',
  'failed Core producer':({e})=>e.producer.run.conclusion='failure',
  'Core manual producer':({e})=>e.producer.run.event='workflow_dispatch',
  'Core branch moved':({e})=>e.producer.current_head=sha(99),
  'Core skipped macOS':({e})=>e.producer.jobs.find(j=>j.name==='BLE consumer (macOS arm64)').conclusion='skipped',
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
  return {
    head:repo=>repo===CORE?e.producer.current_head:target(repo).current_head,
    run:(repo,id)=>repo===CORE?e.producer.run:[...entries().flatMap(t=>[t.normal_run,t.emitter_run,...t.preflights.map(x=>x.run)])].find(r=>r.repository.full_name===repo&&String(r.id)===id),
    jobs:(repo,r)=>repo===CORE?e.producer.jobs:target(repo).normal_jobs,
    artifact:(repo,id)=>repo===CORE?e.producer.artifact:target(repo).artifact,
    download:(repo,id,name)=>{
      if(repo===CORE)return{[p.candidate.archive]:Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${p.candidate.sha256}  ${p.candidate.archive}\n`)};
      if(name==='wss-candidate-provenance')return{'candidate.json':Buffer.from(JSON.stringify(p.candidate))};
      return{'receipt.json':target(repo).receipt_bytes};
    },
    pages:path=>{
      const repo=path.split('/').slice(1,3).join('/'),t=target(repo);
      if(path.includes('/artifacts'))return[t.artifact];
      if(path.includes('/release-receipt.yml/'))return[t.emitter_run];
      return[t.normal_run];
    },
    get:path=>target(path.split('/').slice(1,3).join('/')).pr,
    tree:(repo,ref)=>ref===target(repo).receipt.merge.sha?target(repo).merge_tree:target(repo).tested_tree,
    object:(repo,ref,path)=>{const t=target(repo);if(path in t.trusted)return{sha:t.trusted[path]};const policy=p.manifest.targets.find(x=>x.repository===repo),parent=Object.keys(policy.parents).find(x=>policy.parents[x]===path);return{sha:t.parent_gitlinks[parent],type:'commit',mode:'160000'};}
  };
}
test('collector resolves full GitHub-shaped evidence for seven actual postmerge commits',()=>{
  const f=fixture();const actual=collect(collectorFixture(f),f.p);
  assert.equal(Object.keys(actual.record.targets).length,7);assert.equal(actual.files[f.p.candidate.archive].toString(),'zip');
});
test('collector rejects PR-head evidence even when caller supplies a matching final merge tree',()=>{
  const f=fixture(),t=f.e.targets['csharp-cli'];t.normal_run.event='pull_request';t.normal_run.head_sha=t.receipt.merge.head_sha;
  assert.throws(()=>collect(collectorFixture(f),f.p),/post-merge/);
});
for(const corrupt of [false,true]) test(`existing release ${corrupt?'rejects altered ZIP without a mutation':'replays as verified no-op without creating another release'}`,()=>{
  const {p,e}=fixture(),tag=`dev-beta-${p.candidate.run_id}-${p.plan_id.slice(0,12)}`,record=allClear(p,e);
  const files={[p.candidate.archive]:Buffer.from('zip'),'SHA256SUMS.txt':Buffer.from(`${p.candidate.sha256}  ${p.candidate.archive}\n`)};
  const expected={...files,'plan.json':Buffer.from(canonical(p)+'\n'),'acceptance.json':Buffer.from(canonical({...record,tag,legacy_tag_guard:p.manifest.legacy_tag_guard})+'\n')};
  let mutations=0;
  const g={get:()=>tagRule(),pages:path=>path.includes('matching-refs')?[{ref:`refs/tags/${tag}`,object:{type:'commit',sha:p.candidate.commit}}]:[{tag_name:tag,prerelease:true,draft:false,assets:Object.keys(expected).map(name=>({name}))}],command:args=>{
    if(args[0]==='release'&&args[1]==='download'){
      const directory=args[args.indexOf('--dir')+1];
      for(const [name,bytes]of Object.entries(expected))writeFileSync(join(directory,name),corrupt&&name===p.candidate.archive?Buffer.from('corrupt'):bytes);
    } else mutations++;
  }};
  if(corrupt)assert.throws(()=>publish(g,p,{record,files},'.'),/bytes mismatch/);
  else assert.deepEqual(publish(g,p,{record,files},'.'),{tag,state:'verified-existing'});
  assert.equal(mutations,0);
});
