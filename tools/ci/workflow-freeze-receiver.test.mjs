// Execute the actual retained receiver validator, not a rewritten policy model.
import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,mkdtempSync,mkdirSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {sourceBytes} from './sync-tooling.mjs';
import {pathToFileURL} from 'node:url';
import {resolve,join,dirname} from 'node:path';
import {canonical,digest} from '../release/verify.mjs';

test('existing canonical receiver accepts additive metadata with every exact target unchanged',async()=>{
  const closure=mkdtempSync(join(tmpdir(),'wss-canonical-receiver-'));
  try {
  const sync=JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)));
  for(const [destination,entry] of Object.entries(sync.files)) {
    if(entry.repositories&&!entry.repositories.includes('Python_WSS_Integration_Lib'))continue;
    const output=join(closure,destination);mkdirSync(dirname(output),{recursive:true});writeFileSync(output,sourceBytes(entry));
  }
  const root=resolve(closure,'.github/scripts/wss-ci');
  const {targetPolicy}=await import(pathToFileURL(resolve(root,'receipt.mjs')));
  const {plan}=await import(pathToFileURL(resolve(root,'verify.mjs')));
  const profiles=JSON.parse(readFileSync(resolve(root,'receipt-targets.json')));
  const manifest=JSON.parse(readFileSync(new URL('../release/targets.dev.json',import.meta.url)));
  const sha='1'.repeat(40),candidate={repository:'cwru-non-academic/WSSCore',commit:sha,run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const body={schema:'wss-propagation-plan/v1',channel:'dev',candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:sha,trusted:Object.fromEntries(t.trusted_paths.map(path=>[path,sha]))}])),workflow_snapshots:{schema:'wss-workflow-snapshots/v1',targets:{}}};
  const p={...body,plan_id:digest(body)};
  plan(p);
  for(const t of manifest.targets){assert.equal(canonical(t),canonical(profiles[t.id].target));assert.equal(targetPolicy(p,t.id).id,t.id);}
  const changed=structuredClone(p);changed.manifest.targets[0].trusted_paths.push('.github/workflows');assert.throws(()=>targetPolicy(changed,changed.manifest.targets[0].id),/receiver-owned/);
  } finally {rmSync(closure,{recursive:true,force:true});}
});
