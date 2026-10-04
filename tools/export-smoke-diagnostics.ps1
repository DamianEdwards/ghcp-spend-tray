param(
    [Parameter(Mandatory = $true)][string] $DataDirectory,
    [Parameter(Mandatory = $true)][string] $Destination
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($relative in @('native-smoke-result.txt', 'native-smoke-progress.txt',
    'logs\diagnostics.log', 'logs\diagnostics.previous.log')) {
    $source = Join-Path $DataDirectory $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $target = Join-Path $Destination $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
}
$progress = Join-Path $Destination 'native-smoke-progress.txt'
if (Test-Path -LiteralPath $progress -PathType Leaf) {
    Write-Output "Last recorded smoke phase: $(Get-Content -LiteralPath $progress | Select-Object -Last 1)"
} else {
    Write-Output 'No native smoke progress was recorded; startup may not have reached the managed entry point.'
}
