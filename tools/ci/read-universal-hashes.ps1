param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedSha256
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/shared/Assert-UniversalArchive.ps1"
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ArtifactBuild/manifests/universal.json') -Raw | ConvertFrom-Json
Get-WssUniversalArchiveHashes -Archive $Archive -ExpectedSha256 $ExpectedSha256 -ExpectedFiles $manifest.files | ConvertTo-Json -Depth 3 -Compress
