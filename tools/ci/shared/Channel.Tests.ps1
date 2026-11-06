$ErrorActionPreference='Stop'
. "$PSScriptRoot/Channel.ps1"
foreach($branch in @('dev','main')){
    $context=Get-WssCiChannelContext $branch
    if($context.ref -cne "refs/heads/$branch" -or $context.channel -cne $branch){throw 'PowerShell channel differs from canonical Node policy.'}
}
$rejected=$false;try{$null=Get-WssCiChannelContext 'untrusted'}catch{$rejected=$true}
if(!$rejected){throw 'An unknown channel was accepted.'}
$names=@('GITHUB_ACTIONS','GITHUB_REF','GITHUB_BASE_REF','WSS_CI_CHANNEL')
$original=@{};foreach($name in $names){$original[$name]=[Environment]::GetEnvironmentVariable($name)}
try{
    $env:GITHUB_ACTIONS='true';$env:GITHUB_REF='refs/heads/main';$env:GITHUB_BASE_REF='main'
    Remove-Item Env:WSS_CI_CHANNEL -ErrorAction SilentlyContinue
    $rejected=$false;try{$null=Get-WssCiChannelContext}catch{$rejected=$true}
    if(!$rejected){throw 'Trusted-main execution must not implicitly authorize main.'}
    $env:WSS_CI_CHANNEL='dev'
    if((Get-WssCiChannelContext).channel -cne 'dev'){throw 'Explicit dev target was replaced by main execution source.'}
}finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$original[$name])}}
$global:LASTEXITCODE=0
