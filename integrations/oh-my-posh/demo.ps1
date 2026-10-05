#Requires -Version 7.2
param([switch]$Live, [string]$Hostname = 'github.com')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$posh = Get-Command oh-my-posh -CommandType Application -ErrorAction Stop | Select-Object -First 1
$base = if ($Live) { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GHCPSpendPrompt' }
    else { [IO.Path]::GetTempPath() }
$directory = Join-Path $base ('demo-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$script:CopilotDemoTheme = Join-Path $directory 'demo.omp.json'
$segment = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'copilot.segment.json') -Raw | ConvertFrom-Json -AsHashtable
$theme = @{
    version = 4; final_space = $true
    blocks = @(@{
        type = 'prompt'; alignment = 'left'
        segments = @(
            @{ type = 'path'; style = 'plain'; foreground = '#89B4FA'; template = '{{ .Path }} '; options = @{ style = 'folder' } },
            $segment,
            @{ type = 'text'; style = 'plain'; foreground = '#89B4FA'; template = '> ' }
        )
    })
}
Write-CopilotDocument $script:CopilotDemoTheme $theme

function Show-CopilotPromptExamples {
    $now = [DateTimeOffset]::new(2026, 10, 16, 12, 0, 0, [TimeSpan]::Zero)
    $names = @('COPILOT_SPEND', 'COPILOT_FORECAST', 'COPILOT_FORECAST_STATE', 'COPILOT_CONNECTION_STATE')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $savedExitCode = $global:LASTEXITCODE
    try {
        foreach ($entry in @(@('green', 6000), @('yellow', 9000), @('red', 12000))) {
            $cache = @{
                status = 'fresh'; fetchedAtUtc = $now.ToString('O'); sourceTimestampUtc = $null
                resetAtUtc = '2026-11-01T00:00:00Z'
                consumptionUsd = $entry[1] / 2; allocationUsd = 10000; unlimited = $false
            }
            $view = Get-CopilotPresentation $cache $now
            $env:COPILOT_SPEND = $view.Text
            $env:COPILOT_FORECAST = $view.Forecast
            $env:COPILOT_FORECAST_STATE = $view.ForecastState
            $env:COPILOT_CONNECTION_STATE = 'connected'
            Write-Host ('Synthetic ' + $entry[0] + ':')
            & $posh.Source print primary --config $script:CopilotDemoTheme --shell pwsh
            if ($LASTEXITCODE -ne 0) { throw 'Oh My Posh rendering failed.' }
            Write-Host ''
        }
        foreach ($connection in @('in_progress', 'not_connected')) {
            $env:COPILOT_CONNECTION_STATE = $connection
            $env:COPILOT_SPEND = 'unavailable'
            $env:COPILOT_FORECAST = '?'
            $env:COPILOT_FORECAST_STATE = 'unknown'
            Write-Host ('Synthetic ' + $connection + ':')
            & $posh.Source print primary --config $script:CopilotDemoTheme --shell pwsh
            if ($LASTEXITCODE -ne 0) { throw 'Oh My Posh rendering failed.' }
            Write-Host ''
        }
    }
    finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        $global:LASTEXITCODE = $savedExitCode
    }
}

if ($Live) {
    & $posh.Source init pwsh --config $script:CopilotDemoTheme | Invoke-Expression
    if ($LASTEXITCODE -ne 0) { throw 'Oh My Posh initialization failed.' }
    Initialize-CopilotPrompt -Hostname $Hostname -CacheDirectory (Join-Path $directory 'cache') -InstallHook
    Write-Host 'Isolated live demo. Your normal profile is not changed.' -ForegroundColor Cyan
    Write-Host 'Press Enter after a few seconds for the first background result.'
    Write-Host 'Show-CopilotPromptExamples displays synthetic states; Disable-CopilotPrompt removes the hook.'
    Write-Host ('Demo theme/cache directory: ' + $directory)
}
else {
    try { Show-CopilotPromptExamples }
    finally {
        [IO.File]::Delete($script:CopilotDemoTheme)
        [IO.Directory]::Delete($directory)
    }
}
