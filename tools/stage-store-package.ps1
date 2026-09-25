param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string] $SourceCommit,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [Parameter(Mandatory)][string] $PublisherDisplayName,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Z0-9]{12}\z')][string] $StoreId,
    [ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
    [string] $Repository = 'DamianEdwards/ghcp-spend-tray'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$versionInfo = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
if ($IdentityName -eq 'GHCPSpendTray.Development' -or
    $Publisher -notmatch '\ACN=[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z' -or
    [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'Use the assigned Partner Center identity and publisher, not the development or Azure signing identity.'
}
& "$PSScriptRoot\test-package.ps1" -Bundle $Bundle -Version $Version -IdentityName $IdentityName `
    -Publisher $Publisher -PublisherDisplayName $PublisherDisplayName
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Bundle).Path)
try {
    if ($null -ne $archive.GetEntry('AppxSignature.p7x')) {
        throw 'Store submission bundles must be unsigned here; do not reuse the Azure-signed GitHub bundle.'
    }
}
finally { $archive.Dispose() }
$output = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\store\$Version"
if (Test-Path -LiteralPath $output) { throw "Store output already exists: $output. Preserve it or remove it explicitly before rebuilding." }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$bundleName = "GHCPSpendTray-$Version-store.msixbundle"
Copy-Item -LiteralPath $Bundle -Destination (Join-Path $output $bundleName)
@{
    version = $Version
    packageVersion = $versionInfo.PackageVersion
    sourceCommit = $SourceCommit
    sourceRelease = "https://github.com/$Repository/releases/tag/$($versionInfo.Tag)"
    storeId = $StoreId
    identityName = $IdentityName
    publisher = $Publisher
    publisherDisplayName = $PublisherDisplayName
    signed = $false
    submittedToStore = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'store-package.json') -Encoding utf8NoBOM
$hashLines = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
})
$hashLines | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding ascii
Write-Output "Store submission bundle: $(Join-Path $output $bundleName)"
Write-Output 'Build only: unsigned for Partner Center upload, not for direct installation. No Store submission was created.'
