$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray package smoke tests " + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $root
    $layout = Join-Path $root 'layout'
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Value '<Package><Identity Name="GHCPSpendTray.Development" /></Package>'
    $script = Join-Path $root 'smoke-test-package.ps1'
    Copy-Item -LiteralPath "$PSScriptRoot\smoke-test-package.ps1" -Destination $script
    Copy-Item -LiteralPath "$PSScriptRoot\export-smoke-diagnostics.ps1" -Destination $root
    Set-Content -LiteralPath (Join-Path $root 'activate-package-smoke.ps1') -Value @'
param([string] $ApplicationId, [string] $AppArguments, [string] $ProcessIdPath)
$ErrorActionPreference = 'Stop'
$fixturePath = Join-Path $PSScriptRoot 'activation-fixture.json'
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$fixture.Launches++
$fixture | ConvertTo-Json | Set-Content -LiteralPath $fixturePath
$expected = if ($fixture.Launches % 2 -eq 0) { '--package-smoke-test --demo-empty' } else { '--package-smoke-test' }
if ($ApplicationId -cne 'Synthetic!App' -or $AppArguments -cne $expected -or
    $ProcessIdPath -cne (Join-Path $PSScriptRoot 'Packages\Synthetic\LocalState\Data\package-smoke-activation-pid.txt')) {
    throw 'Activation arguments were not passed intact.'
}
Write-Output 'synthetic activation entered'
$failed = $fixture.Launches -eq $fixture.FailAt
if ($failed -and $fixture.FailKind -eq 'activation-error') { throw 'synthetic activation error' }
if ($failed -and $fixture.FailKind -eq 'activation-timeout') { Start-Sleep -Seconds 120 }
if ($failed -and $fixture.FailKind -eq 'activation-missing') { exit 0 }
$id = if ($failed -and $fixture.FailKind -eq 'activation-invalid') { 'invalid' } else { '42' }
Set-Content -LiteralPath $ProcessIdPath -Value $id
Write-Output 'synthetic activation returned PID'
if ($failed -and $fixture.FailKind -eq 'activation-release-timeout') { Start-Sleep -Seconds 120 }
'@
    $global:PackageSmokeTestState = [pscustomobject]@{
        Registered = $false; Launches = 0; FailAt = 0; FailKind = 'assertion'; Layout = $layout
        Processes = [Collections.Generic.List[object]]::new()
        Workers = [Collections.Generic.List[int]]::new()
        Stopped = [Collections.Generic.List[int]]::new()
    }
    function Reset-ActivationFixture {
        param([int] $FailAt = 0, [string] $FailKind = '')
        @{ Launches = 0; FailAt = $FailAt; FailKind = $FailKind } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $root 'activation-fixture.json')
    }
    function Add-AppxPackage { $global:PackageSmokeTestState.Registered = $true }
    function Remove-AppxPackage { $global:PackageSmokeTestState.Registered = $false }
    function Start-Process {
        param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, [switch] $PassThru)
        $worker = Microsoft.PowerShell.Management\Start-Process -FilePath $FilePath -ArgumentList $ArgumentList `
            -RedirectStandardOutput $RedirectStandardOutput -RedirectStandardError $RedirectStandardError -PassThru
        $global:PackageSmokeTestState.Workers.Add($worker.Id)
        return $worker
    }
    function Stop-Process {
        param([int] $Id, [switch] $Force)
        $global:PackageSmokeTestState.Stopped.Add($Id)
        if ($Id -eq 85) { throw 'An unrelated app must never be terminated.' }
        if ($Id -eq 42 -or $Id -eq 84) {
            foreach ($process in $global:PackageSmokeTestState.Processes) {
                if ($process.Id -eq $Id) { $process.HasExited = $true }
            }
        } else {
            Microsoft.PowerShell.Management\Stop-Process -Id $Id -Force:$Force
        }
    }
    function Get-AppxPackage {
        if ($global:PackageSmokeTestState.Registered) {
            [pscustomobject]@{
                PackageFamilyName = 'Synthetic'; PackageFullName = 'Synthetic'
                InstallLocation = $global:PackageSmokeTestState.Layout
            }
        }
    }
    function Get-Process {
        param([int] $Id)
        if (-not $PSBoundParameters.ContainsKey('Id')) {
            if ($global:PackageSmokeTestState.FailKind -in @('activation-timeout', 'activation-release-timeout')) {
                foreach ($entry in @(
                    @{ Id = 84; Path = (Join-Path $global:PackageSmokeTestState.Layout 'GHCPSpendTray.exe') },
                    @{ Id = 85; Path = (Join-Path $root 'unrelated\GHCPSpendTray.exe') }
                )) {
                    $orphan = [pscustomobject]@{
                        Id = $entry.Id; Path = $entry.Path; ProcessName = 'GHCPSpendTray'
                        HasExited = $false; Disposed = $false
                    }
                    $orphan | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
                    $global:PackageSmokeTestState.Processes.Add($orphan)
                    $orphan
                }
            }
            return
        }
        if ($Id -ne 42) { throw "Unexpected app process ID: $Id" }
        $global:PackageSmokeTestState.Launches++
        $process = [pscustomobject]@{
            Id = 42; Handle = 1; ExitCode = 0; HasExited = $false; Disposed = $false
            ProcessorAffinity = [IntPtr]6; PriorityClass = [Diagnostics.ProcessPriorityClass]::Normal
        }
        $process | Add-Member ScriptMethod WaitForExit {
            param($milliseconds)
            if ($milliseconds -ne 45000) { throw 'The packaged process limit changed.' }
            $data = Join-Path $env:LOCALAPPDATA 'Packages\Synthetic\LocalState\Data'
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            $failed = $global:PackageSmokeTestState.Launches -eq $global:PackageSmokeTestState.FailAt
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'timeout') { return $false }
            $this.HasExited = $true
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'missing') { return $true }
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'exit') { $this.ExitCode = 1 }
            $result = if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'assertion') {
                'FAIL: synthetic assertion'
            } else { 'PASS: synthetic' }
            Set-Content -LiteralPath (Join-Path $data 'native-smoke-result.txt') -Value $result
            Set-Content -LiteralPath (Join-Path $data 'native-smoke-progress.txt') -Value 'synthetic readiness phase'
            return $true
        }
        $process | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
        $global:PackageSmokeTestState.Processes.Add($process)
        return $process
    }
    Reset-ActivationFixture
    $diagnostics = Join-Path $root 'diagnostics'
    & $script -Layout $layout -DiagnosticsDirectory $diagnostics -Iterations 3 -Constrained | Out-Null
    if ($global:PackageSmokeTestState.Registered -or $global:PackageSmokeTestState.Launches -ne 6 -or
        @($global:PackageSmokeTestState.Processes | Where-Object {
            $_.ProcessorAffinity.ToInt64() -ne 2 -or $_.PriorityClass -ne 'BelowNormal'
        }).Count) {
        throw 'Repeated smoke must constrain only each synthetic child and clean its registration.'
    }
    foreach ($iteration in 1..3) {
        foreach ($scenario in @('populated', 'empty')) {
            foreach ($file in @('native-smoke-result.txt', 'package-smoke-activation.log',
                'package-smoke-activation-error.log', 'package-smoke-activation-pid.txt')) {
                if (-not (Test-Path -LiteralPath (Join-Path $diagnostics "iteration-$iteration\$scenario\$file"))) {
                    throw 'Each repeated scenario must retain distinct native and activation diagnostics.'
                }
            }
        }
    }
    foreach ($failure in @(
        @{ kind = 'assertion'; message = 'FAIL: synthetic assertion' },
        @{ kind = 'timeout'; message = 'timed out' },
        @{ kind = 'missing'; message = 'No packaged smoke result' },
        @{ kind = 'exit'; message = 'process failed' },
        @{ kind = 'activation-error'; message = 'activation helper failed' },
        @{ kind = 'activation-missing'; message = 'no valid process ID' },
        @{ kind = 'activation-invalid'; message = 'no valid process ID' },
        @{ kind = 'activation-timeout'; message = 'activation timed out after 15 seconds' },
        @{ kind = 'activation-release-timeout'; message = 'activation timed out after 15 seconds' }
    )) {
        $global:PackageSmokeTestState.Launches = 0
        $global:PackageSmokeTestState.FailAt = 2
        $global:PackageSmokeTestState.FailKind = $failure.kind
        Reset-ActivationFixture -FailAt 2 -FailKind $failure.kind
        $failureDiagnostics = Join-Path $root $failure.kind
        $rejected = $false
        $messages = [Collections.Generic.List[string]]::new()
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        try {
            & $script -Layout $layout -DiagnosticsDirectory $failureDiagnostics -Iterations 3 |
                ForEach-Object { $messages.Add([string]$_) }
        }
        catch {
            if ($_.Exception.Message -notmatch $failure.message) { throw }
            $rejected = $true
        }
        $elapsed.Stop()
        $expectedLaunches = if ($failure.kind -like 'activation-*') { 1 } else { 2 }
        $fixture = Get-Content -LiteralPath (Join-Path $root 'activation-fixture.json') -Raw | ConvertFrom-Json
        if (-not $rejected -or $global:PackageSmokeTestState.Launches -ne $expectedLaunches -or
            $fixture.Launches -ne 2 -or $global:PackageSmokeTestState.Registered) {
            throw 'A failed observation must stop immediately without retrying or retaining registration.'
        }
        if ($failure.kind -like 'activation-*') {
            $last = Join-Path $failureDiagnostics 'iteration-1\empty'
            $log = Get-Content -LiteralPath (Join-Path $last 'package-smoke-activation.log') -Raw
            if ($log -notmatch 'synthetic activation entered' -or
                (Test-Path -LiteralPath (Join-Path $last 'native-smoke-result.txt')) -or
                (Test-Path -LiteralPath (Join-Path $last 'native-smoke-progress.txt'))) {
                throw 'Activation failures must retain helper phases without stale native success results.'
            }
            if ($failure.kind -eq 'activation-error' -and
                (Get-Content -LiteralPath (Join-Path $last 'package-smoke-activation-error.log') -Raw) -notmatch 'synthetic activation error') {
                throw 'Activation errors must retain the helper error output.'
            }
            if ($failure.kind -like '*timeout') {
                if ($elapsed.Elapsed.TotalSeconds -lt 15 -or $elapsed.Elapsed.TotalSeconds -ge 40 -or
                    $messages[0] -notmatch 'preparing activation') {
                    throw 'A hung activation must be announced before launch and bounded independently of app execution.'
                }
                $pidFile = Join-Path $last 'package-smoke-activation-pid.txt'
                if ((Test-Path -LiteralPath $pidFile) -ne ($failure.kind -eq 'activation-release-timeout')) {
                    throw 'The returned app PID must survive a hang releasing the activation manager.'
                }
            }
        }
    }
    if (@($global:PackageSmokeTestState.Stopped | Where-Object { $_ -eq 42 }).Count -ne 1 -or
        @($global:PackageSmokeTestState.Stopped | Where-Object { $_ -eq 84 }).Count -ne 2 -or
        @($global:PackageSmokeTestState.Stopped | Where-Object {
            $global:PackageSmokeTestState.Workers.Contains($_)
        }).Count -ne 2) {
        throw 'Timeouts must terminate the hung helpers and only app processes owned by this test.'
    }
    if (@($global:PackageSmokeTestState.Processes | Where-Object { $_.Id -ne 85 -and -not $_.Disposed }).Count) {
        throw 'Owned app process handles must always be disposed.'
    }
    foreach ($workerId in $global:PackageSmokeTestState.Workers) {
        if (Microsoft.PowerShell.Management\Get-Process -Id $workerId -ErrorAction SilentlyContinue) {
            throw "Activation helper $workerId was left running."
        }
    }
    $global:PackageSmokeTestState.Registered = $true
    $rejected = $false
    try { & $script -Layout $layout | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch 'already registered') { throw }
        $rejected = $true
    }
    if (-not $rejected -or -not $global:PackageSmokeTestState.Registered -or $global:PackageSmokeTestState.Launches -ne 1) {
        throw 'Existing development registrations must remain untouched.'
    }
    Write-Output 'PASS: repeated/constrained packaged smoke, bounded activation and release, retained failure phases, fail-fast assertions and isolated cleanup.'
}
finally {
    if (Test-Path variable:global:PackageSmokeTestState) {
        foreach ($workerId in $global:PackageSmokeTestState.Workers) {
            $worker = Microsoft.PowerShell.Management\Get-Process -Id $workerId -ErrorAction SilentlyContinue
            if ($worker) {
                try { Microsoft.PowerShell.Management\Stop-Process -Id $workerId -Force }
                finally { $worker.Dispose() }
            }
        }
    }
    $env:LOCALAPPDATA = $originalLocalAppData
    Remove-Variable -Name PackageSmokeTestState -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
