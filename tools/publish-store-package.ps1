param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Z0-9]{12}\z')][string] $StoreId,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [Parameter(Mandatory)][string] $ReleaseNotesFile,
    [switch] $ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$versionInfo = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
$bundlePath = (Resolve-Path -LiteralPath $Bundle -ErrorAction Stop).Path
if ((Split-Path $bundlePath -Leaf) -cne "GHCPSpendTray-$Version-store.msixbundle") {
    throw 'Publish only the validated, staged Store bundle for this version.'
}
$releaseNotes = Get-Content -LiteralPath $ReleaseNotesFile -Raw -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace($releaseNotes) -or $releaseNotes.Length -gt 1500 -or
    -not $releaseNotes.StartsWith("What's new in $Version`n", [StringComparison]::Ordinal)) {
    throw 'Store release notes must be nonempty, version-matched, and at most 1500 characters.'
}
function Get-OnlyEnglishListing([object] $Submission) {
    if ($null -eq $Submission.PSObject.Properties['listings'] -or
        $null -eq $Submission.listings) {
        throw 'The Store submission has no listings; release notes cannot be updated.'
    }
    $locales = @($Submission.listings.PSObject.Properties.Name)
    if ($locales.Count -ne 1 -or $locales[0] -ine 'en-us' -or
        $null -eq $Submission.listings.PSObject.Properties[$locales[0]].Value.baseListing) {
        throw 'Store release notes require a single en-us listing; review localized listings manually.'
    }
    $Submission.listings.PSObject.Properties[$locales[0]].Value.baseListing
}
function Assert-RequestedPackages([object] $Submission, [object[]] $PreviousPackages, [string] $FileName) {
    $packages = @($Submission.applicationPackages)
    if ($packages.Count -ne $PreviousPackages.Count + 1 -or
        @($packages | Where-Object {
            $_.fileName -ceq $FileName -and $_.fileStatus -in @('PendingUpload', 'Uploaded')
        }).Count -ne 1) {
        throw 'The Store did not retain the requested package set.'
    }
    foreach ($previous in $PreviousPackages) {
        if (@($packages | Where-Object {
            $_.fileName -ceq $previous.fileName -and $_.fileStatus -ceq 'PendingDelete'
        }).Count -ne 1) {
            throw "The Store did not retain the removal of package $($previous.fileName)."
        }
    }
}
$accessToken = & az account get-access-token --resource 'https://manage.devcenter.microsoft.com' `
    --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw 'Could not obtain a Store API access token from the federated Store identity.'
}
$headers = @{ Authorization = "Bearer $accessToken" }
$url = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$StoreId"
$app = Invoke-RestMethod -Uri $url -Headers $headers
if ($app.id -cne $StoreId -or $app.packageIdentityName -cne $IdentityName -or
    $app.publisherName -cne $Publisher) {
    throw 'Partner Center product identity differs from the validated Store package.'
}
if ($null -ne $app.PSObject.Properties['pendingApplicationSubmission'] -and
    $null -ne $app.pendingApplicationSubmission) {
    throw "Product $StoreId has a pending submission. Resolve it in Partner Center before publishing; no draft was changed."
}
if ($null -eq $app.PSObject.Properties['lastPublishedApplicationSubmission'] -or
    $null -eq $app.lastPublishedApplicationSubmission -or
    [string]$app.lastPublishedApplicationSubmission.id -notmatch '\A[0-9]+\z') {
    throw 'The Store product needs a published submission before automated updates.'
}
$lastId = $app.lastPublishedApplicationSubmission.id
$last = Invoke-RestMethod -Uri "$url/submissions/$lastId" -Headers $headers
if ($last.status -cne 'Published' -or $null -eq $last.applicationPackages -or
    @($last.applicationPackages).Count -eq 0) {
    throw 'The previous Store submission must be published and contain packages.'
}
foreach ($package in $last.applicationPackages) {
    if ([string]$package.version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z' -or
        [version]$package.version -ge [version]$versionInfo.PackageVersion) {
        throw "Store package version must exceed every published package version ($($package.version))."
    }
}
Get-OnlyEnglishListing $last | Out-Null
if ($ValidateOnly) {
    Write-Output "Federated Store API access, package version and release notes verified for $StoreId; no submission created."
    return
}

# A new submission copies listing, availability, and other settings from the published one.
# Never delete or reuse a pending submission: an interrupted run stays available for inspection.
$draft = Invoke-RestMethod -Method Post -Uri "$url/submissions" -Headers $headers `
    -ContentType 'application/json'
if ([string]$draft.id -notmatch '\A[0-9]+\z' -or $draft.status -cne 'PendingCommit' -or
    [string]::IsNullOrWhiteSpace($draft.fileUploadUrl)) {
    throw 'Partner Center did not return a usable new draft; inspect it before retrying.'
}
$draftId = $draft.id
$uploadUrl = $draft.fileUploadUrl
Write-Output "Created Store submission $draftId for $StoreId."
$zip = Join-Path ([IO.Path]::GetTempPath()) "ghcpspendtray-store-$([guid]::NewGuid().ToString('N')).zip"
try {
    $baseListing = Get-OnlyEnglishListing $draft
    if ($null -eq $baseListing.PSObject.Properties['releaseNotes']) {
        $baseListing | Add-Member -NotePropertyName releaseNotes -NotePropertyValue $releaseNotes
    } else {
        $baseListing.releaseNotes = $releaseNotes
    }
    $fileName = Split-Path $bundlePath -Leaf
    $previousPackages = @($draft.applicationPackages)
    if ($previousPackages.Count -ne @($last.applicationPackages).Count -or
        @($previousPackages | Where-Object {
            $_.fileStatus -cne 'Uploaded' -or
            [string]::IsNullOrWhiteSpace($_.fileName) -or
            @($last.applicationPackages | Where-Object fileName -CEQ $_.fileName).Count -ne 1
        }).Count -ne 0) {
        throw "Store draft $draftId does not contain the published packages; inspect it before updating."
    }
    foreach ($package in $previousPackages) {
        $package.fileStatus = 'PendingDelete'
    }
    $draft.applicationPackages = $previousPackages + @(@{
        fileName = $fileName
        fileStatus = 'PendingUpload'
        minimumDirectXVersion = 'None'
        minimumSystemRam = 'None'
    })
    $draft.targetPublishMode = 'Immediate'
    foreach ($readOnly in @('id', 'status', 'statusDetails', 'fileUploadUrl', 'friendlyName')) {
        $draft.PSObject.Properties.Remove($readOnly)
    }
    $updated = Invoke-RestMethod -Method Put -Uri "$url/submissions/$draftId" -Headers $headers `
        -ContentType 'application/json' -Body ($draft | ConvertTo-Json -Depth 100 -Compress)
    if ([string]$updated.id -cne [string]$draftId -or $updated.status -cne 'PendingCommit' -or
        (Get-OnlyEnglishListing $updated).releaseNotes -cne $releaseNotes) {
        throw "Submission $draftId did not retain the requested package and release notes; inspect the draft."
    }
    Assert-RequestedPackages $updated $previousPackages $fileName
    Compress-Archive -LiteralPath $bundlePath -DestinationPath $zip -CompressionLevel NoCompression
    try {
        Invoke-WebRequest -Method Put -Uri $uploadUrl -InFile $zip `
            -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
    }
    catch {
        throw "Store package upload failed for submission $draftId ($($_.Exception.GetType().Name)); inspect the draft before retrying."
    }
    $current = Invoke-RestMethod -Uri $url -Headers $headers
    if ($null -eq $current.PSObject.Properties['pendingApplicationSubmission'] -or
        $null -eq $current.pendingApplicationSubmission -or
        [string]$current.pendingApplicationSubmission.id -cne [string]$draftId) {
        throw "Pending submission changed while uploading; inspect submission $draftId."
    }
    $ready = Invoke-RestMethod -Uri "$url/submissions/$draftId" -Headers $headers
    if ($ready.status -cne 'PendingCommit' -or
        (Get-OnlyEnglishListing $ready).releaseNotes -cne $releaseNotes) {
        throw "Store draft $draftId changed while uploading; inspect it before committing."
    }
    Assert-RequestedPackages $ready $previousPackages $fileName
    $commit = Invoke-RestMethod -Method Post -Uri "$url/submissions/$draftId/commit" -Headers $headers `
        -ContentType 'application/json'
    if ($null -eq $commit -or $commit.status -cne 'CommitStarted') {
        throw "Store submission $draftId did not start committing; inspect Partner Center."
    }
    Write-Output "Store submission $draftId committed for certification; publication is subject to Store review."
}
finally {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
}
