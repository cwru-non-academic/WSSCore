# PowerShell adapters use the same Core channel policy as the Node engines.
function Get-WssCiChannelContext([string]$Channel='') {
    if(!$Channel){$Channel=$env:WSS_CI_CHANNEL}
    # Execution may be on trusted main while the authorized target is dev.
    # Never infer an authorization channel from GITHUB_REF or a PR base.
    if(!$Channel -and $env:GITHUB_ACTIONS -cne 'true'){$Channel='dev'}
    if(!$Channel){throw 'An explicit CI channel context is required.'}
    if(!(Get-Variable WssCiChannelContexts -Scope Script -ErrorAction SilentlyContinue)){$script:WssCiChannelContexts=@{}}
    if(!$script:WssCiChannelContexts.ContainsKey($Channel)){
        $module=if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'channels.mjs')){Join-Path $PSScriptRoot 'channels.mjs'}else{Join-Path $PSScriptRoot '../../release/channels.mjs'}
        $raw=& node $module $Channel
        if($LASTEXITCODE -ne 0){throw 'Canonical CI channel policy rejected the context.'}
        $script:WssCiChannelContexts[$Channel]=($raw -join "`n")|ConvertFrom-Json
    }
    $script:WssCiChannelContexts[$Channel]
}
function Get-WssCiChannel([string]$Channel=''){(Get-WssCiChannelContext $Channel).channel}
function Assert-WssCiTargetRef([string]$Channel=''){
    $context=Get-WssCiChannelContext $Channel
    if($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REF -cne $context.ref){throw 'Target workflow ref differs from the verified CI channel.'}
}
