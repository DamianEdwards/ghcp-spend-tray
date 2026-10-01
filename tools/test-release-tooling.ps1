$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$versionScript = Join-Path $PSScriptRoot 'get-release-version.ps1'
foreach ($version in @('0.1.0', '1.0.0', '65535.65535.65535')) {
    $result = & $versionScript -Version $version
    if ($result.Version -ne $version -or $result.PackageVersion -ne "$version.0" -or $result.Tag -ne "v$version") {
        throw "Incorrect release version mapping: $version"
    }
}
foreach ($version in @('0.0.0', '1.2', '1.2.3.4', 'v1.2.3', '1.2.3-preview.1', '01.2.3', '-1.2.3',
    '65536.0.0', '1.99999999999999999.0', '1.2.3; exit', "1.2.3`n")) {
    $rejected = $false
    try { & $versionScript -Version $version | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid release version was accepted: $version" }
}
$root = Split-Path $PSScriptRoot -Parent
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw
if ($manifest.Package.Identity.Name -cne 'GHCPSpendTray.Development' -or
    $manifest.Package.Applications.Application.Executable -cne 'GHCPSpendTray.exe') {
    throw 'Local packaging must default to the development identity and GHCPSpendTray executable.'
}
$errors = @()
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors)
    $errors += $parseErrors
}
if ($errors.Count) { throw ($errors | Out-String) }
$gate = Join-Path $PSScriptRoot 'assert-verification.ps1'
$results = @('success', 'failure', 'cancelled', 'skipped')
foreach ($runValidation in @('true', 'false', '')) {
    foreach ($changes in $results) {
        foreach ($tests in $results) {
            foreach ($package in $results) {
                $needs = @{
                    changes = @{ result = $changes; outputs = @{ run_validation = $runValidation } }
                    tests = @{ result = $tests }
                    package = @{ result = $package }
                } | ConvertTo-Json -Depth 3
                $expected = $changes -eq 'success' -and (
                    ($runValidation -eq 'true' -and $tests -eq 'success' -and $package -eq 'success') -or
                    ($runValidation -eq 'false' -and $tests -eq 'skipped' -and $package -eq 'skipped'))
                $accepted = $true
                try { & $gate -NeedsJson $needs | Out-Null }
                catch { $accepted = $false }
                if ($accepted -ne $expected) {
                    throw "Incorrect verification gate: validation=$runValidation changes=$changes tests=$tests package=$package"
                }
            }
        }
    }
}
foreach ($needs in @('invalid', '{}',
    '{"changes":{"result":"success","outputs":{"run_validation":"true"}}}',
    '{"changes":{"result":"success","outputs":{"run_validation":"false"}}}',
    '{"changes":{"result":"success","outputs":{}},"tests":{"result":"success"},"package":{"result":"success"}}')) {
    $rejected = $false
    try { & $gate -NeedsJson $needs | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Incomplete verification results were accepted.' }
}
Write-Output 'PASS: verification gate rejects failures, cancellations, unexpected skips and missing results.'
& "$PSScriptRoot\test-store-tooling.ps1"
& "$PSScriptRoot\test-store-publishing.ps1"
& "$PSScriptRoot\test-store-submission-status.ps1"
& "$PSScriptRoot\test-icon-assets.ps1"
& "$PSScriptRoot\test-package-icon-tooling.ps1"
Write-Output 'PASS: release version boundaries, development identity and PowerShell syntax.'
