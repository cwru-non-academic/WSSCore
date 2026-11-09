param([Parameter(Mandatory)][string]$HelperPath)
$ErrorActionPreference='Stop'
Add-Type -Path $HelperPath
$root=Join-Path ([IO.Path]::GetTempPath()) ('peer-nuget-semantics-'+[guid]::NewGuid().ToString('N'))
function Put([string]$Path,[string]$Text){$null=New-Item -ItemType Directory (Split-Path -Parent $Path) -Force;[IO.File]::WriteAllText($Path,$Text)}
function Xml([string]$Value){[Security.SecurityElement]::Escape($Value)}
try {
 Put (Join-Path $root 'global.json') '{"sdk":{"version":"9.0.316","rollForward":"latestPatch"}}'
 $feed=Join-Path $root 'feed';$package=Join-Path $root 'package';$primary=Join-Path $root 'primary';$fallback=Join-Path $root 'fallback';$project=Join-Path $root 'project/Fixture.csproj';$config=Join-Path $root 'NuGet.Config'
 $null=New-Item -ItemType Directory $feed
 Put (Join-Path $package 'Peer.Fixture.nuspec') '<package><metadata><id>Peer.Fixture</id><version>1.0.0</version><authors>Peer</authors><description>Non executable metadata fixture</description></metadata></package>'
 Put (Join-Path $package 'lib/net8.0/Fixture.dll') 'fake-never-loaded'
 [IO.Compression.ZipFile]::CreateFromDirectory($package,(Join-Path $feed 'Peer.Fixture.1.0.0.nupkg'))
 Put $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><PackageReference Include="Peer.Fixture" Version="1.0.0" /></ItemGroup></Project>'
 $null=New-Item -ItemType Directory $fallback
 Put $config "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$(Xml $feed)`" /></packageSources><config><add key=`"globalPackagesFolder`" value=`"$(Xml $primary)`" /></config><fallbackPackageFolders><clear/><add key=`"fallback`" value=`"$(Xml $fallback)`" /></fallbackPackageFolders></configuration>"
 Push-Location $root
 try{
  & dotnet restore $project --configfile $config -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false --nologo
  if($LASTEXITCODE -ne 0){throw 'Restore failed'}
  Copy-Item -LiteralPath (Join-Path $primary 'peer.fixture') -Destination $fallback -Recurse
  $asset='peer.fixture/1.0.0/lib/net8.0/Fixture.dll'
  $assets=Join-Path $root 'project/obj/project.assets.json'
  $selected=Join-Path $primary 'peer.fixture/1.0.0'
  $saved=Join-Path $primary 'peer.fixture/saved'
  foreach($target in @($selected,$saved)){
   if(![IO.Path]::GetFullPath($target).StartsWith([IO.Path]::GetFullPath($root)+[IO.Path]::DirectorySeparatorChar)){throw 'Unsafe fixture move path'}
  }
  Move-Item -LiteralPath $selected -Destination $saved
  foreach($mode in @('absent','empty-directory','asset-only','metadata-only','empty-metadata','malformed-metadata','sha512-only','nuspec-only','missing-asset','complete')) {
   if(Test-Path -LiteralPath $selected){
    $full=[IO.Path]::GetFullPath($selected)
    if(!$full.StartsWith([IO.Path]::GetFullPath($root)+[IO.Path]::DirectorySeparatorChar)){throw 'Unsafe fixture target'}
    Remove-Item -LiteralPath $full -Recurse -Force
   }
   if($mode -eq 'empty-directory'){New-Item -ItemType Directory $selected|Out-Null}
   if($mode -eq 'asset-only'){Put (Join-Path $selected 'lib/net8.0/Fixture.dll') 'fake-never-loaded'}
   if($mode -eq 'metadata-only'){Put (Join-Path $selected '.nupkg.metadata') '{}'}
   if($mode -eq 'empty-metadata'){Put (Join-Path $selected '.nupkg.metadata') ''}
   if($mode -eq 'malformed-metadata'){Put (Join-Path $selected '.nupkg.metadata') 'not-json'}
   if($mode -eq 'sha512-only'){Put (Join-Path $selected 'peer.fixture.1.0.0.nupkg.sha512') 'invalid-sha512'}
   if($mode -eq 'nuspec-only'){Put (Join-Path $selected 'peer.fixture.nuspec') 'invalid-nuspec'}
   if($mode -in @('missing-asset','complete')){Copy-Item -LiteralPath $saved -Destination $selected -Recurse}
   if($mode -eq 'missing-asset'){Remove-Item -LiteralPath (Join-Path $selected 'lib/net8.0/Fixture.dll')}
   $sdk=& dotnet msbuild $project -t:ResolvePackageAssets -getItem:RuntimeCopyLocalItems -p:DisablePackageAssetsCache=true -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false -nologo
   if($LASTEXITCODE -ne 0){throw 'SDK metadata resolution failed'}
   $item=($sdk -join "`n"|ConvertFrom-Json).Items.RuntimeCopyLocalItems | Where-Object NuGetPackageId -ceq 'Peer.Fixture'
   $helper='';$errorText=''
   try{$helper=[Wss.BuildTools.RestoredPackageAssets]::ResolveProjectAsset($assets,$project,'Peer.Fixture/1.0.0','lib/net8.0/Fixture.dll','net9.0')}catch{$errorText=$_.Exception.Message}
   if(Test-Path -LiteralPath $item.Identity){
    if($helper -cne $item.Identity -or $errorText){throw "Resolver/SDK mismatch for $mode : $helper / $($item.Identity) / $errorText"}
   }elseif($errorText -notlike '*fallback is forbidden*'){throw "Missing selected SDK asset did not fail closed for $mode : $errorText"}
   [pscustomobject]@{mode=$mode;identity=$item.Identity;exists=(Test-Path -LiteralPath $item.Identity);canonical_matches_sdk=$true} | ConvertTo-Json -Compress
  } }finally{Pop-Location}
}finally{
 $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
 if((Split-Path -Parent $resolved) -cne $temp -or (Split-Path -Leaf $resolved) -cnotmatch '^peer-nuget-semantics-[a-f0-9]{32}$'){throw 'Unsafe cleanup'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

