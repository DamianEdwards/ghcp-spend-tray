param(
    [string] $Version = '0.1.0',
    [string] $IdentityName = 'GHCPSpendTray.Development',
    [string] $Publisher = 'CN=GHCPSpendTray Development',
    [string] $PublisherDisplayName = 'GHCPSpendTray',
    [switch] $SkipPublish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$release = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
if ($IdentityName -notmatch '\A[A-Za-z0-9.-]{3,50}\z' -or $IdentityName -match '^(Microsoft|Windows)\.') {
    throw 'Supply a valid MSIX identity Name (3-50 letters, digits, periods or hyphens).'
}
if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'Publisher and PublisherDisplayName are required.'
}
$makeappx = & "$PSScriptRoot\get-windows-sdk-tool.ps1" -Name makeappx.exe
if (-not $SkipPublish) { & "$PSScriptRoot\publish.ps1" -Version $Version }
$packageRoot = Join-Path $root 'artifacts\msix'
$bundleRoot = Join-Path $root 'artifacts\release'
foreach ($directory in @($packageRoot, $bundleRoot)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$packages = Join-Path $packageRoot 'packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
foreach ($architecture in @('x64', 'arm64')) {
    $payload = Join-Path $root "artifacts\publish\win-$architecture"
    $exe = Join-Path $payload 'GHCPSpendTray.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing published payload: $exe" }
    if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne $release.PackageVersion) {
        throw "Payload version does not match $($release.PackageVersion). Publish before packaging."
    }
    $layout = Join-Path $packageRoot $architecture
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    Get-ChildItem -LiteralPath $payload | Copy-Item -Destination $layout -Recurse
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw
    $manifest.Package.Identity.Name = $IdentityName
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Identity.Version = $release.PackageVersion
    $manifest.Package.Identity.ProcessorArchitecture = $architecture
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $manifest.Save((Join-Path $layout 'AppxManifest.xml'))
    $package = Join-Path $packages "GHCPSpendTray-$Version-$architecture.msix"
    & $makeappx pack /d $layout /p $package /o | Select-Object -Last 5
    if ($LASTEXITCODE -ne 0) { throw "MSIX validation/packaging failed for $architecture." }
}
$bundle = Join-Path $bundleRoot "GHCPSpendTray-$Version.msixbundle"
& $makeappx bundle /d $packages /p $bundle /bv $release.PackageVersion /o | Select-Object -Last 5
if ($LASTEXITCODE -ne 0) { throw 'MSIX bundle creation failed.' }
& "$PSScriptRoot\test-package.ps1" -Bundle $bundle -Version $Version -IdentityName $IdentityName -Publisher $Publisher
Write-Output "Unsigned bundle (sign before distribution): $bundle"
