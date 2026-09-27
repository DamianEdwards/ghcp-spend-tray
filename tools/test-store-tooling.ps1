$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$selector = Join-Path $PSScriptRoot 'get-store-release.ps1'
$sha = '1234567890123456789012345678901234567890'
$state = @{ ExitCode = 0; Fixture = $null; Requests = @() }
function Reset-Fixture {
    $state.Requests = @()
    $state.Fixture = @{
        release = @{ tag_name = 'v0.1.0'; draft = $false; immutable = $true; prerelease = $false
            body = "## What's Changed`n* Improve tray behavior by @fixture in https://github.com/DamianEdwards/ghcp-spend-tray/pull/1`n* Update packages by @dependabot[bot] in https://github.com/DamianEdwards/ghcp-spend-tray/pull/2`n`n## New Contributors`n* @dependabot[bot]`n**Full Changelog**: https://github.com/DamianEdwards/ghcp-spend-tray/compare/v0.0.1...v0.1.0"
            assets = @(@{ id = 1; name = 'release.json' }, @{ id = 2; name = 'GHCPSpendTray-0.1.0.msixbundle' }) }
        metadata = @{ version = '0.1.0'; packageVersion = '0.1.0.0'; sourceCommit = $sha; signed = $true }
        commit = @{ sha = $sha }
    }
}
Set-Item Function:\gh -Value ({
    $global:LASTEXITCODE = $state.ExitCode
    $state.Requests += $args[1]
    switch -Wildcard ($args[1]) {
        '*/releases/latest' { $state.Fixture.release | ConvertTo-Json -Depth 8 }
        '*/releases/tags/*' { $state.Fixture.release | ConvertTo-Json -Depth 8 }
        '*/releases/assets/1' { $state.Fixture.metadata | ConvertTo-Json }
        '*/commits/*' { $state.Fixture.commit | ConvertTo-Json }
        default { throw "Unexpected GitHub request in fixture: $($args[1])" }
    }
}.GetNewClosure())
function Assert-Rejected([scriptblock] $Change, [switch] $Latest) {
    Reset-Fixture
    & $Change
    $rejected = $false
    try {
        if ($Latest) { & $selector | Out-Null }
        else { & $selector -Version '0.1.0' | Out-Null }
    }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Unsafe Store release selection was accepted.' }
}
try {
    Reset-Fixture
    $release = & $selector -Version '0.1.0'
    if ($release.SourceCommit -cne $sha -or $release.Version -ne '0.1.0' -or
        $release.PackageVersion -ne '0.1.0.0' -or
        $release.StoreReleaseNotes -cne "What's new in 0.1.0`n- Improve tray behavior`n- Update packages" -or
        $release.ReleaseUrl -ne 'https://github.com/DamianEdwards/ghcp-spend-tray/releases/tag/v0.1.0' -or
        $state.Requests[0] -ne 'repos/DamianEdwards/ghcp-spend-tray/releases/tags/v0.1.0') {
        throw 'Incorrect Store version override selection.'
    }
    Reset-Fixture
    $state.Fixture.release.tag_name = 'v0.3.0'
    $state.Fixture.release.assets[1].name = 'GHCPSpendTray-0.3.0.msixbundle'
    $state.Fixture.metadata.version = '0.3.0'
    $state.Fixture.metadata.packageVersion = '0.3.0.0'
    $latest = & $selector
    if ($latest.Version -ne '0.3.0' -or $latest.PackageVersion -ne '0.3.0.0' -or
        $latest.StoreReleaseNotes -cnotlike "What's new in 0.3.0*" -or
        $latest.ReleaseUrl -ne 'https://github.com/DamianEdwards/ghcp-spend-tray/releases/tag/v0.3.0' -or
        $state.Requests[0] -ne 'repos/DamianEdwards/ghcp-spend-tray/releases/latest' -or
        @($state.Requests | Where-Object { $_ -like '*/releases/tags/*' }).Count -ne 0) {
        throw 'Default Store source did not resolve and validate the latest release.'
    }
    Reset-Fixture
    $emptyOverride = & $selector -Version ''
    if ($emptyOverride.Version -ne '0.1.0' -or
        $state.Requests[0] -ne 'repos/DamianEdwards/ghcp-spend-tray/releases/latest') {
        throw 'An empty workflow version input did not select the latest release.'
    }
    Reset-Fixture
    $state.Fixture.release.body = "## What's Changed`r`n`r`n* Cache account avatars and refresh them from account settings by @DamianEdwards in https://github.com/DamianEdwards/ghcp-spend-tray/pull/26`r`n`r`n`r`n**Full Changelog**: https://github.com/DamianEdwards/ghcp-spend-tray/compare/v0.0.1...v0.1.0"
    $withFooter = & $selector -Version '0.1.0'
    if ($withFooter.StoreReleaseNotes -cne "What's new in 0.1.0`n- Cache account avatars and refresh them from account settings") {
        throw 'GitHub-generated full changelog footer was not excluded from Store release notes.'
    }
    Reset-Fixture
    $rejectedWhitespace = $false
    try { & $selector -Version ' ' | Out-Null }
    catch { $rejectedWhitespace = $true }
    if (-not $rejectedWhitespace -or $state.Requests.Count -ne 0) {
        throw 'A whitespace version override was treated as the latest release.'
    }
    Assert-Rejected { $state.Fixture.release.prerelease = $true } -Latest
    Assert-Rejected { $state.Fixture.release.draft = $true } -Latest
    Assert-Rejected { $state.Fixture.release.immutable = $false } -Latest
    Assert-Rejected { $state.Fixture.release.tag_name = 'v1.2' } -Latest
    Assert-Rejected { $state.Fixture.release.tag_name = '' } -Latest
    Assert-Rejected { $state.Fixture.metadata.version = '0.2.0' } -Latest
    Assert-Rejected { $state.Fixture.release.assets = @($state.Fixture.release.assets[0]) } -Latest
    Reset-Fixture
    $state.Fixture.release.prerelease = $true
    $preview = & $selector -Version '0.1.0'
    if ($preview.Version -ne '0.1.0') {
        throw 'An explicit version must continue to support immutable prerelease releases.'
    }
    Assert-Rejected { $state.Fixture.release.draft = $true }
    Assert-Rejected { $state.Fixture.release.immutable = $false }
    Assert-Rejected { $state.Fixture.release.tag_name = 'v0.2.0' }
    Assert-Rejected { $state.Fixture.release.body = 'No changes' }
    Assert-Rejected { $state.Fixture.release.body = "## What's Changed`n* Unknown entry" }
    Assert-Rejected { $state.Fixture.release.body = "## What's Changed`n**Full Changelog**: https://github.com/DamianEdwards/ghcp-spend-tray/compare/v0.0.1...v0.1.0" }
    Assert-Rejected { $state.Fixture.release.body = "## What's Changed`n* Valid by @fixture in https://github.com/DamianEdwards/ghcp-spend-tray/pull/1`n**Full Changelog**: invalid" }
    Assert-Rejected { $state.Fixture.release.body = "## What's Changed`n* Valid by @fixture in https://github.com/DamianEdwards/ghcp-spend-tray/pull/1`n**Full Changelog**: https://github.com/DamianEdwards/ghcp-spend-tray/compare/v0.0.1...v0.1.0`n* Unexpected by @fixture in https://github.com/DamianEdwards/ghcp-spend-tray/pull/2" }
    Assert-Rejected { $state.Fixture.release.body = "## What's Changed`n* $(-join ('A' * 1510)) by @fixture in https://github.com/DamianEdwards/ghcp-spend-tray/pull/1" }
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
    Assert-Rejected {} -Latest
}
finally {
    Remove-Item Function:\gh
    $global:LASTEXITCODE = 0
}
Write-Output 'PASS: latest release selection, version overrides, immutable source integrity and API failures.'
