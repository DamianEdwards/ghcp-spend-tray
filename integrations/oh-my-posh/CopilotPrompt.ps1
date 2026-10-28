#Requires -Version 7.2

function Initialize-CopilotPrompt {
    [CmdletBinding()]
    param(
        [string]$AppExecutable = 'ghcp-spend-tray.exe',
        [string]$Account, [string]$DataDirectory, [switch]$InstallHook
    )
    $helper = Get-Command $AppExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1
    if (Get-Variable CopilotPromptAdapter -Scope Script -ErrorAction SilentlyContinue) { Disable-CopilotPrompt }
    $arguments = @('prompt')
    if ($Account) { $arguments += @('--account', $Account) }
    if ($DataDirectory) { $arguments += @('--data-dir', $DataDirectory) }
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
    $process = $null
    try {
        $state = Get-Variable CopilotPromptAdapter -Scope Script -ValueOnly -ErrorAction Stop
        # Explicit pipes also work for the GUI-subsystem app executable.
        $info = [Diagnostics.ProcessStartInfo]::new($state.Helper)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in $state.Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($info)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(5000)) {
            $process.Kill()
            throw 'App usage command timed out.'
        }
        $output = $stdout.GetAwaiter().GetResult().TrimEnd("`r", "`n")
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($errorText) { Write-Warning $errorText.TrimEnd() }
        if ($process.ExitCode -ne 0 -or $output.Length -gt 2048) { throw 'App usage command failed.' }
        $fields = $output -split "`t"
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
        Write-Warning 'Copilot prompt adapter failed. Check the installed GHCPSpendTray command and options.'
    }
    finally {
        if ($process) { $process.Dispose() }
        $global:LASTEXITCODE = $savedExitCode
    }
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
