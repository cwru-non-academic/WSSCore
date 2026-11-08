[CmdletBinding()]
param([Parameter(Mandatory)][string]$HelperPath)
$ErrorActionPreference='Stop'
Add-Type -Path $HelperPath
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('wss-assets-restore-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory $scratch
function Put([string]$Path,[string]$Text){$null=New-Item -ItemType Directory (Split-Path -Parent $Path) -Force;[IO.File]::WriteAllText($Path,$Text)}
function Xml([string]$Value){[Security.SecurityElement]::Escape($Value)}
try {
 Put (Join-Path $scratch 'global.json') '{"sdk":{"version":"9.0.316","rollForward":"latestPatch"}}'
 Push-Location $scratch
 $packageSource=Join-Path $scratch 'local feed';$package=Join-Path $scratch 'package';$cache=Join-Path $scratch 'configured cache with spaces';$second=Join-Path $scratch 'empty primary cache'
 $null=New-Item -ItemType Directory $packageSource
 Put (Join-Path $package 'Wss.RestoredAssets.Fixture.nuspec') '<package><metadata><id>Wss.RestoredAssets.Fixture</id><version>8.0.0</version><authors>Fixture</authors><description>Restore metadata fixture, never executable.</description></metadata></package>'
 Put (Join-Path $package 'lib/net8.0/Fixture.dll') 'not an executable, never loaded'
 [IO.Compression.ZipFile]::CreateFromDirectory($package,(Join-Path $packageSource 'Wss.RestoredAssets.Fixture.8.0.0.nupkg'))
 foreach($mode in @('configured','fallback')) {
  $project=Join-Path $scratch "$mode/Fixture.csproj"
  Put $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><PackageReference Include="Wss.RestoredAssets.Fixture" Version="8.0.0" /></ItemGroup></Project>'
  $config=Join-Path $scratch "$mode/NuGet.Config";$global=if($mode -ceq 'configured'){$cache}else{$second}
  $fallback=if($mode -ceq 'fallback'){"<fallbackPackageFolders><clear/><add key=`"fixture`" value=`"$(Xml $cache)`" /></fallbackPackageFolders>"}else{''}
  Put $config "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$(Xml $packageSource)`" /></packageSources><config><add key=`"globalPackagesFolder`" value=`"$(Xml $global)`" /></config>$fallback</configuration>"
  $build=Join-Path $scratch "$mode/artifacts with spaces"
  & dotnet restore $project --configfile $config --artifacts-path $build -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false --nologo
  if($LASTEXITCODE -ne 0){throw 'Safe restore-only fixture failed'}
  $assets=Join-Path $build 'obj/Fixture/project.assets.json'
  $resolved=[Wss.BuildTools.RestoredPackageAssets]::ResolveProjectAsset($assets,$project,'Wss.RestoredAssets.Fixture/8.0.0','lib/net8.0/Fixture.dll','net9.0')
  if($resolved -cne (Join-Path $cache 'wss.restoredassets.fixture/8.0.0/lib/net8.0/Fixture.dll')){throw "NuGet.Config $mode selected unexpected root: $resolved"}
 }
 'NuGet.Config override, fallback folders and --artifacts-path passed against actual dotnet restore. No package assembly executed.'
} finally {
 Pop-Location
 $resolved=[IO.Path]::GetFullPath($scratch);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if(!$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^wss-assets-restore-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup path'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}

& pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'PackageRootSelection.Tests.ps1') -HelperPath $HelperPath
if($LASTEXITCODE -ne 0){throw 'SDK package-root selection comparison failed'}

& pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'CachedPackageResolution.Tests.ps1') -HelperPath $HelperPath
if($LASTEXITCODE -ne 0){throw 'Default SDK cache and corrected invocation comparison failed'}
