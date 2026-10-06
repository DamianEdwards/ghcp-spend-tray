$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type @'
public static class SyntheticPackageSmokeActivation {
    public static uint Launch(string id, string arguments) { return 42; }
}
'@
$root = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-package-smoke-tests-" + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = $root
    $layout = Join-Path $root 'layout'
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Value '<Package><Identity Name="GHCPSpendTray.Development" /></Package>'
    $script = Join-Path $root 'smoke-test-package.ps1'
    (Get-Content -LiteralPath "$PSScriptRoot\smoke-test-package.ps1" -Raw).Replace(
        'PackageSmokeActivation', 'SyntheticPackageSmokeActivation') | Set-Content -LiteralPath $script
    Copy-Item -LiteralPath "$PSScriptRoot\export-smoke-diagnostics.ps1" -Destination $root
    $global:PackageSmokeTestState = [pscustomobject]@{
        Registered = $false; Launches = 0; FailAt = 0; FailKind = 'assertion'; StopCalls = 0; Layout = $layout
        Processes = [Collections.Generic.List[object]]::new()
    }
    function Add-Type { }
    function Add-AppxPackage { $global:PackageSmokeTestState.Registered = $true }
    function Remove-AppxPackage { $global:PackageSmokeTestState.Registered = $false }
    function Stop-Process { $global:PackageSmokeTestState.StopCalls++ }
    function Get-AppxPackage {
        if ($global:PackageSmokeTestState.Registered) {
            [pscustomobject]@{
                PackageFamilyName = 'Synthetic'; PackageFullName = 'Synthetic'
                InstallLocation = $global:PackageSmokeTestState.Layout
            }
        }
    }
    function Get-Process {
        $global:PackageSmokeTestState.Launches++
        $process = [pscustomobject]@{
            Id = 42; Handle = 1; ExitCode = 0; HasExited = $true
            ProcessorAffinity = [IntPtr]6; PriorityClass = [Diagnostics.ProcessPriorityClass]::Normal
        }
        $process | Add-Member ScriptMethod WaitForExit {
            param($milliseconds)
            if ($milliseconds -ne 45000) { throw 'The packaged process limit changed.' }
            $data = Join-Path $env:LOCALAPPDATA 'Packages\Synthetic\LocalState\Data'
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            $failed = $global:PackageSmokeTestState.Launches -eq $global:PackageSmokeTestState.FailAt
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'timeout') { return $false }
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'missing') { return $true }
            if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'exit') { $this.ExitCode = 1 }
            $result = if ($failed -and $global:PackageSmokeTestState.FailKind -eq 'assertion') {
                'FAIL: synthetic assertion'
            } else { 'PASS: synthetic' }
            Set-Content -LiteralPath (Join-Path $data 'native-smoke-result.txt') -Value $result
            Set-Content -LiteralPath (Join-Path $data 'native-smoke-progress.txt') -Value 'synthetic readiness phase'
            return $true
        }
        $process | Add-Member ScriptMethod Dispose { }
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
            if (-not (Test-Path -LiteralPath (Join-Path $diagnostics "iteration-$iteration\$scenario\native-smoke-result.txt"))) {
                throw 'Each repeated scenario must retain distinct diagnostics.'
            }
        }
    }
    foreach ($failure in @(
        @{ kind = 'assertion'; message = 'FAIL: synthetic assertion' },
        @{ kind = 'timeout'; message = 'timed out' },
        @{ kind = 'missing'; message = 'No packaged smoke result' },
        @{ kind = 'exit'; message = 'process failed' }
    )) {
        $global:PackageSmokeTestState.Launches = 0
        $global:PackageSmokeTestState.FailAt = 2
        $global:PackageSmokeTestState.FailKind = $failure.kind
        $rejected = $false
        try { & $script -Layout $layout -Iterations 3 | Out-Null }
        catch {
            if ($_.Exception.Message -notmatch $failure.message) { throw }
            $rejected = $true
        }
        if (-not $rejected -or $global:PackageSmokeTestState.Launches -ne 2 -or $global:PackageSmokeTestState.Registered) {
            throw 'A failed observation must stop immediately without retrying or retaining registration.'
        }
    }
    if ($global:PackageSmokeTestState.StopCalls -ne 1) { throw 'Timeout must terminate only the owned process.' }
    $global:PackageSmokeTestState.Registered = $true
    $rejected = $false
    try { & $script -Layout $layout | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch 'already registered') { throw }
        $rejected = $true
    }
    if (-not $rejected -or -not $global:PackageSmokeTestState.Registered -or $global:PackageSmokeTestState.Launches -ne 2) {
        throw 'Existing development registrations must remain untouched.'
    }
    Write-Output 'PASS: repeated/constrained packaged smoke, separate diagnostics, fail-fast assertions and registration isolation.'
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    Remove-Variable -Name PackageSmokeTestState -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
