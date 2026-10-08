#Requires -Version 7.2

function Initialize-CopilotPrompt {
    [CmdletBinding()]
    param(
        [string]$HelperExecutable = 'ghcp-spend-prompt',
        [string]$Hostname = 'github.com', [string]$CacheDirectory,
        [int]$RefreshMinutes = 60, [int]$RequestTimeoutSeconds = 15,
        [string]$GhExecutable = 'gh', [switch]$InstallHook
    )
    $helper = Get-Command $HelperExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1
    if (Get-Variable CopilotPromptAdapter -Scope Script -ErrorAction SilentlyContinue) { Disable-CopilotPrompt }
    $arguments = @('prompt', '--hostname', $Hostname, '--refresh-minutes', "$RefreshMinutes",
        '--request-timeout', "$RequestTimeoutSeconds", '--gh-executable', $GhExecutable)
    if ($CacheDirectory) { $arguments += @('--cache-dir', $CacheDirectory) }
    $script:CopilotPromptAdapter = @{
        Helper = $helper.Source; Arguments = $arguments
        HookInstalled = $false; PreviousHook = $null; PreviousAlias = $null
    }
    if ($InstallHook) {
        $previous = Get-Command Set-PoshContext -ErrorAction SilentlyContinue
        if ($previous) {
            $script:CopilotPromptAdapter.PreviousHook = if ($previous -is [Management.Automation.AliasInfo]) {
                $previous.ResolvedCommand
            }
            else { $previous }
        }
        $script:CopilotPromptAdapter.PreviousAlias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
        New-Alias Set-PoshContext Invoke-CopilotPromptContext -Scope Global -Force
        $script:CopilotPromptAdapter.HookInstalled = $true
    }
}

function Update-CopilotPrompt {
    $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    try {
        $state = Get-Variable CopilotPromptAdapter -Scope Script -ValueOnly -ErrorAction Stop
        $output = @(& $state.Helper @($state.Arguments))
        if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) { throw 'Native helper failed.' }
        $fields = $output[0] -split "`t"
        if ($fields.Count -ne 5 -or $fields[0] -cne 'GHCP-SPEND/1' -or
            $fields[3] -cnotin @('green', 'yellow', 'red', 'unknown') -or
            $fields[4] -cnotin @('connected', 'in_progress', 'not_connected') -or
            $fields.Where({ [string]::IsNullOrEmpty($_) -or $_ -match '[\x00-\x1f\x7f]' }).Count) {
            throw 'Invalid native helper protocol.'
        }
        $env:COPILOT_SPEND, $env:COPILOT_FORECAST, $env:COPILOT_FORECAST_STATE, $env:COPILOT_CONNECTION_STATE = $fields[1..4]
    }
    catch {
        $env:COPILOT_SPEND = 'unavailable'; $env:COPILOT_FORECAST = '?'
        $env:COPILOT_FORECAST_STATE = 'unknown'; $env:COPILOT_CONNECTION_STATE = 'not_connected'
        Write-Warning 'Copilot prompt adapter failed. Check the native helper installation and options.'
    }
    finally { $global:LASTEXITCODE = $savedExitCode }
}

function Invoke-CopilotPromptContext {
    param($OriginalStatus)
    $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    try {
        if ($script:CopilotPromptAdapter.PreviousHook) { & $script:CopilotPromptAdapter.PreviousHook $OriginalStatus }
        Update-CopilotPrompt
    }
    finally { $global:LASTEXITCODE = $savedExitCode }
}

function Disable-CopilotPrompt {
    if (!(Get-Variable CopilotPromptAdapter -Scope Script -ErrorAction SilentlyContinue)) { return }
    $state = $script:CopilotPromptAdapter
    $alias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
    if ($state.HookInstalled -and $alias -and $alias.Definition -eq 'Invoke-CopilotPromptContext') {
        Remove-Alias Set-PoshContext -Scope Global -Force
        if ($state.PreviousAlias) {
            New-Alias Set-PoshContext $state.PreviousAlias.Definition -Scope Global -Force -Option $state.PreviousAlias.Options
        }
    }
    foreach ($name in @('COPILOT_SPEND', 'COPILOT_FORECAST', 'COPILOT_FORECAST_STATE', 'COPILOT_CONNECTION_STATE')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    Remove-Variable CopilotPromptAdapter -Scope Script
}
