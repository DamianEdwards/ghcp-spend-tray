param(
    [Parameter(Mandatory)][ValidatePattern('^[^/]+/[^/]+$')][string] $Repository,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $SourceCommit
)
$ErrorActionPreference = 'Stop'
$json = gh api "repos/$Repository/actions/workflows/verify.yml/runs?head_sha=$SourceCommit&branch=main&per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Could not read verification runs.' }
$run = ($json | ConvertFrom-Json).workflow_runs |
    Where-Object { $_.head_sha -eq $SourceCommit -and $_.head_branch -eq 'main' -and $_.event -in @('push', 'workflow_dispatch') } |
    Sort-Object id -Descending | Select-Object -First 1
if (-not $run -or $run.status -ne 'completed' -or $run.conclusion -ne 'success') {
    throw 'The latest Verify run for this exact main commit must succeed before release.'
}
$json = gh api "repos/$Repository/actions/runs/$($run.id)/attempts/$($run.run_attempt)/jobs?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Could not read verification jobs.' }
$gates = @(($json | ConvertFrom-Json).jobs | Where-Object { $_.name -eq 'Verification' -and $_.head_sha -eq $SourceCommit })
if ($gates.Count -ne 1 -or $gates[0].status -ne 'completed' -or $gates[0].conclusion -ne 'success') {
    throw 'The Verification gate must pass on the pinned release source.'
}
Write-Output "Verified source $SourceCommit (run $($run.id))."
