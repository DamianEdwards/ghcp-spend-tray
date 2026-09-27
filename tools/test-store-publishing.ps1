$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publisher = Join-Path $PSScriptRoot 'publish-store-package.ps1'
$storeId = '9ABCDEFGH123'
$identity = 'Test.StoreIdentity'
$subject = 'CN=12345678-1234-1234-1234-123456789abc'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) "ghcpspendtray-store-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$bundle = Join-Path $testDirectory 'GHCPSpendTray-0.2.0-store.msixbundle'
Set-Content -LiteralPath $bundle -Value 'Synthetic package; not an installable bundle.' -Encoding ascii
$notesFile = Join-Path $testDirectory 'release-notes.txt'
$notes = "What's new in 0.2.0`n- Improve tray behavior"
Set-Content -LiteralPath $notesFile -Value $notes -Encoding utf8NoBOM -NoNewline
$state = @{ Pending = $false; OmitPending = $true; OmitLastPublished = $false
    LocalizedListing = $false; ChangedNotes = $false
    LastVersion = '0.1.0.0'; LastStatus = 'Published'; ChangedDraft = $false
    FailUpload = $false; TokenFailure = $false; Created = 0; Updated = 0; Uploaded = 0; Committed = 0
    SubmittedNotes = $null }
Set-Item Function:\az -Value ({
    if (($args -join ' ') -ne 'account get-access-token --resource https://manage.devcenter.microsoft.com --query accessToken --output tsv') {
        throw 'Unexpected Azure CLI request.'
    }
    if ($state.TokenFailure) {
        $global:LASTEXITCODE = 1
        return
    }
    $global:LASTEXITCODE = 0
    'synthetic-token'
}.GetNewClosure())
Set-Item Function:\Invoke-RestMethod -Value ({
    param($Method = 'Get', $Uri, $Headers, $Body, $ContentType)
    if ($Method -in @('Post', 'Put') -and $ContentType -ne 'application/json') {
        throw 'Store API writes require JSON content type.'
    }
    if ($Method -eq 'Post' -and $null -ne $Body) {
        throw 'Store submission creation and commit must not send a request body.'
    }
    if ($Headers.Authorization -ne 'Bearer synthetic-token') { throw 'Missing Store API authorization.' }
    $base = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$storeId"
    if ($Uri -eq $base -and $Method -eq 'Get') {
        $app = [pscustomobject]@{ id = $storeId; packageIdentityName = $identity; publisherName = $subject
            pendingApplicationSubmission = $(if ($state.Pending) { [pscustomobject]@{ id = '22' } } else { $null })
            lastPublishedApplicationSubmission = [pscustomobject]@{ id = '11' } }
        if ($state.OmitPending -and -not $state.Pending) {
            $app.PSObject.Properties.Remove('pendingApplicationSubmission')
        }
        if ($state.OmitLastPublished) {
            $app.PSObject.Properties.Remove('lastPublishedApplicationSubmission')
        }
        return $app
    }
    if ($Uri -eq "$base/submissions/11" -and $Method -eq 'Get') {
        $listings = [pscustomobject]@{ 'en-us' = [pscustomobject]@{
            baseListing = [pscustomobject]@{ title = 'Keep this listing' } } }
        if ($state.LocalizedListing) {
            $listings | Add-Member -NotePropertyName 'fr-fr' -NotePropertyValue ([pscustomobject]@{
                baseListing = [pscustomobject]@{ title = 'Localized listing' } })
        }
        return [pscustomobject]@{ status = $state.LastStatus
            applicationPackages = @([pscustomobject]@{ version = $state.LastVersion })
            listings = $listings }
    }
    if ($Uri -eq "$base/submissions/22" -and $Method -eq 'Get') {
        return [pscustomobject]@{ status = 'PendingCommit'
            applicationPackages = @([pscustomobject]@{
                fileName = $(if ($state.ChangedDraft) { 'different.msixbundle' }
                    else { 'GHCPSpendTray-0.2.0-store.msixbundle' }) })
            listings = [pscustomobject]@{ 'en-us' = [pscustomobject]@{
                baseListing = [pscustomobject]@{
                    releaseNotes = $(if ($state.ChangedNotes) { 'Stale release notes' }
                        else { $state.SubmittedNotes }) } } } }
    }
    if ($Uri -eq "$base/submissions" -and $Method -eq 'Post') {
        $state.Created++
        $state.Pending = $true
        return [pscustomobject]@{ id = '22'; status = 'PendingCommit'; fileUploadUrl = 'https://blob.example.test/upload'
            applicationPackages = @([pscustomobject]@{ fileName = 'old.msixbundle' })
            listings = [pscustomobject]@{ 'en-us' = [pscustomobject]@{
                baseListing = [pscustomobject]@{ title = 'Keep this listing' } } }
            visibility = 'Public'; targetPublishMode = 'Manual' }
    }
    if ($Uri -eq "$base/submissions/22" -and $Method -eq 'Put') {
        $state.Updated++
        $data = $Body | ConvertFrom-Json
        if ($data.PSObject.Properties.Name -contains 'fileUploadUrl' -or
            $data.listings.'en-us'.baseListing.title -cne 'Keep this listing' -or
            $data.listings.'en-us'.baseListing.releaseNotes -cne $notes -or
            $data.visibility -cne 'Public' -or $data.targetPublishMode -cne 'Immediate' -or
            @($data.applicationPackages).Count -ne 1 -or
            $data.applicationPackages[0].fileStatus -cne 'PendingUpload') {
            throw 'Submission metadata or package list changed unexpectedly.'
        }
        $state.SubmittedNotes = $data.listings.'en-us'.baseListing.releaseNotes
        return [pscustomobject]@{ id = '22'; status = 'PendingCommit'
            applicationPackages = @([pscustomobject]@{ fileName = $data.applicationPackages[0].fileName })
            listings = [pscustomobject]@{ 'en-us' = [pscustomobject]@{
                baseListing = [pscustomobject]@{ releaseNotes = $state.SubmittedNotes } } } }
    }
    if ($Uri -eq "$base/submissions/22/commit" -and $Method -eq 'Post') {
        $state.Committed++
        return [pscustomobject]@{ status = 'CommitStarted' }
    }
    throw "Unexpected Store API request: $Method $Uri"
}.GetNewClosure())
Set-Item Function:\Invoke-WebRequest -Value ({
    param($Method, $Uri, $InFile, $Headers)
    if ($Method -ne 'Put' -or $Uri -ne 'https://blob.example.test/upload' -or
        $Headers['x-ms-blob-type'] -ne 'BlockBlob') { throw 'Unexpected Store upload.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($InFile)
    try {
        if ($null -eq $archive.GetEntry('GHCPSpendTray-0.2.0-store.msixbundle')) {
            throw 'Store upload ZIP has no bundle.'
        }
    }
    finally { $archive.Dispose() }
    if ($state.FailUpload) { throw 'Synthetic upload failure.' }
    $state.Uploaded++
    return [pscustomobject]@{ StatusCode = 201 }
}.GetNewClosure())
function Invoke-Fixture([switch] $ValidateOnly) {
    & $publisher -Bundle $bundle -Version '0.2.0' -StoreId $storeId `
        -IdentityName $identity -Publisher $subject -ReleaseNotesFile $notesFile `
        -ValidateOnly:$ValidateOnly | Out-Null
}
function Assert-Rejected([string] $Reason) {
    $before = $state.Created
    $rejected = $false
    try { Invoke-Fixture }
    catch { $rejected = $true }
    if (-not $rejected -or $state.Created -ne $before) { throw "Unsafe Store submission was accepted: $Reason" }
}
try {
    Set-Content -LiteralPath $notesFile -Value "What's new in 0.1.0`n- Old notes" -NoNewline
    Assert-Rejected 'mismatched release notes version'
    $prefix = "What's new in 0.2.0`n"
    Set-Content -LiteralPath $notesFile -Value ($prefix + ('A' * (1501 - $prefix.Length))) -NoNewline
    Assert-Rejected 'oversized release notes'
    Set-Content -LiteralPath $notesFile -Value $notes -NoNewline
    $state.TokenFailure = $true
    Assert-Rejected 'federated login failure'
    $state.TokenFailure = $false
    $state.Pending = $true
    Assert-Rejected 'existing draft'
    $state.Pending = $false
    $state.OmitLastPublished = $true
    Assert-Rejected 'no published submission'
    $state.OmitLastPublished = $false
    $state.LastVersion = '0.2.0.0'
    Assert-Rejected 'same version'
    $state.LastVersion = '0.3.0.0'
    Assert-Rejected 'downgrade'
    $state.LastVersion = '0.1.0.0'
    $state.LastStatus = 'Certification'
    Assert-Rejected 'unpublished predecessor'
    $state.LastStatus = 'Published'
    $state.LocalizedListing = $true
    Assert-Rejected 'untranslated listing'
    $state.LocalizedListing = $false
    Invoke-Fixture -ValidateOnly
    if ($state.Created -ne 0) { throw 'Read-only Store access check created a submission.' }
    $state.FailUpload = $true
    try { Invoke-Fixture; throw 'Upload failure was accepted.' }
    catch {
        if ($_.Exception.Message -eq 'Upload failure was accepted.') { throw }
    }
    if ($state.Created -ne 1 -or $state.Committed -ne 0) { throw 'Upload failure committed or did not preserve the draft.' }
    $state.Pending = $false
    $state.FailUpload = $false
    $state.ChangedDraft = $true
    try { Invoke-Fixture; throw 'Changed draft was committed.' }
    catch {
        if ($_.Exception.Message -eq 'Changed draft was committed.') { throw }
    }
    if ($state.Committed -ne 0) { throw 'Changed Store draft was committed.' }
    $state.Pending = $false
    $state.ChangedDraft = $false
    $state.ChangedNotes = $true
    try { Invoke-Fixture; throw 'Stale release notes were committed.' }
    catch {
        if ($_.Exception.Message -eq 'Stale release notes were committed.') { throw }
    }
    if ($state.Committed -ne 0) { throw 'Stale Store release notes were committed.' }
    $state.Pending = $false
    $state.ChangedNotes = $false
    Invoke-Fixture
    if ($state.Created -ne 4 -or $state.Updated -ne 4 -or
        $state.Uploaded -ne 3 -or $state.Committed -ne 1) {
        throw 'Store update did not create, update, upload and commit exactly once.'
    }
}
finally {
    Remove-Item Function:\az,Function:\Invoke-RestMethod,Function:\Invoke-WebRequest
    $global:LASTEXITCODE = 0
    Remove-Item -LiteralPath $testDirectory -Recurse
}
Write-Output 'PASS: pending drafts, published versions, preserved listings, upload failure and Store commit.'
