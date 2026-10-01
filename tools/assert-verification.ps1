param([Parameter(Mandatory)][string] $NeedsJson)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$needs = $NeedsJson | ConvertFrom-Json
if ($needs.changes.result -cne 'success') {
    throw "Change detection/Markdown checks did not succeed: $($needs.changes.result)"
}
$expected = switch -CaseSensitive ($needs.changes.outputs.run_validation) {
    'true' { 'success' }
    'false' { 'skipped' }
    default { throw 'Missing or invalid source-change decision.' }
}
foreach ($job in @('tests', 'package')) {
    if ($needs.$job.result -cne $expected) {
        throw "Verification job '$job' must be '$expected', but was '$($needs.$job.result)'."
    }
}
Write-Output 'PASS: all applicable verification jobs succeeded.'
