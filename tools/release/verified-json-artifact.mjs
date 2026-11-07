import {execFileSync} from 'node:child_process';
import {mkdtempSync,writeFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {bytesDigest,requireThat} from './verify.mjs';
// Bound JSON-only archive reader. No extraction, executable files or archive paths.
export function downloadVerifiedJsonArtifact(g,repository,artifact,files) {
  requireThat(/^[A-Za-z0-9_-]+\/[A-Za-z0-9_.-]+$/.test(repository)&&/^[1-9][0-9]*$/.test(String(artifact.id))&&/^sha256:[a-f0-9]{64}$/.test(artifact.digest)&&files.length===1&&/^[a-z-]+\.json$/.test(files[0]),'Invalid verified JSON archive request');
  const bytes=execFileSync('gh',['api',`repos/${repository}/actions/artifacts/${artifact.id}/zip`],{env:g.environment,maxBuffer:4*1024*1024});
  return readVerifiedJsonArchive(bytes,artifact.digest,files);
}
export function readVerifiedJsonArchive(bytes,expectedDigest,files) {
  requireThat(Buffer.isBuffer(bytes)&&bytes.length<=4*1024*1024&&files.length===1&&/^[a-z-]+\.json$/.test(files[0]),'Invalid bounded archive');
  requireThat(`sha256:${bytesDigest(bytes)}`===expectedDigest,'Snapshot ZIP digest mismatch');
  const dir=mkdtempSync(join(tmpdir(),'wss-verified-json-'));
  try {
    const archive=join(dir,'artifact.zip'),script=join(dir,'read.ps1');
    writeFileSync(archive,bytes,{flag:'wx'});
    writeFileSync(script,`param([string]$Archive,[string]$Expected)\n$ErrorActionPreference='Stop'\n$zip=[IO.Compression.ZipFile]::OpenRead($Archive)\ntry {\n if($zip.Entries.Count -ne 1){throw 'Unexpected archive entries'}\n $entry=$zip.Entries[0]\n if($entry.FullName -cne $Expected -or $entry.Length -gt 1048576){throw 'Invalid archive entry'}\n $inputStream=$entry.Open();$outputStream=[IO.MemoryStream]::new()\n try {$buffer=[byte[]]::new(8192);$total=0;while(($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){$total+=$count;if($total -gt 1048576){throw 'Expanded JSON exceeds bound'};$outputStream.Write($buffer,0,$count)};[Convert]::ToBase64String($outputStream.ToArray())} finally {$inputStream.Dispose();$outputStream.Dispose()}\n} finally {$zip.Dispose()}\n`,{flag:'wx'});
    const encoded=execFileSync('pwsh',['-NoProfile','-NonInteractive','-File',script,archive,files[0]],{encoding:'utf8',maxBuffer:2*1024*1024}).trim();
    requireThat(/^[A-Za-z0-9+/]*={0,2}$/.test(encoded),'Invalid JSON archive transport');
    return {[files[0]]:Buffer.from(encoded,'base64')};
  } finally {rmSync(dir,{recursive:true,force:true});}
}
