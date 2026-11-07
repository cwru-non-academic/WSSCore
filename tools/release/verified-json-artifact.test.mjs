import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {bytesDigest} from './verify.mjs';
import {readVerifiedJsonArchive} from './verified-json-artifact.mjs';
function archive(entries) {
 const dir=mkdtempSync(join(tmpdir(),'wss-zip-test-'));
 try {
  writeFileSync(join(dir,'entries.json'),JSON.stringify(entries));
  writeFileSync(join(dir,'create.ps1'),`param([string]$Root)\n$ErrorActionPreference='Stop'\n$entries=Get-Content -LiteralPath (Join-Path $Root 'entries.json') -Raw|ConvertFrom-Json\n$zip=[IO.Compression.ZipFile]::Open((Join-Path $Root 'fixture.zip'),[IO.Compression.ZipArchiveMode]::Create)\ntry {foreach($e in $entries){$entry=$zip.CreateEntry($e.name);$stream=$entry.Open();$writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false));try{$writer.Write($e.value)}finally{$writer.Dispose();$stream.Dispose()}}}finally{$zip.Dispose()}\n`);
  execFileSync('pwsh',['-NoProfile','-NonInteractive','-File',join(dir,'create.ps1'),dir]);
  return readFileSync(join(dir,'fixture.zip'));
 }finally{rmSync(dir,{recursive:true,force:true});}
}
const decode=bytes=>readVerifiedJsonArchive(bytes,'sha256:'+bytesDigest(bytes),['snapshot.json']);
test('real ZIP decoder accepts only exact JSON bytes and authenticated archive digest',()=>{
 const bytes=archive([{name:'snapshot.json',value:'{"ok":true}'}]);
 assert.equal(decode(bytes)['snapshot.json'].toString(),' {"ok":true}'.trim());
 assert.throws(()=>readVerifiedJsonArchive(bytes,'sha256:'+'0'.repeat(64),['snapshot.json']),/digest/);
});
for(const [name,entries] of Object.entries({
 duplicate:[{name:'snapshot.json',value:'{}'},{name:'snapshot.json',value:'{}'}],
 extra:[{name:'snapshot.json',value:'{}'},{name:'extra.json',value:'{}'}],
 traversal:[{name:'../snapshot.json',value:'{}'}],
 oversized:[{name:'snapshot.json',value:'x'.repeat(1048577)}],
 missing:[]
}))test(`real ZIP decoder rejects ${name}`,()=>assert.throws(()=>decode(archive(entries))));
test('real ZIP decoder rejects corrupt archive even if transport digest matches',()=>assert.throws(()=>decode(Buffer.from('not a zip'))));
