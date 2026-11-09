[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactBuildAssembly)
$ErrorActionPreference='Stop'
# Invoke the real build-time Stage method only. Fake native bytes are copied, never loaded.
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ArtifactBuildAssembly))
$program=$assembly.GetType('ArtifactBuild.Program',$true)
$stage=$program.GetMethod('Stage',[Reflection.BindingFlags]'NonPublic,Static')
$manifest=[Activator]::CreateInstance($assembly.GetType('ArtifactBuild.Manifest',$true),$true)
$entry=[Activator]::CreateInstance($assembly.GetType('ArtifactBuild.PackageOverride',$true),$true)
$root=Join-Path ([IO.Path]::GetTempPath()) ('wss-prebuilt-projection-'+[guid]::NewGuid().ToString('N'))
function Put([string]$Path,[string]$Text){$null=New-Item -ItemType Directory (Split-Path -Parent $Path) -Force;[IO.File]::WriteAllText($Path,$Text)}
try{
 $primary=Join-Path $root 'primary';$fallback=Join-Path $root 'fallback';$source=Join-Path $root 'prebuilt';$destination=Join-Path $root 'staged'
 $asset='runtimes/linux-x64/native/fixture.so';$package='Wss.Projection.Fixture/1.0.0'
 foreach($folder in @($primary,$fallback)){Put (Join-Path $folder 'wss.projection.fixture/1.0.0/.nupkg.metadata') '{}'}
 $approved=Join-Path $primary "wss.projection.fixture/1.0.0/$asset"
 Put $approved 'current-approved-package-bytes'
 Put (Join-Path $fallback "wss.projection.fixture/1.0.0/$asset") 'stale-fallback-bytes'
 Put (Join-Path $source $asset) 'foreign-prebuilt-output-bytes'
 $assets=@{libraries=@{$package=@{type='package';path='wss.projection.fixture/1.0.0';files=@($asset)}};packageFolders=[ordered]@{$primary=@{};$fallback=@{}}}
 Put (Join-Path $root 'assets.json') ($assets|ConvertTo-Json -Depth 10)
 $manifest.ManifestDirectory=$root;$manifest.Files.Add($asset);$manifest.NativeFiles.Add($asset)
 $entry.AssetsFile='assets.json';$entry.Package=$package;$entry.Asset=$asset
 $manifest.PackageOverrides.Add($asset,$entry)
 $null=$stage.Invoke($null,[object[]]@($manifest.PSObject.BaseObject,[string]$source,[string]$destination))
 if([IO.File]::ReadAllText((Join-Path $destination $asset)) -cne 'current-approved-package-bytes'){throw 'Staging trusted stale/foreign prebuilt override bytes'}
 # Removing only the selected package asset cannot silently switch to fallback or prebuilt bytes.
 Remove-Item -LiteralPath $approved
 $errorText='';try{$null=$stage.Invoke($null,[object[]]@($manifest.PSObject.BaseObject,[string]$source,[string](Join-Path $root 'must-not-stage')))}catch{$errorText=$_.Exception.ToString()}
 if($errorText -notlike '*fallback is forbidden*' -or (Test-Path -LiteralPath (Join-Path $root 'must-not-stage'))){throw 'Missing approved override did not fail before staging'}
 'Prebuilt projection: exact approved package source copied; foreign prebuilt bytes ignored; missing selected source rejected. This does not attest historical build/cache provenance. No WSS or native payload executed.'
}finally{
 $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if(!$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -cnotmatch '^wss-prebuilt-projection-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup path'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
