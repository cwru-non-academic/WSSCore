import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,writeFileSync,rmSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath} from 'node:url';

const regression=fileURLToPath(new URL('./shared/native-receipt/NativeReceiptAdapter.Tests.ps1',import.meta.url));
// These synthetic adapters exercise the regression harness itself. Receiver
// callers separately execute this same test against their actual adapter AST.
function adapter(family,mutation='') {
  const csharp=family==='csharp',roles=csharp?"'csharp-wrapper','csharp-cli'":"'unity-wrapper','unity-gui'";
  return `param(
    [Parameter(Mandatory)][ValidateSet(${roles})][string] $TargetId,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string] $ProducerRunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string] $PlanId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ContextPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $WorkRoot,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputPath
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne (Get-WssCiChannelContext).ref) { throw 'Adapter is restricted to actual dev receipt execution.' }
$ctx=Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json
${mutation}
if($ctx.plan.channel -cne (Get-WssCiChannel) -or $ctx.target.branch -cne (Get-WssCiChannel)){throw 'Receipt adapter context differs from explicit channel.'}
if((Get-WssCiChannel) -ceq 'main' -and ($env:GITHUB_WORKFLOW_SHA -cne $env:GITHUB_SHA -or $env:GITHUB_WORKFLOW_REF -cne "$env:GITHUB_REPOSITORY/.github/workflows/release-receipt.yml@refs/heads/main")){throw 'Main receipt must execute its exact registered receiver source.'}
if ($ctx.target.id -cne $TargetId -or $ctx.head -cne $env:GITHUB_SHA -or $ctx.target.repository -cne $env:GITHUB_REPOSITORY) { throw 'Adapter source/role differs from receipt context.' }
$AcceptedRoot=$ctx.paths.sourceRoot; $ToolsRoot=$ctx.paths.toolsRoot; $ValidatorRoot=$ctx.paths.validatorRoot; $IntegrationRoot=$ctx.paths.integrationRoot
$core='synthetic-boundary-only'
$preflights=[Collections.Generic.List[object]]::new()
${csharp?'Assert-WssApproval -ReceiptSnapshots':'Assert-UnityApproval -ReceiptSnapshots\nAssert-UnityMergedGuiPreflight -ReceiptSnapshots'}
`;
}
function invoke(family,mutation='') {
  const root=mkdtempSync(join(tmpdir(),'wss native regression '));
  try {
    const path=join(root,'synthetic adapter.ps1');writeFileSync(path,adapter(family,mutation));
    const result=spawnSync('pwsh',['-NoProfile','-NonInteractive','-File',regression,'-AdapterPath',path],{encoding:'utf8'});
    if(result.error)throw result.error;
    assert.equal(result.signal,null);
    return result;
  } finally {rmSync(root,{recursive:true,force:true});}
}
function beforeContextGuard(body){return `if($ctx.plan.channel -cne (Get-WssCiChannel)){${body}}`;}
for(const family of ['csharp','unity']) {
  test(`${family} native boundary harness preserves all positive and negative cases`,()=>{
    const result=invoke(family);assert.equal(result.status,0,result.stderr);
    assert.match(result.stdout,/4 role\/channel positives, 48 context\/parameter rejections/);
  });
  for(const command of ['gh','git','node','New-Item'])test(`${family} swallowed ${command} attempt cannot satisfy an expected rejection`,()=>{
    const result=invoke(family,beforeContextGuard(`try{${command}}catch{}`));
    assert.equal(result.status,1);assert.match(result.stderr,new RegExp(`Forbidden boundary operation attempted: ${command}`));
  });
  for(const [name,body] of Object.entries({
    file:"[IO.File]::WriteAllText($OutputPath,'unexpected output')",
    directory:"$null=[IO.Directory]::CreateDirectory($WorkRoot)"
  }))test(`${family} rejected boundary cannot create a designated ${name}`,()=>{
    const result=invoke(family,beforeContextGuard(body));
    assert.equal(result.status,1);assert.match(result.stderr,/Boundary created a designated output/);
  });
  test(`${family} unrelated fixture errors cannot satisfy expected rejection`,()=>{
    const result=invoke(family,beforeContextGuard("throw 'Unexpected fixture failure'"));
    assert.equal(result.status,1);assert.match(result.stderr,/Unexpected fixture failure/);
  });
  test(`${family} rejected boundary cannot emit fixture output`,()=>{
    const result=invoke(family,beforeContextGuard("Write-Output 'unexpected output'"));
    assert.equal(result.status,1);assert.match(result.stderr,/Rejected boundary emitted fixture output/);
  });
  test(`${family} rejected boundary cannot change fixture inputs`,()=>{
    const result=invoke(family,beforeContextGuard("$script:testJson+=' '"));
    assert.equal(result.status,1);assert.match(result.stderr,/Rejected boundary changed fixture inputs/);
  });
}
test('native boundary regression has one canonical owner and exactly four receivers',()=>{
  const manifest=JSON.parse(readFileSync(new URL('./sync-tooling.json',import.meta.url)));
  assert.deepEqual(manifest.files['.github/scripts/tests/NativeReceiptAdapter.Tests.ps1'],{
    origin:'core',path:'tools/ci/shared/native-receipt/NativeReceiptAdapter.Tests.ps1',
    repositories:['CSharp_WSS_Integration_Lib','CLI_CSharp_WSS_Application','Unity_WSS_Integration_Lib','GUI_Unity_WSS_Application']
  });
});
