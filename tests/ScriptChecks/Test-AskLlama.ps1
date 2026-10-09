#requires -Version 5.1
# Run with both powershell.exe and pwsh. No request reaches the active model.
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$requestScript = Join-Path $root 'scripts/Ask-Llama.ps1'
$llamaCheckState = @{ Requests = 0; Payload = $null }

function Invoke-RestMethod {
    [CmdletBinding()]
    param($Uri, $Method, $TimeoutSec, $ContentType, $Body, [switch]$UseBasicParsing, [switch]$NoProxy)
    if ($PSVersionTable.PSVersion.Major -lt 7 -and $NoProxy) { throw 'NoProxy is unavailable in Windows PowerShell 5.1.' }
    $llamaCheckState.Requests++
    if ($Method -eq 'Get') { return @{ data = @(@{ id = 'Test model' }) } }
    $llamaCheckState.Payload = [System.Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json
    return @{ choices = @(@{ message = @{ content = 'Mock answer' }; finish_reason = 'stop' }) }
}

$prompt = 'Review the files ' + [char]0x03A9
$reply = & $requestScript -WorkingDirectory $root -Prompt $prompt -Files 'README.md'
if ($reply -ne 'Mock answer' -or $llamaCheckState.Requests -ne 2 -or $llamaCheckState.Payload.model -ne 'Test model') {
    throw 'Model discovery or response handling failed.'
}
$content = $llamaCheckState.Payload.messages[1].content
if (-not $content.Contains($prompt) -or -not $content.Contains('# Llama Model Loader')) {
    throw 'UTF-8 request or relative attachment handling failed.'
}
$context = ($content -split "`n`nRequest:", 2)[0].Substring("Workspace context (JSON):`n".Length) | ConvertFrom-Json
if ($context.working_directory -ne $root -or $context.files[0].path -ne (Join-Path $root 'README.md')) {
    throw 'Workspace or attachment path was not resolved correctly.'
}
$llamaCheckState.Requests = 0
$raw = & $requestScript -WorkingDirectory $root -Prompt $prompt -Model 'Explicit model' -Files (Join-Path $root 'README.md') -RawResponse
if ($llamaCheckState.Requests -ne 1 -or $llamaCheckState.Payload.model -ne 'Explicit model' -or $raw.choices[0].message.content -ne 'Mock answer') {
    throw 'Explicit model, absolute attachment, or raw response handling failed.'
}
Write-Output "Ask-Llama compatibility checks passed on PowerShell $($PSVersionTable.PSVersion)."
