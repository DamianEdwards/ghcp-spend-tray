$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$statusScript = Join-Path $PSScriptRoot 'get-store-submission-status.ps1'
$tenantId = [guid]'12345678-1234-1234-1234-123456789abc'
$clientId = [guid]'87654321-4321-4321-4321-cba987654321'
$storeId = '9ABCDEFGH123'
$submissionId = '1234567890'
$state = @{ PostCount = 0; GetCount = 0; MissingToken = $false; MissingStatus = $false }
Set-Item Function:\Read-Host -Value ({
    param($Prompt, [switch] $AsSecureString)
    if (-not $AsSecureString -or $Prompt -notlike '*client secret*') {
        throw 'The secret must be entered using a hidden prompt.'
    }
    ConvertTo-SecureString 'synthetic-client-secret' -AsPlainText -Force
}.GetNewClosure())
Set-Item Function:\Invoke-RestMethod -Value ({
    param($Method = 'Get', $Uri, $ContentType, $Body, $Headers)
    if ($Method -eq 'Post') {
        $state.PostCount++
        if ($Uri -ne "https://login.microsoftonline.com/$tenantId/oauth2/token" -or
            $ContentType -ne 'application/x-www-form-urlencoded' -or
            $Body.grant_type -ne 'client_credentials' -or
            $Body.client_id -ne $clientId.ToString() -or
            $Body.client_secret -ne 'synthetic-client-secret' -or
            $Body.resource -ne 'https://manage.devcenter.microsoft.com') {
            throw 'Incorrect Store token request.'
        }
        if ($state.MissingToken) { return [pscustomobject]@{ access_token = $null } }
        return [pscustomobject]@{ access_token = 'synthetic-access-token' }
    }
    $state.GetCount++
    if ($Method -ne 'Get' -or $null -ne $Body -or
        $Uri -ne "https://manage.devcenter.microsoft.com/v1.0/my/applications/$storeId/submissions/$submissionId/status" -or
        $Headers.Authorization -ne 'Bearer synthetic-access-token') {
        throw 'Status check must only read the requested submission.'
    }
    if ($state.MissingStatus) { return [pscustomobject]@{ status = $null } }
    [pscustomobject]@{ status = 'CommitFailed'; statusDetails = [pscustomobject]@{
        errors = @([pscustomobject]@{ code = 'SyntheticFailure'; details = 'Synthetic rejection' })
        warnings = @() } }
}.GetNewClosure())
function Invoke-Fixture {
    & $statusScript -TenantId $tenantId -ClientId $clientId `
        -StoreId $storeId -SubmissionId $submissionId
}
try {
    $result = (Invoke-Fixture | Out-String) | ConvertFrom-Json
    if ($result.SubmissionId -ne $submissionId -or
        $result.Status -ne 'CommitFailed' -or
        $result.StatusDetails.errors[0].code -ne 'SyntheticFailure' -or
        $state.PostCount -ne 1 -or $state.GetCount -ne 1) {
        throw 'Submission status or failure details were lost.'
    }
    $state.MissingToken = $true
    $rejected = $false
    try { Invoke-Fixture | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or $state.GetCount -ne 1) {
        throw 'Missing Store token was accepted.'
    }
    $state.MissingToken = $false
    $state.MissingStatus = $true
    $rejected = $false
    try { Invoke-Fixture | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or $state.GetCount -ne 2) {
        throw 'Missing Store status was accepted.'
    }
}
finally {
    Remove-Item Function:\Read-Host,Function:\Invoke-RestMethod
}
Write-Output 'PASS: local read-only Store submission status and diagnostics.'
