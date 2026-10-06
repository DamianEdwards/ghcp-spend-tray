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
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PackageSmokeActivation {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        void ActivateForFile(IntPtr item, IntPtr array, IntPtr verb, out uint processId);
        void ActivateForProtocol(IntPtr item, IntPtr array, out uint processId);
    }
    public static uint Launch(string id, string arguments) {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type);
        try { uint pid; manager.ActivateApplication(id, arguments, 0, out pid); return pid; }
        finally { Marshal.ReleaseComObject(manager); }
    }
}
'@
$registered = $false
$process = $null
try {
    Add-AppxPackage -Register $manifestPath
    $registered = $true
    $package = Get-AppxPackage -Name GHCPSpendTray.Development
    if (-not $package) { throw 'Registration did not produce a development package.' }
    $result = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\Data\native-smoke-result.txt"
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        foreach ($arguments in @('--package-smoke-test', '--package-smoke-test --demo-empty')) {
            if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
            $dataDirectory = Split-Path $result -Parent
            $progress = Join-Path $dataDirectory 'native-smoke-progress.txt'
            if (Test-Path -LiteralPath $progress) { Remove-Item -LiteralPath $progress -Force }
            try {
                $processId = [PackageSmokeActivation]::Launch("$($package.PackageFamilyName)!App", $arguments)
                $process = Get-Process -Id $processId
                # Retain the native handle so ExitCode remains available after a fast startup crash.
                $null = $process.Handle
                if ($Constrained) {
                    # Constrain only this synthetic child, never the runner or another app.
                    $affinity = $process.ProcessorAffinity.ToInt64()
                    $process.ProcessorAffinity = [IntPtr]($affinity -band (-$affinity))
                    $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
                }
                Write-Output "Packaged smoke iteration $iteration/$Iterations ($arguments); constrained=$($Constrained.IsPresent)"
                if (-not $process.WaitForExit(45000)) {
                    Stop-Process -Id $processId
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
                if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
                if ($DiagnosticsDirectory) {
                    $scenario = if ($arguments -match '--demo-empty') { 'empty' } else { 'populated' }
                    if ($Iterations -gt 1) { $scenario = "iteration-$iteration\$scenario" }
                    & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory $dataDirectory `
                        -Destination (Join-Path $DiagnosticsDirectory $scenario)
                }
                if ($null -ne $process) { $process.Dispose(); $process = $null }
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
