param(
    [string] $Version,
    [ValidatePattern('\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z')]
    [string] $Repository = 'DamianEdwards/ghcp-spend-tray'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-GitHubJson([string] $Endpoint, [switch] $Asset) {
    $arguments = @('api', $Endpoint)
    if ($Asset) { $arguments += @('-H', 'Accept: application/octet-stream') }
    $json = & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not read GitHub release data: $Endpoint" }
    $json | ConvertFrom-Json
}

$latest = $null
if ([string]::IsNullOrEmpty($Version)) {
    $latest = Read-GitHubJson "repos/$Repository/releases/latest"
    if ($null -eq $latest -or $null -eq $latest.PSObject.Properties['tag_name'] -or
        [string]$latest.tag_name -cnotmatch '\Av.+\z' -or
        $null -eq $latest.PSObject.Properties['prerelease'] -or $latest.prerelease -ne $false) {
        throw 'The latest GitHub release must be a non-prerelease with a v-prefixed version tag.'
    }
    $Version = $latest.tag_name.Substring(1)
}
$versionInfo = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
$release = if ($null -ne $latest) {
    $latest
} else {
    Read-GitHubJson "repos/$Repository/releases/tags/$($versionInfo.Tag)"
}
if ($release.tag_name -cne $versionInfo.Tag -or $release.draft -ne $false -or $release.immutable -ne $true) {
    throw 'Store packages must be built from an existing published, immutable GitHub release.'
}
$metadataAssets = @($release.assets | Where-Object name -ceq 'release.json')
$bundleAssets = @($release.assets | Where-Object name -ceq "GHCPSpendTray-$Version.msixbundle")
if ($metadataAssets.Count -ne 1 -or $bundleAssets.Count -ne 1) {
    throw 'The release must contain its source metadata and GitHub MSIX bundle.'
}
if ([string]$metadataAssets[0].id -notmatch '\A[0-9]+\z') { throw 'Invalid release metadata asset ID.' }
$metadata = Read-GitHubJson "repos/$Repository/releases/assets/$($metadataAssets[0].id)" -Asset
$commit = Read-GitHubJson "repos/$Repository/commits/$($versionInfo.Tag)"
if ($metadata.sourceCommit -cnotmatch '\A[0-9a-f]{40}\z' -or $metadata.sourceCommit -cne $commit.sha -or
    $metadata.version -cne $Version -or $metadata.packageVersion -cne $versionInfo.PackageVersion -or
    $metadata.signed -ne $true) {
    throw 'The release metadata must match its immutable tag, version and signed GitHub distribution.'
}
$storeReleaseNotes = & "$PSScriptRoot\format-store-release-notes.ps1" -Body $release.body -Version $Version
[pscustomobject]@{
    Version = $versionInfo.Version
    PackageVersion = $versionInfo.PackageVersion
    Tag = $versionInfo.Tag
    SourceCommit = $commit.sha
    ReleaseUrl = "https://github.com/$Repository/releases/tag/$($versionInfo.Tag)"
    StoreReleaseNotes = $storeReleaseNotes
}
