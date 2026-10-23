# Run in Windows PowerShell 5.1 with Developer Mode already enabled.
param(
    [string] $Layout = "$PSScriptRoot\..\artifacts\msix\x64",
    [string] $DiagnosticsDirectory,
    [ValidateRange(1, 10)][int] $Iterations = 1,
    [switch] $Constrained
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$layoutPath = (Resolve-Path -LiteralPath $Layout).Path
$manifestPath = Join-Path $layoutPath 'AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
if ($manifest.Package.Identity.Name -cne 'GHCPSpendTray.Development') {
    throw 'Only the development package may be registered by this test.'
}
if (Get-AppxPackage -Name GHCPSpendTray.Development) {
    throw 'A development package is already registered. This test never replaces existing registrations.'
}
$activationScript = Join-Path $PSScriptRoot 'activate-package-smoke.ps1'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$executable = Join-Path $layoutPath 'GHCPSpendTray.exe'
$registered = $false
$process = $null
try {
    Add-AppxPackage -Register $manifestPath
    $registered = $true
    $package = Get-AppxPackage -Name GHCPSpendTray.Development
    if (-not $package) { throw 'Registration did not produce a development package.' }
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\ghcp-spend-tray.exe'
    if (-not (Test-Path -LiteralPath $alias)) { throw 'The app execution alias was not registered.' }
    $queryDirectory = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\Data"
    New-Item -ItemType Directory -Path $queryDirectory -Force | Out-Null
    $queryOutput = Join-Path $queryDirectory 'shell-cli-smoke-output.txt'
    $queryError = Join-Path $queryDirectory 'shell-cli-smoke-error.txt'
    $query = Start-Process -FilePath $alias -ArgumentList @('usage', '--format', 'json') `
        -RedirectStandardOutput $queryOutput -RedirectStandardError $queryError -PassThru
    try {
        $null = $query.Handle
        if (-not $query.WaitForExit(10000)) {
            Stop-Process -Id $query.Id -Force
            throw 'Packaged usage alias did not exit before UI startup.'
        }
        $queryDocument = Get-Content -LiteralPath $queryOutput -Raw | ConvertFrom-Json
        if ($query.ExitCode -ne 0 -or $queryDocument.version -ne 1 -or
            [IO.File]::ReadAllText($queryError).Trim()) {
            throw 'Packaged usage alias did not return versioned JSON on stdout.'
        }
        Write-Output 'PASS: installed execution alias returns JSON and exits without starting the tray UI.'
    }
    finally { $query.Dispose() }
    $profileCode = "`$ErrorActionPreference = 'Stop'; & '" + $alias.Replace("'", "''") + "' init pwsh | Invoke-Expression; " +
        "if ((Get-Alias Set-PoshContext -ErrorAction Stop).Definition -ne 'Invoke-CopilotPromptContext') { throw 'Init hook missing.' }; " +
        "Disable-CopilotPrompt; Write-Output 'PASS: bundled shell initialization.'"
    $encodedProfile = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($profileCode))
    $initOutput = Join-Path $queryDirectory 'shell-init-smoke-output.txt'
    $initError = Join-Path $queryDirectory 'shell-init-smoke-error.txt'
    $init = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encodedProfile) `
        -RedirectStandardOutput $initOutput -RedirectStandardError $initError -PassThru
    try {
        $null = $init.Handle
        if (-not $init.WaitForExit(10000)) {
            Stop-Process -Id $init.Id -Force
            throw 'Packaged shell initialization pipeline did not exit.'
        }
        if ($init.ExitCode -ne 0 -or
            [IO.File]::ReadAllText($initOutput).Trim() -cne 'PASS: bundled shell initialization.' -or
            [IO.File]::ReadAllText($initError).Trim()) {
            Write-Output ([IO.File]::ReadAllText($initOutput))
            Write-Output ([IO.File]::ReadAllText($initError))
            throw "Packaged shell initialization pipeline failed (exit code $($init.ExitCode))."
        }
    }
    finally {
        $init.Dispose()
        if ($DiagnosticsDirectory) {
            & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory $queryDirectory `
                -Destination (Join-Path $DiagnosticsDirectory 'shell-init') | Out-Null
        }
    }
    $result = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\Data\native-smoke-result.txt"
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        foreach ($arguments in @('--package-smoke-test', '--package-smoke-test --demo-empty')) {
            Write-Output "Packaged smoke iteration $iteration/$Iterations ($arguments); constrained=$($Constrained.IsPresent); preparing activation"
            $dataDirectory = Split-Path $result -Parent
            New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
            $activationOutput = Join-Path $dataDirectory 'package-smoke-activation.log'
            $activationError = Join-Path $dataDirectory 'package-smoke-activation-error.log'
            $activationPid = Join-Path $dataDirectory 'package-smoke-activation-pid.txt'
            $activation = $null
            try {
                foreach ($path in @($result, (Join-Path $dataDirectory 'native-smoke-progress.txt'),
                    $activationOutput, $activationError, $activationPid)) {
                    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
                }
                # A separate process makes a blocked COM call (including release) cancellable.
                Write-Output "$([DateTimeOffset]::UtcNow.ToString('O')) Starting packaged activation (15-second limit)"
                $activation = Start-Process -FilePath $powershell -ArgumentList @(
                    '-NoProfile', '-NonInteractive', '-STA', '-File', "`"$activationScript`"",
                    '-ApplicationId', "`"$($package.PackageFamilyName)!App`"",
                    '-AppArguments', "`"$arguments`"", '-ProcessIdPath', "`"$activationPid`""
                ) -RedirectStandardOutput $activationOutput -RedirectStandardError $activationError -PassThru
                $null = $activation.Handle
                if (-not $activation.WaitForExit(15000)) {
                    throw "Packaged activation timed out after 15 seconds ($arguments)."
                }
                if ($activation.ExitCode -ne 0) {
                    throw "Packaged activation helper failed ($arguments). Exit code: $($activation.ExitCode)"
                }
                [uint32]$processId = 0
                if (-not (Test-Path -LiteralPath $activationPid) -or
                    -not [uint32]::TryParse((Get-Content -LiteralPath $activationPid -Raw), [ref]$processId) -or
                    $processId -eq 0) {
                    throw "Packaged activation returned no valid process ID ($arguments)."
                }
                Write-Output "$([DateTimeOffset]::UtcNow.ToString('O')) Packaged activation completed; process ID: $processId"
                $process = Get-Process -Id $processId
                # Retain the native handle so ExitCode remains available after a fast startup crash.
                $null = $process.Handle
                if ($Constrained) {
                    # Constrain only this synthetic child, never the runner or another app.
                    $affinity = $process.ProcessorAffinity.ToInt64()
                    $process.ProcessorAffinity = [IntPtr]($affinity -band (-$affinity))
                    $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
                }
                Write-Output "$([DateTimeOffset]::UtcNow.ToString('O')) Waiting for packaged smoke process (45-second limit)"
                if (-not $process.WaitForExit(45000)) {
                    throw "Packaged smoke test timed out ($arguments)."
                }
                if (-not (Test-Path -LiteralPath $result)) {
                    throw "No packaged smoke result ($arguments). Exit code: $($process.ExitCode)"
                }
                $text = Get-Content -LiteralPath $result -Raw
                if (-not $text.StartsWith('PASS:')) { throw $text }
                if ($process.ExitCode -ne 0) { throw "Packaged smoke process failed ($arguments). Exit code: $($process.ExitCode)" }
                Write-Output $text
            }
            finally {
                try {
                    if ($null -ne $activation -and -not $activation.HasExited) {
                        Stop-Process -Id $activation.Id -Force
                        if (-not $activation.WaitForExit(5000)) { throw 'Activation helper did not stop after termination.' }
                    }
                    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                    if ($null -eq $process) {
                        # Activation can launch the app without returning its PID. This layout
                        # belongs only to the development registration created by this test.
                        foreach ($orphan in @(Get-Process | Where-Object {
                            $_.ProcessName -eq 'GHCPSpendTray' -and $_.Path -eq $executable
                        })) {
                            try { if (-not $orphan.HasExited) { Stop-Process -Id $orphan.Id -Force } }
                            finally { $orphan.Dispose() }
                        }
                    }
                }
                finally {
                    try {
                        foreach ($log in @($activationOutput, $activationError)) {
                            if (Test-Path -LiteralPath $log) {
                                $content = Get-Content -LiteralPath $log -Raw
                                if ($content) { Write-Output $content }
                            }
                        }
                        if ($DiagnosticsDirectory) {
                            $scenario = if ($arguments -match '--demo-empty') { 'empty' } else { 'populated' }
                            if ($Iterations -gt 1) { $scenario = "iteration-$iteration\$scenario" }
                            & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory $dataDirectory `
                                -Destination (Join-Path $DiagnosticsDirectory $scenario)
                        }
                    }
                    finally {
                        if ($null -ne $activation) { $activation.Dispose() }
                        if ($null -ne $process) { $process.Dispose(); $process = $null }
                    }
                }
            }
        }
    }
    Write-Output 'PASS: packaged activation, package-local data and disabled Windows StartupTask.'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if ($null -ne $process) { $process.Dispose() }
    if ($registered) {
        $package = Get-AppxPackage -Name GHCPSpendTray.Development
        if ($package -and $package.InstallLocation -eq $layoutPath) {
            Remove-AppxPackage -Package $package.PackageFullName
            if (Get-AppxPackage -Name GHCPSpendTray.Development) { throw 'Development package cleanup failed.' }
        } else { throw 'Development registration changed; refusing to remove a different package.' }
    }
}
