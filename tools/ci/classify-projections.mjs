import {isEntrypoint} from '../release/entrypoint.mjs';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync,writeFileSync,mkdirSync,appendFileSync} from 'node:fs';
import {dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {GitHub} from '../release/github.mjs';
import {bytesDigest,candidate,canonical,CORE,requireThat} from '../release/verify.mjs';
import {validatePlan} from './dev-propagation.mjs';
import {assertWorkflows} from './workflow-freeze.mjs';
import {channelContext} from '../release/channels.mjs';

export const UNIVERSAL_FILES=JSON.parse(readFileSync(new URL('../ArtifactBuild/manifests/universal.json',import.meta.url))).files;
const legacy=UNIVERSAL_FILES.filter(path=>!path.startsWith('backends/macos/'));
const unity=UNIVERSAL_FILES.filter(path=>!path.includes('/') || path.startsWith('backends/windows/') ||
  path.startsWith('backends/macos/') || path.startsWith('runtimes/'));
const unityLegacy=unity.filter(path=>!path.startsWith('backends/macos/'));
export const PROFILES={
  csharp:{repository:'cwru-non-academic/CSharp_WSS_Integration_Lib',root:'lib',record:'lib/wss-candidate.json',sources:UNIVERSAL_FILES},
  unity:{repository:'cwru-non-academic/Unity_WSS_Integration_Lib',root:'Plugins/WSS_Core_Interface',record:'.github/unity-candidate.json',sources:unity},
  python:{repository:'cwru-non-academic/Python_WSS_Integration_Lib',root:'Cs_Libraries',record:'.github/python-candidate.json',sources:UNIVERSAL_FILES},
  matlab:{repository:'cwru-non-academic/Matlab_WSS_Integration_Lib',root:'Cs_Libraries',record:'.github/matlab-candidate.json',sources:UNIVERSAL_FILES},
};
const same=(a,b)=>canonical(a)===canonical(b);
const hash=value=>typeof value==='string' && /^[a-f0-9]{64}$/.test(value);
export const projectedPath=(id,source)=>id==='unity' && !source.startsWith('runtimes/') && !source.startsWith('backends/macos/') ? source.split('/').at(-1):source;
const inventory=(actual,expected)=>Array.isArray(actual) && new Set(actual.map(x=>String(x).toLowerCase())).size===actual.length &&
  same([...actual].sort(),[...expected].sort());
function gitBlob(g,repository,entry,cache) {
  requireThat(entry?.type==='blob' && ['100644','100755'].includes(entry.mode) && /^[a-f0-9]{40}$/.test(entry.sha),'Missing/nonregular immutable Git file');
  if(!cache.has(entry.sha)) {
    const blob=g.get(`repos/${repository}/git/blobs/${entry.sha}`);
    requireThat(blob.sha===entry.sha && blob.encoding==='base64' && Number.isSafeInteger(blob.size) && blob.size>0 && blob.size<=67108864,'Invalid Git blob evidence');
    const bytes=Buffer.from(blob.content,'base64');
    requireThat(bytes.length===blob.size && createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')===entry.sha,'Git blob bytes differ from frozen tree');
    cache.set(entry.sha,bytes);
  }
  return cache.get(entry.sha);
}
function installed(g,t,head,cache) {
  const profile=PROFILES[t.id];
  requireThat(t.repository===profile.repository && t.record===profile.record,'Unreviewed receiver projection profile');
  const commit=g.get(`repos/${t.repository}/git/commits/${head}`);
  requireThat(commit.sha===head && /^[a-f0-9]{40}$/.test(commit.tree?.sha),'Missing immutable receiver commit');
  const response=g.get(`repos/${t.repository}/git/trees/${commit.tree.sha}?recursive=1`);
  requireThat(response.sha===commit.tree.sha && response.truncated===false && Array.isArray(response.tree),'Missing/truncated frozen receiver tree');
  const entries=new Map();
  for(const entry of response.tree) {
    requireThat(typeof entry.path==='string' && !entries.has(entry.path.toLowerCase()),'Duplicate/case-colliding receiver tree path');
    entries.set(entry.path.toLowerCase(),entry);
  }
  function read(path) {
    const entry=entries.get(path.toLowerCase());
    requireThat(entry?.path===path,`Missing exact frozen file: ${path}`);
    return gitBlob(g,t.repository,entry,cache);
  }
  const recordBytes=read(profile.record),record=JSON.parse(recordBytes.toString('utf8').replace(/^\uFEFF/,''));
  candidate(record.candidate);
  let sources=profile.sources;
  if(t.id==='csharp') {
    requireThat(record.schema===2 && [record.integration?.source_sha,record.cli?.source_sha].every(s=>/^[a-f0-9]{40}$/.test(s)) &&
      [record.integration?.preflight_run_id,record.cli?.preflight_run_id].every(s=>/^[1-9][0-9]*$/.test(String(s))),'Missing/invalid C# provenance metadata');
  }
  else {
    requireThat(record.schema===`wss-${t.id}-projection/v1` && Array.isArray(record.files) && hash(record.projection_sha256),'Unsupported/incomplete committed projection metadata');
    const allowed=t.id==='matlab'?[profile.sources]:[profile.sources,t.id==='unity'?unityLegacy:legacy];
    sources=allowed.find(paths=>inventory(record.files.map(f=>f.source),paths));
    requireThat(sources,'Committed projection differs from exact reviewed inventories');
    if(t.id==='python') requireThat((record.universal_contract || 'legacy-v1')===(sources.length===UNIVERSAL_FILES.length?'macos-ble-v1':'legacy-v1'),'Python projection contract/inventory mismatch');
    if(t.id==='matlab') requireThat(record.bootstrap_reference===false,'Historical MATLAB bootstrap is not current candidate evidence');
  }
  const actual=response.tree.filter(e=>e.type!=='tree' && e.path.startsWith(`${profile.root}/`)).map(e=>e.path.slice(profile.root.length+1));
  let payload=actual;
  if(t.id==='csharp') {
    payload=actual.filter(p=>p!=='wss-candidate.json');
    sources=[UNIVERSAL_FILES,legacy].find(paths=>inventory(payload,paths));
    requireThat(sources,'C# installed inventory differs from reviewed layouts');
  } else if(t.id==='matlab') payload=actual.filter(p=>p!=='payload-source.json');
  else if(t.id==='unity') payload=actual.filter(p=>/\.(dll|so|dylib)$/i.test(p));
  requireThat(inventory(payload,sources.map(p=>projectedPath(t.id,p))),'Missing/extra/case-colliding installed projection files');
  const hashes={};
  for(const source of sources) {
    const path=projectedPath(t.id,source),bytes=read(`${profile.root}/${path}`);
    hashes[source]=bytesDigest(bytes);
    if(t.id!=='csharp') {
      const item=record.files.find(f=>f.source===source);
      requireThat(hash(item.sha256) && item.sha256===hashes[source],`Committed projection hash differs from installed bytes: ${source}`);
      if(t.id==='unity') {
        const metaHash=bytesDigest(Buffer.from(read(`${profile.root}/${path}.meta`).toString('utf8').replace(/\r\n/g,'\n')));
        requireThat(hash(item.meta_sha256) && item.meta_sha256===metaHash,`Missing/changed Unity importer metadata: ${source}`);
      }
    }
  }
  if(t.id!=='csharp') {
    // Compare actual per-file bytes; do not reimplement the receivers' differing
    // culture/ordinal aggregate-digest and importer-policy validators here.
    // Those remain mandatory in normal preflight/promotion, including on change.
    if(t.id==='matlab') requireThat(same(JSON.parse(read(`${profile.root}/payload-source.json`).toString('utf8').replace(/^\uFEFF/,'')),record),'MATLAB duplicate provenance differs');
  }
  return {hashes,candidate:record.candidate,provenance_sha256:bytesDigest(recordBytes)};
}

// Mirror the C# receiver's ordering guard before any library is dispatched.
// This supplies an earlier diagnostic; the receiver still rechecks its own live
// head, approval, producer and lineage immediately before its publication writes.
export function assertCSharpCandidateLineage(g,incoming,installed) {
  candidate(incoming);candidate(installed);
  if(incoming.run_id===installed.run_id) {
    requireThat(same(incoming,installed),'Installed producer run has a conflicting candidate identity');
    return 'same-installed-candidate';
  }
  if(incoming.commit===installed.commit) {
    requireThat(BigInt(incoming.run_id)>BigInt(installed.run_id),'Candidate would roll back the installed producer run');
    return 'same-source-later-run';
  }
  const comparison=g.get(`repos/${CORE}/compare/${installed.commit}...${incoming.commit}`);
  requireThat(comparison.status==='ahead' && comparison.base_commit?.sha===installed.commit &&
    comparison.merge_base_commit?.sha===installed.commit,'Candidate would roll back or diverge from installed Core source; inspect without replacing provenance');
  return 'descendant-core-source';
}

// Diagnostic only: even verified equality never becomes a v1 acceptance receipt.
export function classifyProjections(g,p,archiveHashes) {
  validatePlan(p);
  const cache=new Map(),results=[];
  let archiveError;
  try {
    requireThat(archiveHashes?.sha256===p.candidate.sha256 && inventory(Object.keys(archiveHashes.files || {}),UNIVERSAL_FILES) &&
      Object.values(archiveHashes.files).every(hash),'Verified candidate ZIP inventory/checksum evidence missing');
    requireThat(g.head(CORE,p.policy.channel)===p.candidate.commit,'Core moved before classification');
  } catch(error) {archiveError=error.message;}
  for(const t of p.policy.libraries) {
    const head=p.receivers[t.id]?.heads?.[p.policy.channel];
    const row={target:t.id,repository:t.repository,head_sha:head,status:'blocked',acceptance:false};
    try {
      assertWorkflows(g,p,'stage',{ids:[t.id]});
      requireThat(!archiveError,archiveError);
      requireThat(/^[a-f0-9]{40}$/.test(head) && g.head(t.repository,p.policy.channel)===head &&
        g.head(t.repository,'main')===p.receivers[t.id].heads.main,'Receiver moved from frozen heads');
      const old=installed(g,t,head,cache),profile=PROFILES[t.id];
      row.installed_candidate=old.candidate;
      if(t.id==='csharp') row.lineage=assertCSharpCandidateLineage(g,p.candidate,old.candidate);
      const changed=profile.sources.filter(source=>old.hashes[source]!==archiveHashes.files[source]);
      row.status=changed.length?'payload-change':'equivalent-but-unsupported';
      row.changed_sources=changed;row.preserved_provenance_sha256=old.provenance_sha256;
      row.reason=changed.length?'New candidate changes the installed projection; normal reviewed promotion is required.':
        'Installed bytes match; v1 has no equivalence acceptance. Preserve provenance and inspect recovery.';
    } catch(error) {row.reason=error.message;}
    results.push(row);
  }
  // Classifying one target cannot hide another target moving during these reads.
  for(const row of results) {
    try {requireThat(g.head(CORE,p.policy.channel)===p.candidate.commit && g.head(row.repository,p.policy.channel)===row.head_sha &&
      g.head(row.repository,'main')===p.receivers[row.target].heads.main,'Core/receiver moved during classification');}
    catch(error) {row.status='blocked';row.reason=error.message;}
  }
  return {schema:'wss-projection-classification/v1',candidate:p.candidate,plan_id:p.plan_id,
    downstream_writes_allowed:results.length===4 && results.every(r=>r.status==='payload-change'),results};
}

function main() {
  const [planPath,archivePath,outputPath]=process.argv.slice(2);
  requireThat(planPath && archivePath && outputPath,'Expected frozen plan, candidate ZIP and report destination');
  const p=validatePlan(JSON.parse(readFileSync(planPath)));
  requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF===channelContext(p.policy.channel).ref && process.env.GITHUB_EVENT_NAME==='push' &&
    p.candidate.run_id===process.env.GITHUB_RUN_ID && p.candidate.commit===process.env.GITHUB_SHA,'Classification only for this canonical dev producer');
  let hashes;
  try {
    hashes=JSON.parse(execFileSync('pwsh',['-NoProfile','-File',fileURLToPath(new URL('./read-universal-hashes.ps1',import.meta.url)),
      '-Archive',archivePath,'-ExpectedSha256',p.candidate.sha256],{encoding:'utf8',maxBuffer:1024*1024}));
  } catch { /* Emit four blocked results for absent/unverifiable candidate bytes. */ }
  const report=classifyProjections(new GitHub(),p,hashes);
  mkdirSync(dirname(outputPath),{recursive:true});writeFileSync(outputPath,canonical(report)+'\n');
  const rows=report.results.map(r=>`| ${r.target} | ${r.status} | ${String(r.reason).replace(/[|\r\n]/g,' ')} |`).join('\n');
  if(process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY,`\n### Pre-write projection classification\n| Target | Outcome | Detail |\n|---|---|---|\n${rows}\n\nDiagnostic only; no acceptance receipt or provenance update.\n`);
  requireThat(report.downstream_writes_allowed,'Downstream handoff blocked by pre-write projection classification; inspect the retained report. No no-op acceptance is authorized.');
}
if(isEntrypoint(import.meta.url))
  try {main();} catch(error) {console.error(error.message);process.exitCode=1;}
