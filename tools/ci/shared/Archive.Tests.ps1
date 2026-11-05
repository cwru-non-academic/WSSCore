$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Assert-UniversalArchive.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) ('wss-archive-contract-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
function New-Fixture([string]$Name, [object[]]$Entries) {
    $path = Join-Path $root ($Name + '.zip')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $Entries) {
            $entry = $zip.CreateEntry($item.path)
            if ($item.ContainsKey('attributes')) { $entry.ExternalAttributes = $item.attributes }
            $stream = $entry.Open()
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($item.content); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $path
}
function Assert-Rejected([string]$Name, [object[]]$Entries, [string]$Message) {
    $path = New-Fixture $Name $Entries
    $checksum = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    try { $null = Get-WssUniversalArchiveHashes $path $checksum @('file.dll'); throw 'Fixture was accepted.' }
    catch { if ($_.Exception.Message -notmatch $Message) { throw } }
}
try {
    $path = New-Fixture 'valid' @(@{path='WSS-Universal/file.dll';content='payload'})
    $checksum = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $actual = Get-WssUniversalArchiveHashes $path $checksum @('file.dll')
    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('payload'))).ToLowerInvariant()
    if ($actual.sha256 -cne $checksum -or $actual.files.Count -ne 1 -or $actual.files['file.dll'] -cne $expected) { throw 'Valid archive hash mismatch.' }
    Assert-Rejected 'case-collision' @(@{path='WSS-Universal/file.dll';content='a'},@{path='WSS-Universal/FILE.dll';content='b'}) 'colliding'
    Assert-Rejected 'traversal' @(@{path='WSS-Universal/../file.dll';content='a'}) 'Unexpected'
    Assert-Rejected 'symlink' @(@{path='WSS-Universal/file.dll';content='a';attributes=[int](-1610612736)}) 'regular files'
    Assert-Rejected 'empty' @(@{path='WSS-Universal/file.dll';content=''}) 'Unexpected'
    Assert-Rejected 'unexpected-directory' @(@{path='WSS-Universal/';content=''}) 'Unexpected ZIP directory'
    Assert-Rejected 'missing' @() 'incomplete'
    Write-Output 'Shared archive contracts passed.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
