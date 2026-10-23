$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray package smoke tests " + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $root
    $layout = Join-Path $root 'layout'
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    $alias = Join-Path $root 'Microsoft\WindowsApps\ghcp-spend-tray.exe'
    New-Item -ItemType Directory -Path (Split-Path $alias -Parent) -Force | Out-Null
    Set-Content -LiteralPath $alias -Value 'synthetic execution alias'
    Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Value '<Package><Identity Name="GHCPSpendTray.Development" /></Package>'
    $script = Join-Path $root 'smoke-test-package.ps1'
    Copy-Item -LiteralPath "$PSScriptRoot\smoke-test-package.ps1" -Destination $script
    Copy-Item -LiteralPath "$PSScriptRoot\export-smoke-diagnostics.ps1" -Destination $root
    $global:PackageSmokeTestState = [pscustomobject]@{
        Registered = $false; Launches = 0; ActivationLaunches = 0; FailAt = 0; FailKind = 'assertion'; Layout = $layout
        Processes = [Collections.Generic.List[object]]::new()
        Workers = [Collections.Generic.List[object]]::new()
        Queries = [Collections.Generic.List[object]]::new()
        Stopped = [Collections.Generic.List[int]]::new()
    }
    function Add-AppxPackage { $global:PackageSmokeTestState.Registered = $true }
    function Remove-AppxPackage { $global:PackageSmokeTestState.Registered = $false }
    function Start-Process {
        param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, [switch] $PassThru)
        $state = $global:PackageSmokeTestState
        if ($FilePath -ceq $alias -or $FilePath -ceq 'pwsh') {
            $profileInit = $FilePath -ceq 'pwsh'
            if ($profileInit) {
                if ($ArgumentList.Count -ne 4 -or $ArgumentList[2] -cne '-EncodedCommand' -or
                    [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentList[3])) -notmatch
                    'init pwsh \| Invoke-Expression') {
                    throw 'Packaged initialization must test the direct PowerShell profile pipeline.'
                }
            }
            elseif (($ArgumentList -join ',') -cne 'usage,--format,json' -or -not $PassThru) {
                throw 'Usage alias must receive read-only JSON arguments.'
            }
            $queryJson = if ($profileInit -and $state.FailKind -eq 'init-missing') { '' }
                elseif ($profileInit) { 'PASS: bundled shell initialization.' }
                elseif ($state.FailKind -eq 'query-invalid') { '{"version":99}' } else { '{"version":1,"status":"unavailable"}' }
            # Redirected successful stderr is zero bytes, not the newline Set-Content adds.
            [IO.File]::WriteAllText($RedirectStandardOutput, $queryJson)
            $queryError = if ($state.FailKind -eq 'query-stderr' -or
                ($profileInit -and $state.FailKind -eq 'init-stderr')) { 'synthetic stderr' } else { '' }
            [IO.File]::WriteAllText($RedirectStandardError, $queryError)
            $query = [pscustomobject]@{
                Id = $(if ($profileInit) { 901 } else { 900 }); Handle = 1
                ExitCode = $(if ($state.FailKind -eq 'query-error' -or ($profileInit -and $state.FailKind -eq 'init-error')) { 1 } else { 0 })
                HasExited = $false; Disposed = $false
                Failure = $state.FailKind; IsProfileInit = $profileInit
            }
            $query | Add-Member ScriptMethod WaitForExit {
                param($milliseconds)
                if ($milliseconds -ne 10000) { throw 'Usage CLI must have a bounded 10-second smoke timeout.' }
                if ($this.Failure -eq 'query-timeout' -or
                    ($this.IsProfileInit -and $this.Failure -eq 'init-timeout')) { return $false }
                $this.HasExited = $true
                return $true
            }
            $query | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
            $state.Queries.Add($query)
            return $query
        }
        $state.ActivationLaunches++
        $data = Join-Path $env:LOCALAPPDATA 'Packages\Synthetic\LocalState\Data'
        $pidPath = Join-Path $data 'package-smoke-activation-pid.txt'
        $appArguments = if ($state.ActivationLaunches % 2 -eq 0) { '--package-smoke-test --demo-empty' } else { '--package-smoke-test' }
        $expectedArguments = @(
            '-NoProfile', '-NonInteractive', '-STA', '-File', "`"$(Join-Path $root 'activate-package-smoke.ps1')`"",
            '-ApplicationId', '"Synthetic!App"', '-AppArguments', "`"$appArguments`"",
            '-ProcessIdPath', "`"$pidPath`""
        )
        if ($FilePath -cne (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -or
            -not $PassThru -or $ArgumentList.Count -ne $expectedArguments.Count -or
            $RedirectStandardOutput -cne (Join-Path $data 'package-smoke-activation.log') -or
            $RedirectStandardError -cne (Join-Path $data 'package-smoke-activation-error.log')) {
            throw 'Activation process configuration changed.'
        }
        for ($index = 0; $index -lt $expectedArguments.Count; $index++) {
            if ($ArgumentList[$index] -cne $expectedArguments[$index]) {
                throw "Activation argument $index was not passed intact."
            }
        }
        $worker = [pscustomobject]@{
            Id = 1000 + $state.Workers.Count; Handle = 1; ExitCode = 0; HasExited = $false; Disposed = $false
            Failure = $(if ($state.ActivationLaunches -eq $state.FailAt) { $state.FailKind } else { '' })
            OutputPath = $RedirectStandardOutput; ErrorPath = $RedirectStandardError; PidPath = $pidPath
            Waits = [Collections.Generic.List[int]]::new()
        }
        $worker | Add-Member ScriptMethod WaitForExit {
            param($milliseconds)
            $this.Waits.Add($milliseconds)
            if ($this.Waits.Count -eq 2) {
                if ($milliseconds -ne 5000 -or -not $this.HasExited) {
                    throw 'A terminated activation helper must be given 5 seconds to exit.'
                }
                return $true
            }
            if ($this.Waits.Count -ne 1 -or $milliseconds -ne 15000) {
                throw 'The activation helper must be given 15 seconds to exit.'
            }
            Set-Content -LiteralPath $this.OutputPath -Value 'synthetic activation entered'
            Set-Content -LiteralPath $this.ErrorPath -Value ''
            if ($this.Failure -eq 'activation-timeout') { return $false }
            if ($this.Failure -eq 'activation-error') {
                Set-Content -LiteralPath $this.ErrorPath -Value 'synthetic activation error'
                $this.ExitCode = 1
            } elseif ($this.Failure -ne 'activation-missing') {
                $id = if ($this.Failure -eq 'activation-invalid') { 'invalid' } else { '42' }
                Set-Content -LiteralPath $this.PidPath -Value $id
                Add-Content -LiteralPath $this.OutputPath -Value 'synthetic activation returned PID'
            }
            if ($this.Failure -eq 'activation-release-timeout') { return $false }
            $this.HasExited = $true
            return $true
        }
        $worker | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
        $state.Workers.Add($worker)
        return $worker
    }
    function Stop-Process {
        param([int] $Id, [switch] $Force)
        $global:PackageSmokeTestState.Stopped.Add($Id)
        if ($Id -eq 85) { throw 'An unrelated app must never be terminated.' }
        $owned = @(
            (@($global:PackageSmokeTestState.Processes) + @($global:PackageSmokeTestState.Workers) +
                @($global:PackageSmokeTestState.Queries)) |
                Where-Object { $_.Id -eq $Id }
        )
        if (-not $owned -or -not $Force) { throw "Unexpected process termination: $Id" }
        foreach ($process in $owned) {
            $process.HasExited = $true
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
        $global:PackageSmokeTestState.ActivationLaunches = 0
        $global:PackageSmokeTestState.FailAt = 2
        $global:PackageSmokeTestState.FailKind = $failure.kind
        $failureDiagnostics = Join-Path $root $failure.kind
        $rejected = $false
        $messages = [Collections.Generic.List[string]]::new()
        try {
            & $script -Layout $layout -DiagnosticsDirectory $failureDiagnostics -Iterations 3 |
                ForEach-Object { $messages.Add([string]$_) }
        }
        catch {
            if ($_.Exception.Message -notmatch $failure.message) { throw }
            $rejected = $true
        }
        $expectedLaunches = if ($failure.kind -like 'activation-*') { 1 } else { 2 }
        if (-not $rejected -or $global:PackageSmokeTestState.Launches -ne $expectedLaunches -or
            $global:PackageSmokeTestState.ActivationLaunches -ne 2 -or $global:PackageSmokeTestState.Registered) {
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
                if ($messages[0] -notmatch 'installed execution alias' -or
                    $messages[1] -notmatch 'preparing activation') {
                    throw 'A hung activation must be announced before launch.'
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
            $_ -ge 1000
        }).Count -ne 2) {
        throw 'Timeouts must terminate the hung helpers and only app processes owned by this test.'
    }
    if (@($global:PackageSmokeTestState.Processes | Where-Object { $_.Id -ne 85 -and -not $_.Disposed }).Count) {
        throw 'Owned app process handles must always be disposed.'
    }
    foreach ($worker in $global:PackageSmokeTestState.Workers) {
        $hung = $worker.Failure -in @('activation-timeout', 'activation-release-timeout')
        $expectedWaits = if ($hung) { '15000,5000' } else { '15000' }
        if (-not $worker.HasExited -or -not $worker.Disposed -or
            ($worker.Waits -join ',') -cne $expectedWaits -or
            $global:PackageSmokeTestState.Stopped.Contains($worker.Id) -ne $hung) {
            throw "Activation helper $($worker.Id) must observe exact timeout, termination and disposal semantics."
        }
    }
    if ($global:PackageSmokeTestState.Queries.Count -ne 20 -or
        @($global:PackageSmokeTestState.Queries | Where-Object { -not $_.HasExited -or -not $_.Disposed }).Count) {
        throw 'Every registered smoke run must verify and dispose the read-only execution alias query.'
    }
    foreach ($queryFailure in @('query-invalid', 'query-error', 'query-timeout', 'query-stderr',
        'init-error', 'init-timeout', 'init-missing', 'init-stderr')) {
        $launches = $global:PackageSmokeTestState.ActivationLaunches
        $global:PackageSmokeTestState.FailKind = $queryFailure
        $rejected = $false
        try { & $script -Layout $layout | Out-Null }
        catch {
            $expectedMessage = if ($queryFailure -like 'init-*') { 'Packaged shell initialization pipeline' }
                else { 'Packaged usage alias' }
            if ($_.Exception.Message -notlike "$expectedMessage*") { throw }
            $rejected = $true
        }
        if (-not $rejected -or $global:PackageSmokeTestState.Registered -or
            $global:PackageSmokeTestState.ActivationLaunches -ne $launches -or
            -not $global:PackageSmokeTestState.Queries[-1].Disposed) {
            throw 'An invalid, failed or hung usage alias must fail before UI activation and clean its registration.'
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
    Write-Output 'PASS: deterministic repeated/constrained packaged smoke, bounded activation and release, retained failure phases, fail-fast assertions and isolated cleanup.'
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    Remove-Variable -Name PackageSmokeTestState -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
