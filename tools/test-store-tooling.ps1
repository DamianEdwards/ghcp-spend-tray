$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$selector = Join-Path $PSScriptRoot 'get-store-release.ps1'
$sha = '1234567890123456789012345678901234567890'
$state = @{ ExitCode = 0; Fixture = $null }
function Reset-Fixture {
    $state.Fixture = @{
        release = @{ tag_name = 'v0.1.0'; draft = $false; immutable = $true; prerelease = $true
            assets = @(@{ id = 1; name = 'release.json' }, @{ id = 2; name = 'GHCPSpendTray-0.1.0.msixbundle' }) }
        metadata = @{ version = '0.1.0'; packageVersion = '0.1.0.0'; sourceCommit = $sha; signed = $true }
        commit = @{ sha = $sha }
    }
}
Set-Item Function:\gh -Value ({
    $global:LASTEXITCODE = $state.ExitCode
    switch -Wildcard ($args[1]) {
        '*/releases/tags/*' { $state.Fixture.release | ConvertTo-Json -Depth 8 }
        '*/releases/assets/1' { $state.Fixture.metadata | ConvertTo-Json }
        '*/commits/*' { $state.Fixture.commit | ConvertTo-Json }
        default { throw "Unexpected GitHub request in fixture: $($args[1])" }
    }
}.GetNewClosure())
function Assert-Rejected([scriptblock] $Change) {
    Reset-Fixture
    & $Change
    $rejected = $false
    try { & $selector -Version '0.1.0' | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Unsafe Store release selection was accepted.' }
}
try {
    Reset-Fixture
    $release = & $selector -Version '0.1.0'
    if ($release.SourceCommit -cne $sha -or $release.PackageVersion -ne '0.1.0.0' -or
        $release.ReleaseUrl -ne 'https://github.com/DamianEdwards/ghcp-spend-tray/releases/tag/v0.1.0') {
        throw 'Incorrect Store source selection.'
    }
    Assert-Rejected { $state.Fixture.release.draft = $true }
    Assert-Rejected { $state.Fixture.release.immutable = $false }
    Assert-Rejected { $state.Fixture.release.tag_name = 'v0.2.0' }
    Assert-Rejected { $state.Fixture.release.assets = @($state.Fixture.release.assets[0]) }
    Assert-Rejected { $state.Fixture.release.assets = @($state.Fixture.release.assets[1]) }
    Assert-Rejected { $state.Fixture.release.assets += $state.Fixture.release.assets[0] }
    Assert-Rejected { $state.Fixture.metadata.version = '0.2.0' }
    Assert-Rejected { $state.Fixture.metadata.packageVersion = '0.1.0.1' }
    Assert-Rejected { $state.Fixture.metadata.signed = $false }
    Assert-Rejected { $state.Fixture.commit.sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
    Assert-Rejected { $state.Fixture.metadata.sourceCommit = 'invalid' }
    $state.ExitCode = 1
    Assert-Rejected {}
}
finally {
    Remove-Item Function:\gh
    $global:LASTEXITCODE = 0
}
Write-Output 'PASS: immutable release selection, source/version integrity, metadata presence, previews and API failures.'
