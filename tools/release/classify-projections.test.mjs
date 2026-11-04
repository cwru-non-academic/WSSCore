import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {readFileSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {assertCSharpCandidateLineage,classifyProjections,PROFILES,projectedPath,UNIVERSAL_FILES} from '../ci/classify-projections.mjs';
import {bytesDigest,CORE,digest} from './verify.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
function fixture(equal=[]) {
  const policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const receivers=Object.fromEntries(policy.libraries.map(t=>[t.id,{heads:{main:sha(2),dev:sha(3)},trusted:{main:{},dev:{}}}]));
  const body={schema:'wss-dev-propagation-plan/v1',candidate,policy,policy_sha256:digest(policy),receivers};
  const p={...body,plan_id:digest(body)},trees={},blobs=new Map(),records={};
  const hashes={sha256:candidate.sha256,files:Object.fromEntries(UNIVERSAL_FILES.map(path=>[path,bytesDigest(Buffer.from(`new:${path}`))]))};
  function add(repo,path,bytes) {
    const oid=createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
    blobs.set(oid,{sha:oid,encoding:'base64',content:bytes.toString('base64'),size:bytes.length});
    trees[repo].push({path,sha:oid,type:'blob',mode:'100644'});
  }
  for(const t of policy.libraries) {
    const profile=PROFILES[t.id];trees[t.repository]=[];
    const files=profile.sources.map(source=>{
      const bytes=Buffer.from(`${equal.includes(t.id)?'new':'old'}:${source}`),path=projectedPath(t.id,source);
      add(t.repository,`${profile.root}/${path}`,bytes);
      const item={source,sha256:bytesDigest(bytes)};
      if(t.id==='unity') {
        const meta=Buffer.from(`guid: ${source}\n`);add(t.repository,`${profile.root}/${path}.meta`,meta);
        item.meta_sha256=bytesDigest(meta);
      }
      return item;
    });
    const projection=files.sort((a,b)=>a.source<b.source?-1:1).map(f=>`${f.source}|${f.sha256}${t.id==='unity'?`|${f.meta_sha256}`:''}`).join('\n');
    const record=t.id==='csharp'?{schema:2,candidate,integration:{source_sha:sha(4),preflight_run_id:'11'},cli:{source_sha:sha(5),preflight_run_id:'12'}}:
      {schema:`wss-${t.id}-projection/v1`,candidate,files,projection_sha256:bytesDigest(Buffer.from(projection)),
        ...(t.id==='python'?{universal_contract:'macos-ble-v1'}:{}),...(t.id==='matlab'?{bootstrap_reference:false}:{})};
    records[t.id]=Buffer.from(JSON.stringify(record));add(t.repository,profile.record,records[t.id]);
    if(t.id==='matlab') add(t.repository,`${profile.root}/payload-source.json`,records[t.id]);
  }
  const g={head:(repo,branch='dev')=>repo===CORE?candidate.commit:branch==='main'?sha(2):sha(3),
    get:path=>{
      const commit=path.match(/^repos\/(cwru-non-academic\/[^/]+)\/git\/commits\/([a-f0-9]+)$/);
      if(commit) return {sha:commit[2],tree:{sha:sha(80)}};
      const tree=path.match(/^repos\/(cwru-non-academic\/[^/]+)\/git\/trees\/([a-f0-9]+)\?recursive=1$/);
      if(tree) return {sha:tree[2],truncated:false,tree:trees[tree[1]]};
      const blob=path.match(/\/git\/blobs\/([a-f0-9]+)$/);if(blob) return blobs.get(blob[1]);
      throw new Error(`Unexpected non-read query: ${path}`);
    }};
  return {p,g,hashes,trees,blobs,records};
}
test('all four changed projections permit only the existing reviewed promotion route',()=>{
  const f=fixture(),before=Object.values(f.records).map(b=>b.toString()),r=classifyProjections(f.g,f.p,f.hashes);
  assert.equal(r.downstream_writes_allowed,true);assert.equal(r.results.length,4);
  assert.ok(r.results.every(x=>x.status==='payload-change' && x.acceptance===false));
  assert.deepEqual(Object.values(f.records).map(b=>b.toString()),before);
  assert.equal(PROFILES.unity.sources.length,20);assert.equal(UNIVERSAL_FILES.length,27);
  assert.equal(projectedPath('unity','backends/windows/WssBleWindows.dll'),'WssBleWindows.dll');
  assert.equal(projectedPath('unity','backends/macos/libwss_macos_ble.dylib'),'backends/macos/libwss_macos_ble.dylib');
  assert.equal(projectedPath('unity','runtimes/unix/lib/net8.0/System.IO.Ports.dll'),'runtimes/unix/lib/net8.0/System.IO.Ports.dll');
});
test('all-equal and mixed targets stop the whole handoff without fabricating acceptance',()=>{
  for(const equal of [Object.keys(PROFILES),['unity']]) {
    const f=fixture(equal),r=classifyProjections(f.g,f.p,f.hashes);
    assert.equal(r.downstream_writes_allowed,false);
    for(const row of r.results) {
      assert.equal(row.status,equal.includes(row.target)?'equivalent-but-unsupported':'payload-change');
      assert.equal(row.acceptance,false);
    }
  }
});
test('a missing target record blocks writes while still reporting all four targets',()=>{
  const f=fixture(),profile=PROFILES.python;
  f.trees[profile.repository]=f.trees[profile.repository].filter(e=>e.path!==profile.record);
  const r=classifyProjections(f.g,f.p,f.hashes);
  assert.equal(r.downstream_writes_allowed,false);assert.equal(r.results.length,4);
  assert.equal(r.results.find(x=>x.target==='python').status,'blocked');
  assert.equal(r.results.filter(x=>x.status==='payload-change').length,3);
});
test('C# classification preserves exact candidate identities and rejects stale runs and Core lineages',()=>{
  const installed=fixture().p.candidate;
  const noRead={get:()=>assert.fail('Same-source ordering does not substitute API or timestamp heuristics')};
  assert.equal(assertCSharpCandidateLineage(noRead,installed,installed),'same-installed-candidate');
  assert.throws(()=>assertCSharpCandidateLineage(noRead,{...installed,sha256:'b'.repeat(64)},installed),/conflicting candidate identity/);
  const later={...installed,run_id:'3'};
  assert.equal(assertCSharpCandidateLineage(noRead,later,installed),'same-source-later-run');
  assert.throws(()=>assertCSharpCandidateLineage(noRead,installed,later),/roll back/);
  const next={...later,commit:sha(9)},g={get:path=>{
    assert.equal(path,`repos/${CORE}/compare/${installed.commit}...${next.commit}`);
    return {status:'ahead',base_commit:{sha:installed.commit},merge_base_commit:{sha:installed.commit}};
  }};
  assert.equal(assertCSharpCandidateLineage(g,next,installed),'descendant-core-source');
  for(const comparison of [
    {status:'behind',base_commit:{sha:installed.commit},merge_base_commit:{sha:installed.commit}},
    {status:'diverged',base_commit:{sha:installed.commit},merge_base_commit:{sha:sha(99)}},
    {status:'ahead',base_commit:{sha:sha(99)},merge_base_commit:{sha:installed.commit}},
    {status:'ahead',base_commit:{sha:installed.commit},merge_base_commit:{sha:sha(99)}},
  ]) assert.throws(()=>assertCSharpCandidateLineage({get:()=>comparison},next,installed),/roll back or diverge/);
  assert.throws(()=>assertCSharpCandidateLineage({get:()=>{throw new Error('API evidence unavailable');}},next,installed),/unavailable/);
});
test('an installed C# descendant blocks the whole mixed-target handoff',()=>{
  const f=fixture(),profile=PROFILES.csharp;
  const entry=f.trees[profile.repository].find(e=>e.path===profile.record);
  const record=JSON.parse(f.records.csharp);
  record.candidate={...record.candidate,commit:sha(9),run_id:'99'};
  const bytes=Buffer.from(JSON.stringify(record));
  entry.sha=createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
  f.blobs.set(entry.sha,{sha:entry.sha,encoding:'base64',content:bytes.toString('base64'),size:bytes.length});
  const get=f.g.get;f.g.get=path=>path.includes('/compare/')?
    {status:'behind',base_commit:{sha:record.candidate.commit},merge_base_commit:{sha:f.p.candidate.commit}}:get(path);
  const r=classifyProjections(f.g,f.p,f.hashes);
  assert.equal(r.downstream_writes_allowed,false);
  assert.equal(r.results.find(x=>x.target==='csharp').status,'blocked');
  assert.equal(r.results.filter(x=>x.status==='payload-change').length,3);
  assert.deepEqual(JSON.parse(bytes).candidate,record.candidate);
});
for(const [name,corrupt] of [
  ['missing ZIP evidence',f=>f.hashes=undefined],
  ['wrong ZIP digest',f=>f.hashes.sha256='b'.repeat(64)],
  ['missing Mac ZIP member',f=>delete f.hashes.files['backends/macos/libwss_macos_ble.dylib']],
  ['extra ZIP member',f=>f.hashes.files['arbitrary.dll']='a'.repeat(64)],
  ['case-aliased ZIP member',f=>f.hashes.files['wss_core_interface.dll']='a'.repeat(64)],
  ['hash-only invalid proof',f=>f.hashes.files['WSS_Core_Interface.dll']='not-a-hash'],
]) test(`classification blocks ${name}`,()=>{
  const f=fixture();corrupt(f);const r=classifyProjections(f.g,f.p,f.hashes);
  assert.equal(r.downstream_writes_allowed,false);assert.ok(r.results.every(x=>x.status==='blocked'));
});
for(const [name,corrupt] of [
  ['missing DLL',f=>f.trees[PROFILES.python.repository].shift()],
  ['wrong Git blob bytes',f=>{const b=f.blobs.get(f.trees[PROFILES.python.repository][0].sha);b.content=Buffer.from('altered').toString('base64');}],
  ['symlink DLL',f=>f.trees[PROFILES.python.repository][0].mode='120000'],
  ['truncated Git tree',f=>{const get=f.g.get;f.g.get=path=>{const value=get(path);return path.includes('/git/trees/')?{...value,truncated:true}:value;};}],
  ['case collision',f=>f.trees[PROFILES.python.repository].push({...f.trees[PROFILES.python.repository][0],path:'Cs_Libraries/wss_core_interface.dll'})],
  ['extra Unity DLL',f=>f.trees[PROFILES.unity.repository].push({...f.trees[PROFILES.unity.repository][0],path:'Plugins/WSS_Core_Interface/unreviewed.dll'})],
  ['missing Unity importer',f=>{const repo=PROFILES.unity.repository;f.trees[repo]=f.trees[repo].filter(e=>!e.path.endsWith('libwss_macos_ble.dylib.meta'));}],
  ['missing projection hash metadata',f=>{
    const entry=f.trees[PROFILES.python.repository].find(e=>e.path===PROFILES.python.record);
    const blob=f.blobs.get(entry.sha),record=JSON.parse(Buffer.from(blob.content,'base64'));
    delete record.projection_sha256;
    const bytes=Buffer.from(JSON.stringify(record));
    entry.sha=createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
    f.blobs.set(entry.sha,{sha:entry.sha,encoding:'base64',content:bytes.toString('base64'),size:bytes.length});
  }],
  ['moved receiver',f=>{const head=f.g.head;f.g.head=(repo,branch)=>repo===PROFILES.python.repository?sha(99):head(repo,branch);}],
  ['moved trusted main',f=>{const head=f.g.head;f.g.head=(repo,branch)=>repo===PROFILES.unity.repository&&branch==='main'?sha(99):head(repo,branch);}],
  ['unreviewed projection mapping',f=>{
    f.p.policy.libraries.find(t=>t.id==='python').record='.github/other-candidate.json';
    f.p.policy_sha256=digest(f.p.policy);const {plan_id,...body}=f.p;f.p.plan_id=digest(body);
  }],
  ['moved Core during reads',f=>{const head=f.g.head;let coreReads=0;f.g.head=(repo,branch)=>repo===CORE&&++coreReads>1?sha(99):head(repo,branch);}],
]) test(`classification rejects ${name} without allowing partial writes`,()=>{
  const f=fixture();corrupt(f);const r=classifyProjections(f.g,f.p,f.hashes);
  assert.equal(r.downstream_writes_allowed,false);assert.ok(r.results.some(x=>x.status==='blocked'));
});

test('ZIP reader checks actual bytes, exact inventory, duplicate/traversal entries and checksum',()=>{
  const directory=mkdtempSync(join(tmpdir(),'wss-classification-'));
  const archive=join(directory,'candidate.zip');
  const script=fileURLToPath(new URL('../ci/read-universal-hashes.ps1',import.meta.url));
  const entries=UNIVERSAL_FILES.map(path=>({name:`WSS-Universal/${path}`,content:Buffer.from(path).toString('base64')}));
  const build=`$ErrorActionPreference='Stop'; $zip=[IO.Compression.ZipFile]::Open($env:WSS_FIXTURE_ZIP,[IO.Compression.ZipArchiveMode]::Create); try { foreach($item in ($env:WSS_FIXTURE_ENTRIES | ConvertFrom-Json)) { $entry=$zip.CreateEntry($item.name); $stream=$entry.Open(); try { $bytes=[Convert]::FromBase64String($item.content); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() } } } finally { $zip.Dispose() }`;
  function make(list) {
    rmSync(archive,{force:true});
    execFileSync('pwsh',['-NoProfile','-Command',build],{env:{...process.env,WSS_FIXTURE_ZIP:archive,WSS_FIXTURE_ENTRIES:JSON.stringify(list)},stdio:'pipe'});
    return bytesDigest(readFileSync(archive));
  }
  const read=hash=>execFileSync('pwsh',['-NoProfile','-File',script,'-Archive',archive,'-ExpectedSha256',hash],{encoding:'utf8',stdio:'pipe'});
  try {
    const zipHash=make(entries),result=JSON.parse(read(zipHash));
    assert.equal(result.sha256,zipHash);assert.equal(Object.keys(result.files).length,27);
    assert.equal(result.files['backends/macos/libwss_macos_ble.dylib'],bytesDigest(Buffer.from('backends/macos/libwss_macos_ble.dylib')));
    assert.throws(()=>read('f'.repeat(64)));
    for(const invalid of [entries.slice(1),[...entries,entries[0]],
      [...entries,{name:'WSS-Universal/../outside.dll',content:'YQ=='}],
      [...entries,{name:'WSS-Universal/extra.dll',content:'YQ=='}]]) {
      const badHash=make(invalid);assert.throws(()=>read(badHash));
    }
  } finally {rmSync(directory,{recursive:true,force:true});}
});
