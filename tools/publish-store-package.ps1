param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Z0-9]{12}\z')][string] $StoreId,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [switch] $ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$versionInfo = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
$bundlePath = (Resolve-Path -LiteralPath $Bundle -ErrorAction Stop).Path
if ((Split-Path $bundlePath -Leaf) -cne "GHCPSpendTray-$Version-store.msixbundle") {
    throw 'Publish only the validated, staged Store bundle for this version.'
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
if ($null -ne $app.pendingApplicationSubmission) {
    throw "Product $StoreId has a pending submission. Resolve it in Partner Center before publishing; no draft was changed."
}
if ($null -eq $app.lastPublishedApplicationSubmission -or
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
if ($ValidateOnly) {
    Write-Output "Federated Store API access and package version verified for $StoreId; no submission created."
    return
}

# A new submission copies listing, availability, and other settings from the published one.
# Never delete or reuse a pending submission: an interrupted run stays available for inspection.
$draft = Invoke-RestMethod -Method Post -Uri "$url/submissions" -Headers $headers
if ([string]$draft.id -notmatch '\A[0-9]+\z' -or $draft.status -cne 'PendingCommit' -or
    [string]::IsNullOrWhiteSpace($draft.fileUploadUrl)) {
    throw 'Partner Center did not return a usable new draft; inspect it before retrying.'
}
$draftId = $draft.id
$uploadUrl = $draft.fileUploadUrl
Write-Output "Created Store submission $draftId for $StoreId."
$zip = Join-Path ([IO.Path]::GetTempPath()) "ghcpspendtray-store-$([guid]::NewGuid().ToString('N')).zip"
try {
    $fileName = Split-Path $bundlePath -Leaf
    $draft.applicationPackages = @(@{
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
        @($updated.applicationPackages).Count -ne 1 -or
        $updated.applicationPackages[0].fileName -cne $fileName) {
        throw "Submission $draftId did not retain the requested package; inspect the draft."
    }
    Compress-Archive -LiteralPath $bundlePath -DestinationPath $zip -CompressionLevel NoCompression
    try {
        Invoke-WebRequest -Method Put -Uri $uploadUrl -InFile $zip `
            -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
    }
    catch {
        throw "Store package upload failed for submission $draftId ($($_.Exception.GetType().Name)); inspect the draft before retrying."
    }
    $current = Invoke-RestMethod -Uri $url -Headers $headers
    if ($null -eq $current.pendingApplicationSubmission -or
        [string]$current.pendingApplicationSubmission.id -cne [string]$draftId) {
        throw "Pending submission changed while uploading; inspect submission $draftId."
    }
    $ready = Invoke-RestMethod -Uri "$url/submissions/$draftId" -Headers $headers
    if ($ready.status -cne 'PendingCommit' -or @($ready.applicationPackages).Count -ne 1 -or
        $ready.applicationPackages[0].fileName -cne $fileName) {
        throw "Store draft $draftId changed while uploading; inspect it before committing."
    }
    $commit = Invoke-RestMethod -Method Post -Uri "$url/submissions/$draftId/commit" -Headers $headers
    if ($null -eq $commit -or $commit.status -cne 'CommitStarted') {
        throw "Store submission $draftId did not start committing; inspect Partner Center."
    }
    Write-Output "Store submission $draftId committed for certification; publication is subject to Store review."
}
finally {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
}
