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
Write-Output 'PASS: release version boundaries, development identity and PowerShell syntax.'
