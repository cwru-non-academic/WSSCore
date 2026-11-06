param([Parameter(Mandatory)][string]$EventPath,[Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
# Compatibility entrypoint: policy and preparation live in the same Node engine.
if ($EventPath -cne $env:GITHUB_EVENT_PATH) { throw 'Event path differs from the runner event.' }
& node (Join-Path $PSScriptRoot 'dev-propagation.mjs') initial unity $WorkRoot
if ($LASTEXITCODE -ne 0) { throw 'Canonical initial handoff preparation failed.' }
