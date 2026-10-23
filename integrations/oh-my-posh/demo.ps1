#Requires -Version 7.2
param([string]$AppExecutable = 'ghcp-spend-tray.exe', [string]$Account)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$directory = Join-Path ([IO.Path]::GetTempPath()) ('GHCPAppPrompt-demo-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$theme = Join-Path $directory 'theme.omp.json'
$segment = Get-Content (Join-Path $PSScriptRoot 'copilot.segment.json') -Raw | ConvertFrom-Json
@{ version = 4; blocks = @(@{ type = 'prompt'; alignment = 'left'; segments = @($segment) }) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $theme -Encoding utf8
try {
    Initialize-CopilotPrompt -AppExecutable $AppExecutable -Account $Account
    Update-CopilotPrompt
    oh-my-posh print primary --config $theme --shell pwsh
}
finally {
    Disable-CopilotPrompt
    [IO.File]::Delete($theme)
    [IO.Directory]::Delete($directory)
}
