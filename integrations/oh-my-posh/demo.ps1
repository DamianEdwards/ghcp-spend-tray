#Requires -Version 7.2
param([string]$HelperExecutable = 'ghcp-spend-prompt', [switch]$Live, [string]$Hostname = 'github.com')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$helper = (Get-Command $HelperExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$directory = Join-Path ([IO.Path]::GetTempPath()) ('GHCPPrompt-demo-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$script:CopilotDemoTheme = Join-Path $directory 'theme.omp.json'
& $helper demo-theme (Join-Path $PSScriptRoot 'copilot.segment.json') $script:CopilotDemoTheme
if ($LASTEXITCODE -ne 0) { throw 'Native demo theme generation failed.' }
function Show-CopilotPromptExamples {
    foreach ($state in @('green','yellow','red','in_progress','not_connected')) {
        $fields = (& $helper demo $state) -split "`t"
        if ($LASTEXITCODE -ne 0 -or $fields.Count -ne 5) { throw 'Native demo failed.' }
        $env:COPILOT_SPEND, $env:COPILOT_FORECAST, $env:COPILOT_FORECAST_STATE, $env:COPILOT_CONNECTION_STATE = $fields[1..4]
        Write-Host "Synthetic $state"
        oh-my-posh print primary --config $script:CopilotDemoTheme --shell pwsh
        if ($LASTEXITCODE -ne 0) { throw 'Oh My Posh rendering failed.' }
        Write-Host ''
    }
}
if ($Live) {
    oh-my-posh init pwsh --config $script:CopilotDemoTheme | Invoke-Expression
    Initialize-CopilotPrompt -HelperExecutable $helper -Hostname $Hostname -CacheDirectory (Join-Path $directory 'cache') -InstallHook
    Write-Host 'Isolated native helper demo. No profile was modified; press Enter after the background fetch.'
    Write-Host "Demo theme/cache directory: $directory"
}
else {
    try { Show-CopilotPromptExamples }
    finally {
        [IO.File]::Delete($script:CopilotDemoTheme)
        [IO.Directory]::Delete($directory)
    }
}
