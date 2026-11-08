import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const read=path=>readFileSync(new URL('../../'+path,import.meta.url),'utf8');
const flag='-p:DisablePackageAssetsCache=true';
function requireUncached(text){
 const commands=[...text.matchAll(/\bdotnet (?:restore|build|publish|test|run)\b[^\r\n]*/g)];
 assert.ok(commands.length,'Expected real SDK project invocations');
 for(const [command] of commands)assert.ok(command.includes(flag),`Cached SDK invocation: ${command}`);
}
test('actual Core project producers and helper CI enforce uncached SDK resolution',()=>{
 for(const path of ['.github/workflows/artifacts.yml','.github/workflows/shared-ci.yml']){
  const text=read(path);requireUncached(text);
  assert.throws(()=>requireUncached(text.replace(flag,'')),/Cached SDK invocation/);
 }
 const program=read('tools/ArtifactBuild/Program.cs');
 const runBuild=program.slice(program.indexOf('private static void RunBuild('),program.indexOf('private static Manifest ReadManifest('));
 assert.ok(runBuild.includes(`startInfo.ArgumentList.Add("${flag}");`));
 assert.ok(runBuild.indexOf(`startInfo.ArgumentList.Add("${flag}");`)>runBuild.indexOf('foreach (var argument in additionalArguments)'));
 assert.ok(runBuild.indexOf(`startInfo.ArgumentList.Add("${flag}");`)<runBuild.indexOf('Process.Start(startInfo)'));
 const native=read('tools/WindowsBleBridge/build.ps1');
 const nested=[...native.matchAll(/Invoke-Checked 'dotnet' @\('build'[^\r\n]+/g)];
 assert.equal(nested.length,2);for(const [line] of nested)assert.ok(line.includes(flag));
});
test('real default cache discrepancy and paired corrected SDK invocation stay in mandatory helper suite',()=>{
 const layout=read('tools/ci/shared/restored-assets/RestoreLayout.Tests.ps1');
 assert.ok(layout.includes("'CachedPackageResolution.Tests.ps1'"));
 const testSource=read('tools/ci/shared/restored-assets/CachedPackageResolution.Tests.ps1');
 assert.ok(testSource.includes('$first=Resolve-Sdk'));
 assert.ok(testSource.includes('$cached=Resolve-Sdk'));
 assert.ok(testSource.includes('$corrected=Resolve-Sdk -Uncached'));
 assert.ok(testSource.includes('RestoreProof=$fresh'));
 assert.ok(testSource.indexOf('$fresh=[Wss.BuildTools.RestoredPackageAssets]::CaptureProjectAsset')<testSource.indexOf('$corrected=Resolve-Sdk -Uncached'));
});
test('prebuilt path keeps current approved projection distinct from historical build provenance',()=>{
 const universal=read('tools/ArtifactBuild/UniversalBuild.cs'),program=read('tools/ArtifactBuild/Program.cs');
 assert.match(universal,/case "--no-build": managedAlreadyBuilt = true/);
 assert.match(universal,/VerifyRestoredClosure\(manifest, common\);\s*Stage\(manifest, common, destination\);\s*Validate\(manifest, destination\);/);
 assert.match(program,/var overrides = manifest.PackageOverrides.ToDictionary/);
 assert.match(program,/identity != reference/);
 assert.match(program,/SHA256.HashData\(sourceStream\).SequenceEqual\(SHA256.HashData\(destinationStream\)\)/);
 assert.ok(read('.github/workflows/shared-ci.yml').includes('PrebuiltPackageOverride.Tests.ps1'));
});
test('compiled runner is owned once and scoped only to the existing CSharp receivers',()=>{
 const registry=JSON.parse(read('tools/ci/sync-tooling.json'));
 assert.deepEqual(registry.files['.github/scripts/wss-ci/compiled-tests/CompiledCSharpTests.ps1'],{
  origin:'core',path:'tools/ci/shared/compiled-tests/CompiledCSharpTests.ps1',repositories:['CSharp_WSS_Integration_Lib','CLI_CSharp_WSS_Application']
 });
 const runner=read('tools/ci/shared/compiled-tests/CompiledCSharpTests.ps1');
 assert.ok(runner.includes("'../../TrustedBaseline.ps1'"));
 assert.ok(runner.includes('New-TrustedBaselineSnapshot'));
 assert.ok(runner.includes('Assert-TrustedBaselineSnapshot'));
 assert.ok(runner.includes("@('exec',$Proof.vstest,$Proof.assembly"));
 assert.ok(!/dotnet (?:test|build|msbuild)/.test(runner));
});
