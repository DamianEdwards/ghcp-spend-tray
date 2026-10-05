param([Parameter(Mandatory)][string] $NeedsJson)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$needs = $NeedsJson | ConvertFrom-Json
if ($needs.changes.result -cne 'success') {
    throw "Change detection did not succeed: $($needs.changes.result)"
}
foreach ($platform in @('windows', 'macos', 'markdown')) {
    $expected = switch -CaseSensitive ($needs.changes.outputs.$platform) {
        'true' { 'success' }
        'false' { 'skipped' }
        default { throw "Missing or invalid $platform change decision." }
    }
    $jobs = switch ($platform) {
        'windows' { @('tests', 'package') }
        'macos' { @('macos_tests', 'macos', 'macos_runtime') }
        default { @($platform) }
    }
    foreach ($job in $jobs) {
        if ($needs.$job.result -cne $expected) {
            throw "Verification job '$job' must be '$expected', but was '$($needs.$job.result)'."
        }
    }
}
Write-Output 'PASS: all applicable verification jobs succeeded.'
