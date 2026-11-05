$ErrorActionPreference='Stop'
. "$PSScriptRoot/Process.ps1"
$literal=@('path with spaces','$(literal)','a;b','quote"inside','--an-option')
$raw=Invoke-WssCiProcess -Executable node -Arguments (@('-e','process.stdout.write(JSON.stringify(process.argv.slice(1)))','--')+$literal)
$actual=@($raw|ConvertFrom-Json)
if ($actual.Count -ne $literal.Count -or @(Compare-Object $literal $actual -CaseSensitive -SyncWindow 0).Count) {throw 'Native argv changed.'}
$rejected=$false
try {Invoke-WssCiProcess -Executable node -Arguments @('-e','process.exit(17)') -FailureMessage 'Expected fixture failure'} catch {if($_.Exception.Message -cne 'Expected fixture failure (exit 17).'){throw};$rejected=$true}
if(!$rejected){throw 'Native nonzero exit was accepted.'}
Write-Output 'Shared real-process argv and nonzero-exit contracts passed.'
