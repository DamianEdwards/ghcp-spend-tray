#Requires -Version 7.2
param([switch]$Render, [switch]$Measure)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$script:Checks = 0
function Assert-Equal($Actual, $Expected, [string]$Name) {
    if ($Actual -cne $Expected) { throw "$Name expected '$Expected', received '$Actual'." }
    $script:Checks++
}
function Assert-Throws([scriptblock]$Action, [string]$Name) {
    $threw = $false
    try { & $Action | Out-Null }
    catch { $threw = $true }
    Assert-Equal $threw $true $Name
}
$now = [DateTimeOffset]'2026-10-16T12:00:00Z'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('GHCPSpendPrompt-tests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$key = 'a' * 64
$options = @{
    Hostname = 'github.com'; CacheDirectory = $directory; RefreshMinutes = 60
    RequestTimeoutSeconds = 1; GhExecutable = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    ScriptPath = Join-Path $PSScriptRoot 'CopilotPrompt.ps1'
}
$path = Get-CopilotCachePath $options $key
$quotaJson = '{"quota_snapshots":{"premium_interactions":{"credits_used":4000,"entitlement":10000,"unlimited":false,"has_quota":true,"token_based_billing":true}},"quota_reset_date":"2026-11-01"}'
function New-Sample([decimal]$Estimated = 80) {
    @{
        version = 1; hostname = 'github.com'; contextKey = $key; accountId = '123'
        status = 'fresh'; fetchedAtUtc = $now.ToString('O'); sourceTimestampUtc = $null
        resetAtUtc = '2026-11-01T00:00:00Z'; consumptionUsd = $Estimated / 2
        allocationUsd = 100; unlimited = $false; nextAttemptUtc = $now.AddMinutes(60).ToString('O')
    }
}
$realContext = (Get-Command Get-CopilotCredentialContext).ScriptBlock
$script:TestContext = $key
$script:Calls = [Collections.Generic.List[string]]::new()
$transport = {
    param($Options, $Endpoint)
    $script:Calls.Add($Endpoint)
    Assert-Equal (Test-CopilotRefreshLease (Read-CopilotDocument ($path + '.refresh')) $key $now) $true 'Worker publishes refresh lease'
    if ($Endpoint -eq 'user') {
        return @{ StatusCode = 200; ExitCode = 0; Headers = @{}; Body = '{"id":123}' }
    }
    return @{ StatusCode = 200; ExitCode = 0; Headers = @{}; Body = $quotaJson }
}
try {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        $tokens = $null
        $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        Assert-Equal $errors.Count 0 ('PowerShell syntax: ' + $file.Name)
    }
    Assert-Equal (Get-Variable CopilotPromptState -Scope Script -ErrorAction SilentlyContinue) $null 'Loading does not initialize'
    foreach ($case in @(@(0, '$0'), @(0.49, '$0'), @(0.5, '$1'), @(246.55, '$247'),
        @(999.49, '$999'), @(999.5, '$1K'), @(1000, '$1K'), @(1050, '$1.1K'),
        @(4426, '$4.4K'), @(10000, '$10K'), @(10500, '$10.5K'))) {
        Assert-Equal (Format-CopilotUsd $case[0]) $case[1] "Compact USD $($case[0])"
    }
    foreach ($case in @(@(0, 'green'), @(79.99, 'green'), @(80, 'green'),
        @(80.01, 'yellow'), @(100, 'yellow'), @(100.01, 'red'), @(120, 'red'))) {
        Assert-Equal (Get-CopilotPresentation (New-Sample $case[0]) $now).ForecastState $case[1] "Unrounded threshold $($case[0])"
    }
    $cache = New-Sample
    Assert-Equal (Get-CopilotPresentation $cache $now).Text '$40 (~40%)' 'Spend-first format'
    Assert-Equal (Get-CopilotPresentation $cache $now).Forecast '$80' 'Calendar forecast'
    $cache.consumptionUsd = [decimal]246.55
    $cache.allocationUsd = 3000
    Assert-Equal (Get-CopilotPresentation $cache $now).Text '$247 (~8%)' 'Rounded percentage'
    $cache = New-Sample
    Assert-Equal (Get-CopilotPresentation $cache $now.AddMinutes(60)).Text 'unavailable' 'Exact freshness boundary'
    Assert-Equal (Get-CopilotPresentation $cache $now.AddMinutes(60) 120).Text '$40 (~40%)' 'Configured freshness'
    Assert-Equal (Get-CopilotPresentation $cache $now.AddMinutes(-1)).Text 'unavailable' 'Future observation'
    $cache.status = 'unavailable'
    Assert-Equal (Get-CopilotPresentation $cache $now).ForecastState 'unknown' 'Failed refresh'
    $cache = New-Sample
    foreach ($allocation in @($null, 0)) {
        $cache.allocationUsd = $allocation
        Assert-Equal (Get-CopilotPresentation $cache $now).Text '$40' 'Unavailable allocation preserves dollars'
        Assert-Equal (Get-CopilotPresentation $cache $now).Forecast '?' 'Unavailable allocation has no colored forecast'
    }
    $cache = New-Sample
    $cache.unlimited = $true
    Assert-Equal (Get-CopilotPresentation $cache $now).ForecastState 'unknown' 'Unlimited allocation'
    $cache = New-Sample
    $cache.resetAtUtc = '2026-10-17T00:00:00Z'
    Assert-Equal (Get-CopilotPresentation $cache $now).Forecast '?' 'Non-calendar billing'
    $cache = New-Sample
    $cache.sourceTimestampUtc = $now.AddMinutes(-61).ToString('O')
    Assert-Equal (Get-CopilotPresentation $cache $now).Forecast '?' 'Stale source timestamp'
    $cache.sourceTimestampUtc = $now.AddSeconds(1).ToString('O')
    Assert-Equal (Get-CopilotPresentation $cache $now).Forecast '?' 'Future source timestamp'
    $cache = New-Sample
    $cache.fetchedAtUtc = '2026-10-01T12:00:00Z'
    Assert-Equal (Get-CopilotPresentation $cache ([DateTimeOffset]'2026-10-01T12:00:00Z')).Forecast '?' 'First 24 hours'
    $cache.fetchedAtUtc = '2026-10-31T23:59:00Z'
    Assert-Equal (Get-CopilotPresentation $cache ([DateTimeOffset]'2026-11-01T00:00:00Z')).Text 'unavailable' 'Billing rollover'
    $cache.resetAtUtc = $null
    Assert-Equal (Get-CopilotPresentation $cache ([DateTimeOffset]'2026-11-01T00:00:00Z')).Text 'unavailable' 'Calendar rollover without reset'
    $parsed = ConvertFrom-CopilotQuota $quotaJson $now $options $key '123'
    Assert-Equal $parsed.consumptionUsd ([decimal]40) 'Credit conversion'
    Assert-Equal $parsed.allocationUsd ([decimal]100) 'Allocation conversion'
    Assert-Equal (ConvertTo-CopilotUtc ($parsed | ConvertTo-Json | ConvertFrom-Json -AsHashtable).fetchedAtUtc) $now 'JSON date timezone preserved'
    Assert-Equal (ConvertTo-CopilotUtc ([DateTime]::new(2026, 10, 16, 12, 0, 0))) $now 'Unspecified JSON dates are UTC'
    Assert-Equal (ConvertTo-CopilotUtc $now.ToUnixTimeSeconds()) $now 'Unix timestamps are UTC'
    foreach ($invalid in @('[]', '{}', 'not-json',
        $quotaJson.Replace('"token_based_billing":true', '"token_based_billing":false'),
        $quotaJson.Replace('"unlimited":false', '"unlimited":"false"'),
        $quotaJson.Replace('"credits_used":4000', '"credits_used":-1'),
        $quotaJson.Replace('"credits_used":4000', '"credits_used":"4000"'),
        $quotaJson.Replace('2026-11-01', '2026-10-01'))) {
        Assert-Throws { ConvertFrom-CopilotQuota $invalid $now $options $key '123' } 'Invalid quota rejected'
    }
    $withoutAllocation = $quotaJson.Replace('"entitlement":10000,', '')
    Assert-Equal (ConvertFrom-CopilotQuota $withoutAllocation $now $options $key '123').allocationUsd $null 'Missing allocation supported'

    $environment = @{ GH_CONFIG_DIR = $directory; GH_TOKEN = 'synthetic-token-a' }
    $firstKey = & $realContext $options $environment
    Assert-Equal ($firstKey -cmatch '^[a-f0-9]{64}$') $true 'Fingerprint is opaque'
    $environment.GH_TOKEN = 'synthetic-token-b'
    Assert-Equal ((& $realContext $options $environment) -cne $firstKey) $true 'Environment credential changes isolate cache'
    $environment.GH_TOKEN = ''
    $firstKey = & $realContext $options $environment
    [IO.File]::WriteAllText((Join-Path $directory 'hosts.yml'), 'synthetic configuration metadata')
    Assert-Equal ((& $realContext $options $environment) -cne $firstKey) $true 'gh account configuration changes isolate cache'
    $otherHost = $options.Clone()
    $otherHost.Hostname = 'enterprise.example'
    Assert-Equal ((& $realContext $otherHost $environment) -cne (& $realContext $options $environment)) $true 'Hosts isolate cache'
    $otherInterval = $options.Clone()
    $otherInterval.RefreshMinutes = 120
    Assert-Equal ((& $realContext $otherInterval $environment) -cne (& $realContext $options $environment)) $true 'Refresh schedules isolate cache'
    function Get-CopilotCredentialContext { param($Options) return $script:TestContext }

    Invoke-CopilotRefresh $options $key -Now $now -Transport $transport
    $cache = Read-CopilotCache $path $options $key
    Assert-Equal $cache.status 'fresh' 'Successful refresh persists'
    Assert-Equal $cache.accountId '123' 'Verified account identity persists'
    Assert-Equal ($script:Calls -join ',') 'user,copilot_internal/user' 'Identity and quota requests'
    Assert-Equal ([IO.File]::Exists($path + '.refresh')) $false 'Completed refresh removes lease'
    Invoke-CopilotRefresh $options $key -Now $now.AddMinutes(59) -Transport $transport
    Assert-Equal $script:Calls.Count 2 'Fresh cache does not call API'
    $lock = [IO.File]::Open($path + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Invoke-CopilotRefresh $options $key -Now $now.AddHours(2) -Transport $transport }
    finally { $lock.Dispose() }
    Assert-Equal $script:Calls.Count 2 'Concurrent refresh cannot call API'
    $lease = @{ contextKey = $key; startedAtUtc = $now.ToString('O'); expiresAtUtc = $now.AddSeconds(30).ToString('O') }
    Assert-Equal (Test-CopilotRefreshLease $lease $key $now) $true 'Other terminal observes refresh'
    Assert-Equal (Test-CopilotRefreshLease $lease $key $now.AddSeconds(30)) $false 'Abandoned lease expires'
    Assert-Equal (Test-CopilotRefreshLease $lease ('b' * 64) $now) $false 'Lease is context-specific'

    $failureTransport = {
        param($Options, $Endpoint)
        $script:Calls.Add($Endpoint)
        @{ StatusCode = 403; ExitCode = 1; Headers = @{}; Body = 'SYNTHETIC-PRIVATE-RESPONSE' }
    }
    Invoke-CopilotRefresh $options $key -Now $now.AddHours(1) -Transport $failureTransport
    $cache = Read-CopilotCache $path $options $key
    Assert-Equal $cache.status 'unavailable' 'Authentication failure is persisted'
    Assert-Equal ($cache.diagnostic.Contains('403')) $true 'Diagnostic includes HTTP status'
    Assert-Equal ([IO.File]::ReadAllText($path).Contains('SYNTHETIC-PRIVATE-RESPONSE')) $false 'Raw responses are not persisted'
    $beforeCooldown = $script:Calls.Count
    Invoke-CopilotRefresh $options $key -Now $now.AddHours(1).AddMinutes(4) -Transport $failureTransport
    Assert-Equal $script:Calls.Count $beforeCooldown 'Failure cooldown prevents repeated API requests'
    $retryResponse = @{ Headers = @{ 'retry-after' = '3600' } }
    Assert-Equal (Get-CopilotRetryAt $retryResponse $now) $now.AddHours(1) 'Numeric Retry-After'
    $retryResponse.Headers['retry-after'] = $now.AddHours(2).ToString('r')
    Assert-Equal (Get-CopilotRetryAt $retryResponse $now) $now.AddHours(2) 'HTTP-date Retry-After'
    $retryResponse.Headers = @{ 'x-ratelimit-remaining' = '0'; 'x-ratelimit-reset' = [string]$now.AddHours(3).ToUnixTimeSeconds() }
    Assert-Equal (Get-CopilotRetryAt $retryResponse $now) $now.AddHours(3) 'Rate-limit reset'
    $limitedTransport = {
        param($Options, $Endpoint)
        if ($Endpoint -eq 'user') { return @{ StatusCode = 200; ExitCode = 0; Headers = @{}; Body = '{"id":123}' } }
        @{ StatusCode = 429; ExitCode = 1; Headers = @{ 'retry-after' = '3600' }; Body = 'SYNTHETIC-PRIVATE-RESPONSE' }
    }
    Invoke-CopilotRefresh $options $key -Now $now.AddHours(2) -Transport $limitedTransport
    $cache = Read-CopilotCache $path $options $key
    Assert-Equal $cache.status 'unavailable' 'API failure replaces prior observation'
    Assert-Equal ($cache.diagnostic.Contains('429')) $true 'API diagnostic identifies rate limit'
    Assert-Equal (ConvertTo-CopilotUtc $cache.nextAttemptUtc) $now.AddHours(3) 'API retry deadline is persisted'
    $changingTransport = {
        param($Options, $Endpoint)
        if ($Endpoint -eq 'user') { return @{ StatusCode = 200; ExitCode = 0; Headers = @{}; Body = '{"id":123}' } }
        $script:TestContext = 'b' * 64
        @{ StatusCode = 200; ExitCode = 0; Headers = @{}; Body = $quotaJson.Replace('2026-11-01', '2026-12-01') }
    }
    Invoke-CopilotRefresh $options $key -Now $now.AddHours(3) -Transport $changingTransport
    $cache = Read-CopilotCache $path $options $key
    Assert-Equal $cache.status 'unavailable' 'Changed credential context discards result'
    Assert-Equal ($cache.diagnostic.Contains('Credentials changed')) $true 'Credential switch has a diagnostic'
    $script:TestContext = $key
    $cache = New-Sample
    $cache.hostname = 'different.example'
    Write-CopilotDocument $path $cache
    Assert-Throws { Read-CopilotCache $path $options $key } 'Mismatched host rejected'
    $cache = New-Sample
    $cache.consumptionUsd = -1
    Write-CopilotDocument $path $cache
    Assert-Throws { Read-CopilotCache $path $options $key } 'Corrupt amounts rejected'
    [IO.File]::WriteAllText($path, '{')
    Assert-Throws { Read-CopilotCache $path $options $key } 'Truncated cache rejected'
    [IO.File]::WriteAllText($path, 'x' * 16385)
    Assert-Throws { Read-CopilotCache $path $options $key } 'Oversized cache rejected'

    $workerScript = Join-Path $directory 'synthetic-worker.ps1'
    [IO.File]::WriteAllText($workerScript, @'
function Invoke-CopilotRefresh {
    param($Options, $ContextKey)
    $proof = @{ hostname = $Options.Hostname; contextKey = $ContextKey } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $Options.CacheDirectory 'worker-proof.json'), $proof)
}
'@)
    $workerOptions = $options.Clone()
    $workerOptions.ScriptPath = $workerScript
    $worker = Start-CopilotRefreshJob $workerOptions $key
    try {
        if ($null -eq (Wait-Job $worker -Timeout 30)) { throw 'Synthetic background worker timed out.' }
        Receive-Job $worker -ErrorAction Stop | Out-Null
        Assert-Equal $worker.State 'Completed' 'Real background job completes offline'
        $proof = Read-CopilotDocument (Join-Path $directory 'worker-proof.json')
        Assert-Equal $proof.contextKey $key 'Background job receives credential context'
        Assert-Equal $proof.hostname 'github.com' 'Background job receives options'
    }
    finally {
        if ($worker.State -in @('Running', 'NotStarted')) { Stop-Job $worker }
        Remove-Job $worker
    }

    $cache = New-Sample
    Write-CopilotDocument $path $cache
    Initialize-CopilotPrompt -CacheDirectory $directory -GhExecutable pwsh
    Assert-Equal $script:CopilotPromptState.Options.ScriptPath $options.ScriptPath 'Worker knows source script'
    $global:LASTEXITCODE = 37
    Update-CopilotPrompt -Now $now
    Assert-Equal $global:LASTEXITCODE 37 'Last exit code preserved'
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'connected' 'Healthy connection glyph'
    Assert-Equal ($null -eq $script:CopilotPromptState.Job) $true 'Cached prompt starts no process'
    $script:StartedJobs = 0
    function Start-CopilotRefreshJob {
        param($Options, $ContextKey)
        $script:StartedJobs++
        [pscustomobject]@{ State = 'Running' }
    }
    Write-CopilotDocument ($path + '.refresh') $lease
    Update-CopilotPrompt -Now $now.AddSeconds(1)
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'in_progress' 'Another terminal changes progress glyph'
    Assert-Equal $script:StartedJobs 0 'Existing lease avoids another job'
    [IO.File]::Delete($path + '.refresh')
    Update-CopilotPrompt -Now $now.AddMinutes(60)
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'in_progress' 'Own refresh changes progress glyph'
    Assert-Equal $script:StartedJobs 1 'Expired cache launches one worker'
    Update-CopilotPrompt -Now $now.AddMinutes(60).AddSeconds(1)
    Assert-Equal $script:StartedJobs 1 'Active worker is reused'
    $script:CopilotPromptState.Job = $null
    $cache.status = 'unavailable'
    $cache.diagnostic = 'Synthetic authentication failure.'
    $cache.nextAttemptUtc = $now.AddHours(2).ToString('O')
    Write-CopilotDocument $path $cache
    $warnings = @()
    Update-CopilotPrompt -Now $now.AddMinutes(61) -WarningVariable warnings -WarningAction SilentlyContinue
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'not_connected' 'Failure glyph'
    Assert-Equal $warnings.Count 1 'Failure is surfaced'
    $warnings = @()
    Update-CopilotPrompt -Now $now.AddMinutes(61).AddSeconds(1) -WarningVariable warnings -WarningAction SilentlyContinue
    Assert-Equal $warnings.Count 0 'Failure warning is not repeated per prompt'

    Disable-CopilotPrompt
    $oldAlias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
    $script:HookCalls = @()
    function global:Invoke-SyntheticPoshHook { param($Status); $script:HookCalls += $Status; $global:LASTEXITCODE = 99 }
    New-Alias Set-PoshContext Invoke-SyntheticPoshHook -Scope Global -Force
    try {
        $cache = New-Sample
        $cache.fetchedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $cache.resetAtUtc = $null
        $cache.nextAttemptUtc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O')
        Write-CopilotDocument $path $cache
        Initialize-CopilotPrompt -CacheDirectory $directory -GhExecutable pwsh -InstallHook
        $global:LASTEXITCODE = 42
        Set-PoshContext $false
        Assert-Equal $script:HookCalls.Count 1 'Existing hook is composed'
        Assert-Equal $script:HookCalls[0] $false 'Existing hook receives original status'
        Assert-Equal $global:LASTEXITCODE 42 'Composed hook preserves exit code'
        Initialize-CopilotPrompt -CacheDirectory $directory -GhExecutable pwsh -InstallHook
        Set-PoshContext $true
        Assert-Equal $script:HookCalls.Count 2 'Reinitialization does not recursively wrap hook'
        Disable-CopilotPrompt
        Assert-Equal (Get-Alias Set-PoshContext).Definition 'Invoke-SyntheticPoshHook' 'Previous alias is restored'
        Assert-Equal ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('COPILOT_SPEND', 'Process'))) $true 'Disable clears owned variables'
    }
    finally {
        Remove-Alias Set-PoshContext -Scope Global -Force
        if ($oldAlias) { New-Alias Set-PoshContext $oldAlias.Definition -Scope Global -Force }
        Remove-Item Function:\Invoke-SyntheticPoshHook
    }

    $segment = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'copilot.segment.json') -Raw | ConvertFrom-Json
    Assert-Equal $segment.type 'text' 'Standard text segment'
    foreach ($code in @(0xEC1E, 0xEC3E, 0xEC4C)) {
        Assert-Equal ($segment.template.Contains([string][char]$code)) $true 'Copilot glyph in canonical segment'
    }
    Assert-Equal ($segment.template.Contains([char]::ConvertFromUtf32(0xF15B6))) $true 'Chart glyph in canonical segment'
    Assert-Equal ($segment.template.Contains('est.')) $false 'No estimate label'
    if ($Render) {
        $posh = Get-Command oh-my-posh -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $themePath = Join-Path $directory 'test.omp.json'
        Write-CopilotDocument $themePath @{ version = 4; blocks = @(@{ type = 'prompt'; alignment = 'left'; segments = @($segment) }) }
        $env:COPILOT_SPEND = '$247 (~8%)'
        $env:COPILOT_FORECAST = '$3.3K'
        foreach ($case in @(@('green', '166;227;161'), @('yellow', '249;226;175'), @('red', '243;139;168'))) {
            $env:COPILOT_FORECAST_STATE = $case[0]
            $env:COPILOT_CONNECTION_STATE = 'connected'
            $rendered = (& $posh.Source print primary --config $themePath --shell pwsh) -join "`n"
            Assert-Equal $LASTEXITCODE 0 'Oh My Posh rendering succeeds'
            Assert-Equal ($rendered.Contains($case[1])) $true 'Rendered projection color'
            Assert-Equal ($rendered.Contains('$247 (~8%)')) $true 'Rendered spend'
            Assert-Equal ($rendered.Contains('$3.3K')) $true 'Rendered compact forecast'
        }
        foreach ($case in @(@('connected', 0xEC1E), @('in_progress', 0xEC4C), @('not_connected', 0xEC3E))) {
            $env:COPILOT_CONNECTION_STATE = $case[0]
            $rendered = (& $posh.Source print primary --config $themePath --shell pwsh) -join "`n"
            Assert-Equal ($rendered.Contains([string][char]$case[1])) $true 'Rendered connection glyph'
        }
    }
    if ($Measure) {
        Initialize-CopilotPrompt -CacheDirectory $directory -GhExecutable pwsh
        $cache = New-Sample
        Write-CopilotDocument $path $cache
        Update-CopilotPrompt -Now $now
        $samples = @(1..200 | ForEach-Object { (Measure-Command { Update-CopilotPrompt -Now $now }).TotalMilliseconds }) | Sort-Object
        'Cached update: median {0:N3} ms; p95 {1:N3} ms (rendering excluded).' -f $samples[99], $samples[189]
    }
    "PASS: $script:Checks offline Copilot prompt assertions."
}
finally {
    if (Get-Variable CopilotPromptState -Scope Script -ErrorAction SilentlyContinue) {
        $script:CopilotPromptState.Job = $null
        Disable-CopilotPrompt
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($directory)
}
