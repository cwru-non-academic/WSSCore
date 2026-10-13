param(
    [Parameter(Mandatory=$true)][string]$ReleaseAssets,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $ReleaseAssets -PathType Container)) { throw 'Release asset directory is missing.' }
if (Test-Path -LiteralPath $Destination) { throw 'Extraction destination must be new.' }
if (!(Test-Path -LiteralPath (Split-Path -Parent $Destination))) { throw 'Extraction parent directory is missing.' }
$lines = @(Get-Content -LiteralPath (Join-Path $ReleaseAssets 'SHA256SUMS.txt'))
if ($lines.Count -ne 1 -or $lines[0] -notmatch '^([0-9a-f]{64})  (WSS-Universal-[A-Za-z0-9._-]+\.zip)$') { throw 'Invalid universal checksum metadata.' }
$digest = $Matches[1]
$archiveName = $Matches[2]
$files = @(Get-ChildItem -LiteralPath $ReleaseAssets -File)
if ($files.Count -ne 2 -or @($files | Where-Object { $_.Name -ne $archiveName -and $_.Name -ne 'SHA256SUMS.txt' }).Count -ne 0) { throw 'Expected one universal ZIP and SHA256SUMS.txt only.' }
$zip = Join-Path $ReleaseAssets $archiveName
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw 'Universal checksum mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath $Destination
if (!(Test-Path -LiteralPath (Join-Path $Destination 'WSS-Universal/WSS.Transport.BLE.dll'))) { throw 'Universal archive root is missing.' }
'Universal checksum and extraction: PASS'
