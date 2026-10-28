param([Parameter(Mandatory)][string]$EventPath,[Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Read-GhJson([string]$Path) {
    $response=& gh api $Path
    if ($LASTEXITCODE -ne 0) { throw "GitHub read failed: $Path" }
    $response | ConvertFrom-Json
}
$repository='cwru-non-academic/WSSCore'
$wrapper='cwru-non-academic/Python_WSS_Integration_Lib'
$cli='cwru-non-academic/CLI_Python_WSS_Application'
$ros='cwru-non-academic/ROS_Python_WSS_Application'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_EVENT_NAME -cne 'workflow_run') { throw 'Only the trusted Core completion workflow prepares automatic handoffs.' }
$event=Get-Content -LiteralPath $EventPath -Raw | ConvertFrom-Json
$id=[string]$event.workflow_run.id
if ($id -cnotmatch '^[1-9][0-9]*$') { throw 'Invalid producer run ID.' }
$run=Read-GhJson "repos/$repository/actions/runs/$id"
$workflow=Read-GhJson "repos/$repository/actions/workflows/artifacts.yml"
if ($run.repository.full_name -cne $repository -or $run.head_repository.full_name -cne $repository -or
    $run.path -cne '.github/workflows/artifacts.yml' -or $run.workflow_id -ne $workflow.id -or
    $run.event -cne 'push' -or $run.head_branch -cne 'dev' -or $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or
    $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or $event.workflow_run.head_sha -cne $run.head_sha) { throw 'Not the exact successful canonical Core dev push producer.' }
$comparison=Read-GhJson "repos/$repository/compare/$($run.head_sha)...dev"
if ($comparison.status -cnotin @('ahead','identical')) { throw 'Producer commit is not contained in Core dev.' }
$pages=& gh api --paginate --slurp "repos/$repository/actions/runs/$id/jobs?per_page=100&filter=latest"
if ($LASTEXITCODE -ne 0) { throw 'Producer jobs could not be read.' }
$jobs=@(($pages | ConvertFrom-Json) | ForEach-Object { $_.jobs })
foreach ($name in @('Build and validate artifacts','Core conformance','Serial consumer (Windows)','Serial consumer (macOS)','Serial consumer (Linux)','BLE consumer (Windows)','BLE consumer (Linux)','Build macOS universal BLE bridge','BLE consumer (macOS arm64)','BLE consumer (macOS x86_64)','Core consumer (Windows)','Core consumer (macOS)','Core consumer (Linux)','Start routine downstream validation')) {
    $matchingJobs=@($jobs | Where-Object { $_.name -ceq $name })
    if ($matchingJobs.Count -ne 1 -or $matchingJobs[0].conclusion -cne 'success') { throw "Required producer job missing/skipped/failed: $name" }
}
$artifactPages=& gh api --paginate --slurp "repos/$repository/actions/runs/$id/artifacts?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Producer artifacts could not be read.' }
$artifacts=@(($artifactPages | ConvertFrom-Json) | ForEach-Object { $_.artifacts } | Where-Object { $_.name -ceq 'universal-release-assets' })
if ($artifacts.Count -ne 1 -or $artifacts[0].expired) { throw 'Expected one unexpired authoritative universal artifact.' }
$artifact=Read-GhJson "repos/$repository/actions/artifacts/$($artifacts[0].id)"
if ([string]$artifact.workflow_run.id -cne $id -or $artifact.workflow_run.head_sha -cne $run.head_sha) { throw 'Artifact belongs to another producer.' }
if (Test-Path -LiteralPath $WorkRoot) { throw 'Handoff directory must be new.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$download=Join-Path $WorkRoot 'download'
& gh run download $id --repo $repository --name universal-release-assets --dir $download
if ($LASTEXITCODE -ne 0) { throw 'Exact producer artifact download failed.' }
& "$PSScriptRoot/../ArtifactBuild/extract-universal.ps1" -ReleaseAssets $download -Destination (Join-Path $WorkRoot 'verified')
if (!$?) { throw 'Producer archive verification failed.' }
$archive=Join-Path $download 'WSS-Universal-ci.zip'
if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Core dev must supply WSS-Universal-ci.zip.' }
$candidate=[ordered]@{repository=$repository;commit=$run.head_sha;run_id=$id;artifact_id=[string]$artifact.id;archive='WSS-Universal-ci.zip';sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()}
$candidate | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $WorkRoot 'candidate.json')
# Check default-branch availability before minting any downstream write token.
foreach ($destination in @(@{repo=$wrapper;paths=@('.github/workflows/promote-wss.yml','.github/workflows/wss-preflight.yml','.github/workflows/dispatch-cli-promotion.yml')},@{repo=$cli;paths=@('.github/workflows/promote-integration.yml','.github/workflows/wss-preflight.yml')},@{repo=$ros;paths=@('.github/workflows/promote-integration.yml','.github/workflows/wss-preflight.yml','.github/workflows/ros-ci.yml')})) {
    $metadata=Read-GhJson "repos/$($destination.repo)"
    $tree=Read-GhJson "repos/$($destination.repo)/git/trees/$($metadata.default_branch)?recursive=1"
    if ($tree.truncated) { throw 'Destination tree response is truncated.' }
    foreach ($path in $destination.paths) { if ($path -cnotin $tree.tree.path) { throw "Publish $path to $($destination.repo) default branch $($metadata.default_branch) through a human-reviewed merge before enabling handoff." } }
    $dev=Read-GhJson "repos/$($destination.repo)/branches/dev"
    if ($dev.commit.sha -cnotmatch '^[0-9a-f]{40}$') { throw "A published dev baseline is required in $($destination.repo) before handoff." }
}
$title="Promote universal WSS / wss-$id-$($artifact.id)"
$runPages=& gh api --paginate --slurp "repos/$wrapper/actions/workflows/promote-wss.yml/runs?event=workflow_dispatch&per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Python promotion workflow is not registered/accessible on its default branch.' }
$existing=@(($runPages | ConvertFrom-Json) | ForEach-Object { $_.workflow_runs } | Where-Object { $_.display_title -ceq $title })
if ($existing.Count -gt 1) { throw 'Ambiguous correlated Python promotion runs; inspect before any dispatch.' }
if ($existing.Count -eq 1) {
    if ($existing[0].status -ceq 'completed' -and $existing[0].conclusion -cne 'success') { throw "Prior correlated Python promotion failed: $($existing[0].html_url). No blind redispatch." }
    'dispatch_needed=false' >> $env:GITHUB_OUTPUT
    "Existing correlated Python promotion: $($existing[0].html_url). No redispatch." >> $env:GITHUB_STEP_SUMMARY
    exit 0
}
@{ref='dev';inputs=@{candidate_json=($candidate | ConvertTo-Json -Compress)}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $WorkRoot 'dispatch.json')
'dispatch_needed=true' >> $env:GITHUB_OUTPUT
"Verified Core producer $id / artifact $($artifact.id) / $($run.head_sha); Python handoff is ready." >> $env:GITHUB_STEP_SUMMARY
