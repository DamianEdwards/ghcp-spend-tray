$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("GHCPSpendTray-smoke-diagnostics-" + [Guid]::NewGuid().ToString('N'))
try {
    $data = Join-Path $root 'data'
    $logs = Join-Path $data 'logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'config.json') -Value 'synthetic account settings must not be copied'
    Set-Content -LiteralPath (Join-Path $data 'native-smoke-progress.txt') -Value @('phase one', 'phase two')
    Set-Content -LiteralPath (Join-Path $logs 'diagnostics.log') -Value 'synthetic diagnostic'
    $timeout = Join-Path $root 'timeout'
    $message = & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory $data -Destination $timeout
    if ($message -ne 'Last recorded smoke phase: phase two' -or
        -not (Test-Path -LiteralPath (Join-Path $timeout 'native-smoke-progress.txt')) -or
        -not (Test-Path -LiteralPath (Join-Path $timeout 'logs\diagnostics.log')) -or
        (Test-Path -LiteralPath (Join-Path $timeout 'config.json'))) {
        throw 'Timeout diagnostics must retain the last phase and safe logs, not account settings.'
    }
    Set-Content -LiteralPath (Join-Path $data 'native-smoke-result.txt') -Value 'FAIL: synthetic assertion'
    Set-Content -LiteralPath (Join-Path $logs 'diagnostics.previous.log') -Value 'synthetic previous diagnostic'
    $failure = Join-Path $root 'failure'
    & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory $data -Destination $failure | Out-Null
    if ((Get-Content -LiteralPath (Join-Path $failure 'native-smoke-result.txt') -Raw).Trim() -ne 'FAIL: synthetic assertion' -or
        -not (Test-Path -LiteralPath (Join-Path $failure 'logs\diagnostics.previous.log'))) {
        throw 'Assertion failures must retain the native result and rotated diagnostics.'
    }
    $startup = Join-Path $root 'startup'
    $message = & "$PSScriptRoot\export-smoke-diagnostics.ps1" -DataDirectory (Join-Path $root 'missing') -Destination $startup
    if ($message -ne 'No native smoke progress was recorded; startup may not have reached the managed entry point.') {
        throw 'Missing startup diagnostics must be explicit, not reported as success.'
    }
    Write-Output 'PASS: smoke timeout/assertion/startup diagnostics preserve only explicit synthetic diagnostic files.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
