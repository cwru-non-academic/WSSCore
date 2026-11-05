$ErrorActionPreference='Stop'
. "$PSScriptRoot/Evidence.ps1"
$repo='cwru-non-academic/WSSCore';$source='a'*40
$identity=New-WssCiEvidenceIdentity $repo '123' '2' $source 'X86_64'
Assert-WssCiEvidenceIdentity $identity $repo '123' '2' $source 'X64'
foreach($field in @('repository','run_id','attempt','source_sha','architecture','schema')) {
    $bad=$identity|ConvertTo-Json|ConvertFrom-Json
    $bad.$field='wrong'
    $rejected=$false;try{Assert-WssCiEvidenceIdentity $bad $repo '123' '2' $source 'x64'}catch{$rejected=$true}
    if(!$rejected){throw "Changed CI identity accepted: $field"}
}
foreach($priorAttempt in @('1','3')) {
    $rejected=$false;try{Assert-WssCiEvidenceIdentity $identity $repo '123' $priorAttempt $source 'x64'}catch{$rejected=$true}
    if(!$rejected){throw 'Evidence from another workflow attempt accepted.'}
}
$rejected=$false;try{New-WssCiEvidenceIdentity $repo '123' '' $source 'x64'}catch{$rejected=$true}
if(!$rejected){throw 'Missing workflow attempt accepted.'}
$record=[pscustomobject]@{schema='fixture/v1';run_id='1';attempt='2';source_sha=('a'*40);hardware=$false}
$expected=@{schema='fixture/v1';run_id='1';attempt='2';source_sha=('a'*40);hardware=$false}
Assert-WssCiEvidenceFields $record $expected
foreach($field in @('run_id','attempt','source_sha','hardware')) {
    $bad=$record|ConvertTo-Json|ConvertFrom-Json
    $bad.$field=if($field -ceq 'hardware'){'false'}else{'wrong'}
    $rejected=$false;try{Assert-WssCiEvidenceFields $bad $expected}catch{$rejected=$true}
    if(!$rejected){throw "Changed evidence accepted: $field"}
}
Assert-WssCiArchitecturePair @(@{architecture='x64'},@{architecture='arm64'}) @('x64','arm64')
$rejected=$false;try{Assert-WssCiArchitecturePair @(@{architecture='x64'},@{architecture='x64'}) @('x64','arm64')}catch{$rejected=$true}
if(!$rejected){throw 'Duplicated architecture accepted.'}
Write-Output 'Shared typed evidence identity and architecture contracts passed.'
