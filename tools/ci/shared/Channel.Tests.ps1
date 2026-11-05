$ErrorActionPreference='Stop'
. "$PSScriptRoot/Channel.ps1"
foreach($branch in @('dev','main')){
    $context=Get-WssCiChannelContext $branch
    if($context.ref -cne "refs/heads/$branch" -or $context.channel -cne $branch){throw 'PowerShell channel differs from canonical Node policy.'}
}
$rejected=$false;try{$null=Get-WssCiChannelContext 'untrusted'}catch{$rejected=$true}
if(!$rejected){throw 'An unknown channel was accepted.'}
$global:LASTEXITCODE=0
