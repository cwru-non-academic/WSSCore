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

test('actual decompressed bytes remain bounded even when ZIP metadata understates size',()=>{
 const bytes=archive([{name:'snapshot.json',value:'x'.repeat(1048577)}]);
 const central=bytes.indexOf(Buffer.from([0x50,0x4b,0x01,0x02]));
 const local=bytes.indexOf(Buffer.from([0x50,0x4b,0x03,0x04]));
 assert.ok(central>=0&&local>=0);bytes.writeUInt32LE(10,central+24);bytes.writeUInt32LE(10,local+22);
 let decoded;
 try {decoded=decode(bytes)['snapshot.json'];}
 catch(error){assert.match(error.message,/Expanded JSON|invalid|corrupt|length/i);return;}
 assert.ok(decoded.length<=1048576);
 // .NET clamps to the stated length on this runtime; truncated data still cannot
 // become a valid snapshot document. The reader also independently bounds reads.
 assert.throws(()=>JSON.parse(decoded));
});
import {extractVerifiedProofArchive,downloadVerifiedArtifactBytes} from './verified-json-artifact.mjs';
import {existsSync} from 'node:fs';
test('rich proof transport fetches exact immutable ID and verifies raw ZIP digest',()=>{
 const bytes=archive([{name:'evidence.json',value:'{}'}]),calls=[];
 const g={artifactBytes:(repo,id,limit)=>{calls.push([repo,id,limit]);return bytes;}};
 const a={id:'41',digest:'sha256:'+bytesDigest(bytes)};
 assert.deepEqual(downloadVerifiedArtifactBytes(g,'owner/repo',a),bytes);assert.equal(calls[0][1],'41');
 assert.throws(()=>downloadVerifiedArtifactBytes(g,'owner/repo',{...a,digest:'sha256:'+'0'.repeat(64)}),/digest/);
});
test('bounded native archive preserves exact nested Unicode proof bytes without executing them',()=>{
 const root=mkdtempSync(join(tmpdir(),'native extraction Ω ')),destination=join(root,'evidence');
 try{extractVerifiedProofArchive(archive([{name:'multi/evidence.json',value:'{"proof":"λ"}'},{name:'logs/test.log',value:'exact\r\nbytes\n'}]),destination);assert.equal(readFileSync(join(destination,'multi/evidence.json'),'utf8'),'{"proof":"λ"}');assert.equal(readFileSync(join(destination,'logs/test.log'),'utf8'),'exact\r\nbytes\n');assert.throws(()=>extractVerifiedProofArchive(archive([{name:'x',value:'x'}]),destination));}finally{rmSync(root,{recursive:true,force:true});}
});
for(const [name,entries] of Object.entries({traversal:[{name:'../outside.json',value:'{}'}],absolute:[{name:'/outside.json',value:'{}'}],trailingDot:[{name:'dir./proof.json',value:'{}'}],reserved:[{name:'NUL.json',value:'{}'}],backslash:[{name:'dir\\outside.json',value:'{}'}],alternateStream:[{name:'safe:stream',value:'{}'}],duplicateCase:[{name:'proof.json',value:'{}'},{name:'PROOF.json',value:'{}'}],dotSegment:[{name:'dir/./proof.json',value:'{}'}]}))test(`bounded native archive rejects ${name} before creating destination`,()=>{const root=mkdtempSync(join(tmpdir(),'native-bad-')),destination=join(root,'evidence');try{assert.throws(()=>extractVerifiedProofArchive(archive(entries),destination));assert.equal(existsSync(destination),false);}finally{rmSync(root,{recursive:true,force:true});}});
