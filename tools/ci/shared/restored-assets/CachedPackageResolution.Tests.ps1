[CmdletBinding()]
param([Parameter(Mandatory)][string]$HelperPath)
$ErrorActionPreference='Stop'
Add-Type -Path $HelperPath
. "$PSScriptRoot/Universal-WssFiles.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('wss-default-cache-'+[guid]::NewGuid().ToString('N'))
function Put([string]$Path,[string]$Text){$null=New-Item -ItemType Directory (Split-Path -Parent $Path) -Force;[IO.File]::WriteAllText($Path,$Text)}
function Xml([string]$Value){[Security.SecurityElement]::Escape($Value)}
function Require([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Resolve-Sdk([switch]$Uncached){
 $arguments=@('msbuild',$project,'-t:ResolvePackageAssets','-getItem:RuntimeCopyLocalItems','-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','-p:NuGetAudit=false','-nologo')
 if($Uncached){$arguments+='-p:DisablePackageAssetsCache=true'}
 $result=& dotnet @arguments
 if($LASTEXITCODE -ne 0){throw 'Safe SDK package resolution failed'}
 $items=@(($result -join "`n"|ConvertFrom-Json).Items.RuntimeCopyLocalItems|Where-Object NuGetPackageId -ceq 'System.IO.Ports')
 Require ($items.Count -eq 1) 'Expected exactly one SDK fixture runtime asset'
 [IO.Path]::GetFullPath($items[0].Identity)
}
try {
 Put (Join-Path $root 'global.json') '{"sdk":{"version":"9.0.316","rollForward":"latestPatch"}}'
 if(!$IsWindows){Push-Location $root;try{$root=(& /bin/pwd -P).Trim()}finally{Pop-Location}}
 $feed=Join-Path $root 'local feed';$package=Join-Path $root 'package';$primary=Join-Path $root 'primary cache';$fallback=Join-Path $root 'fallback cache'
 $project=Join-Path $root 'project/Fixture.csproj';$config=Join-Path $root 'NuGet.Config'
 $null=New-Item -ItemType Directory $feed,$fallback
 Put (Join-Path $package 'System.IO.Ports.nuspec') '<package><metadata><id>System.IO.Ports</id><version>8.0.0</version><authors>Fixture</authors><description>Non executable cache contract fixture</description></metadata></package>'
 Put (Join-Path $package 'lib/net8.0/System.IO.Ports.dll') 'fallback-neutral-never-loaded'
 [IO.Compression.ZipFile]::CreateFromDirectory($package,(Join-Path $feed 'System.IO.Ports.8.0.0.nupkg'))
 Put $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><PackageReference Include="System.IO.Ports" Version="8.0.0" /></ItemGroup></Project>'
 Put $config "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$(Xml $feed)`" /></packageSources><config><add key=`"globalPackagesFolder`" value=`"$(Xml $primary)`" /></config><fallbackPackageFolders><clear/><add key=`"fixture`" value=`"$(Xml $fallback)`" /></fallbackPackageFolders></configuration>"
 Push-Location $root
 try {
  & dotnet restore $project --configfile $config -p:DisablePackageAssetsCache=true -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false --nologo
  if($LASTEXITCODE -ne 0){throw 'Safe local restore failed'}
  $selected=Join-Path $primary 'system.io.ports/8.0.0';$fallbackPackage=Join-Path $fallback 'system.io.ports/8.0.0'
  $null=New-Item -ItemType Directory (Split-Path -Parent $fallbackPackage)
  # Only this newly created fixture package is moved, using one shell and checked absolute paths.
  foreach($target in @($selected,$fallbackPackage)){Require ([IO.Path]::GetFullPath($target).StartsWith($root+[IO.Path]::DirectorySeparatorChar)) 'Unsafe fixture move target'}
  Move-Item -LiteralPath $selected -Destination $fallbackPackage
  $fallbackAsset=Join-Path $fallbackPackage 'lib/net8.0/System.IO.Ports.dll'
  $first=Resolve-Sdk
  Require ($first -ceq $fallbackAsset) 'Initial default SDK resolution must select fallback'
  $assets=Join-Path $root 'project/obj/project.assets.json'
  $before=[Wss.BuildTools.RestoredPackageAssets]::CaptureProjectAsset($assets,$project,'System.IO.Ports/8.0.0','lib/net8.0/System.IO.Ports.dll','net9.0')
  Copy-Item -LiteralPath $fallbackPackage -Destination $selected -Recurse
  $primaryAsset=Join-Path $selected 'lib/net8.0/System.IO.Ports.dll'
  Put $primaryAsset 'primary-neutral-never-loaded'
  $cached=Resolve-Sdk
  Require ($cached -ceq $fallbackAsset) 'Expected real default SDK cache to retain fallback after complete primary appears'
  $fresh=[Wss.BuildTools.RestoredPackageAssets]::CaptureProjectAsset($assets,$project,'System.IO.Ports/8.0.0','lib/net8.0/System.IO.Ports.dll','net9.0')
  Require ($fresh.NeutralAssetPath -ceq $primaryAsset) 'Canonical uncached selection must select complete primary'
  $errorText='';try{[Wss.BuildTools.RestoredPackageAssets]::AssertProjectAssetProof($before,$assets,$project,'System.IO.Ports/8.0.0','lib/net8.0/System.IO.Ports.dll','net9.0')}catch{$errorText=$_.Exception.Message}
  Require ($errorText -like '*resolution identity*') 'Pre-child proof must reject resolution shift even with unchanged original fallback bytes'
  # Capture first, resolve using the production invocation contract, then validate the same proof.
  $corrected=Resolve-Sdk -Uncached
  Require ($corrected -ceq $fresh.NeutralAssetPath) 'Cache-disabled SDK and captured canonical root must agree'
  [Wss.BuildTools.RestoredPackageAssets]::AssertProjectAssetProof($fresh,$assets,$project,'System.IO.Ports/8.0.0','lib/net8.0/System.IO.Ports.dll','net9.0')
  $payload=Join-Path $root 'payload';$output=Join-Path $root 'output'
  foreach($relative in $WssUniversalFiles){Put (Join-Path $payload $relative) "verified-$relative";Put (Join-Path $output $relative) "verified-$relative"}
  $invoke=@{ArtifactRoot=$payload;OutputRoot=$output;ProjectAssetsFile=$assets;ProjectPath=$project;RestoreProof=$fresh}
  Copy-Item -LiteralPath $corrected -Destination (Join-Path $output 'System.IO.Ports.dll')
  & "$PSScriptRoot/Verify-WssOutput.ps1" @invoke | Out-Null
  foreach($foreign in @('fallback-neutral-never-loaded','foreign-unapproved')){
   Put (Join-Path $output 'System.IO.Ports.dll') $foreign
   $errorText='';try{& "$PSScriptRoot/Verify-WssOutput.ps1" @invoke | Out-Null}catch{$errorText=$_.Exception.Message}
   Require ($errorText -like '*net462 is forbidden*') 'Cached fallback/foreign output must not match corrected proof'
  }
  [pscustomobject]@{default_cached_identity=$cached;corrected_uncached_identity=$corrected;canonical_identity=$fresh.NeutralAssetPath;default_cache_discrepancy_reproduced=$true;corrected_invocation_verified=$true;native_or_package_execution=$false}|ConvertTo-Json -Compress
 }finally{Pop-Location}
}finally{
 $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if(!$IsWindows){Push-Location $temp;try{$temp=(& /bin/pwd -P).Trim()}finally{Pop-Location}}
 if(!$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^wss-default-cache-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup path'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

