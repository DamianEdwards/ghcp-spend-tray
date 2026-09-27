param(
    [Parameter(Mandatory)][guid] $TenantId,
    [Parameter(Mandatory)][guid] $ClientId,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Z0-9]{12}\z')][string] $StoreId,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\z')][string] $SubmissionId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$clientSecret = Read-Host -Prompt 'Partner Center Entra app client secret' -AsSecureString
if ($null -eq $clientSecret -or $clientSecret.Length -eq 0) {
    throw 'A client secret is required to query the Store API locally.'
}
$body = @{
    grant_type = 'client_credentials'
    client_id = $ClientId.ToString()
    client_secret = ConvertFrom-SecureString $clientSecret -AsPlainText
    resource = 'https://manage.devcenter.microsoft.com'
}
try {
    $token = Invoke-RestMethod -Method Post `
        -Uri "https://login.microsoftonline.com/$TenantId/oauth2/token" `
        -ContentType 'application/x-www-form-urlencoded' -Body $body
}
finally {
    $body.Remove('client_secret')
    $clientSecret.Dispose()
}
if ([string]::IsNullOrWhiteSpace($token.access_token)) {
    throw 'Microsoft Entra did not return a Store API access token.'
}

$uri = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$StoreId/submissions/$SubmissionId/status"
$response = Invoke-RestMethod -Uri $uri -Headers @{ Authorization = "Bearer $($token.access_token)" }
if ([string]::IsNullOrWhiteSpace($response.status) -or
    $null -eq $response.PSObject.Properties['statusDetails']) {
    throw 'The Store API did not return a submission status and details.'
}
[pscustomobject]@{
    SubmissionId = $SubmissionId
    Status = $response.status
    StatusDetails = $response.statusDetails
} | ConvertTo-Json -Depth 10
