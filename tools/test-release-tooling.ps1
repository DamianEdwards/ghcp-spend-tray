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
foreach ($windows in @('true', 'false')) {
    foreach ($macos in @('true', 'false')) {
        foreach ($markdown in @('true', 'false')) {
            foreach ($prompt in @('true', 'false')) {
                $needs = @{
                    changes = @{ result = 'success'; outputs = @{ windows = $windows; macos = $macos; markdown = $markdown; prompt = $prompt } }
                    tests = @{ result = $(if ($windows -eq 'true') { 'success' } else { 'skipped' }) }
                    package = @{ result = $(if ($windows -eq 'true') { 'success' } else { 'skipped' }) }
                    macos_tests = @{ result = $(if ($macos -eq 'true') { 'success' } else { 'skipped' }) }
                    macos = @{ result = $(if ($macos -eq 'true') { 'success' } else { 'skipped' }) }
                    macos_runtime = @{ result = $(if ($macos -eq 'true') { 'success' } else { 'skipped' }) }
                    markdown = @{ result = $(if ($markdown -eq 'true') { 'success' } else { 'skipped' }) }
                    prompt = @{ result = $(if ($prompt -eq 'true') { 'success' } else { 'skipped' }) }
                }
                foreach ($job in @('changes', 'tests', 'package', 'macos_tests', 'macos', 'macos_runtime', 'markdown', 'prompt')) {
                    $original = $needs[$job].result
                    foreach ($result in $results) {
                        $needs[$job].result = $result
                        $accepted = $true
                        try { & $gate -NeedsJson ($needs | ConvertTo-Json -Depth 3) | Out-Null }
                        catch { $accepted = $false }
                        if ($accepted -ne ($result -eq $original)) {
                            throw "Incorrect verification gate: windows=$windows macos=$macos markdown=$markdown prompt=$prompt job=$job result=$result"
                        }
                    }
                    $needs[$job].result = $original
                    $missing = $needs.Clone()
                    $missing.Remove($job)
                    $rejected = $false
                    try { & $gate -NeedsJson ($missing | ConvertTo-Json -Depth 3) | Out-Null }
                    catch { $rejected = $true }
                    if (-not $rejected) { throw "Missing verification job was accepted: $job" }
                }
            }
        }
    }
}
foreach ($needs in @('invalid', '{}',
    '{"changes":{"result":"success","outputs":{"windows":"true","macos":"true","markdown":"true"}}}',
    '{"changes":{"result":"success","outputs":{"windows":"false","macos":"false","markdown":"false"}}}',
    '{"changes":{"result":"success","outputs":{"windows":"","macos":"false","markdown":"false","prompt":"false"}},"tests":{"result":"skipped"},"package":{"result":"skipped"},"macos":{"result":"skipped"},"markdown":{"result":"skipped"},"prompt":{"result":"skipped"}}',
    '{"changes":{"result":"success","outputs":{"windows":"false","macos":"false","markdown":"false","prompt":""}},"tests":{"result":"skipped"},"package":{"result":"skipped"},"macos_tests":{"result":"skipped"},"macos":{"result":"skipped"},"macos_runtime":{"result":"skipped"},"markdown":{"result":"skipped"},"prompt":{"result":"skipped"}}',
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
& "$PSScriptRoot\test-windows-app-runtime.ps1"
& "$PSScriptRoot\test-smoke-diagnostics.ps1"
Write-Output 'PASS: release version boundaries, development identity and PowerShell syntax.'
