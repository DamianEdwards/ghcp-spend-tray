#Requires -Version 7.2

function ConvertTo-CopilotUtc {
    param($Value)
    if ($null -eq $Value -or $Value -eq '') { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { $Value = [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc) }
        return [DateTimeOffset]::new($Value.ToUniversalTime())
    }
    if ($Value -is [long] -or $Value -is [int]) {
        return [DateTimeOffset]::FromUnixTimeSeconds($Value)
    }
    if ($Value -isnot [string]) { throw 'Invalid usage timestamp.' }
    return [DateTimeOffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

function ConvertTo-CopilotAmount {
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool] -or
        $Value -is [System.Collections.IEnumerable]) { throw 'Invalid quota amount.' }
    $amount = [decimal]$Value
    if ($amount -lt 0) { throw 'Invalid quota amount.' }
    return $amount
}

function Format-CopilotUsd {
    param([decimal]$Amount)
    if ($Amount -lt 0) { throw 'Invalid dollar amount.' }
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $rounded = [decimal]::Round($Amount, 0, [MidpointRounding]::AwayFromZero)
    if ($rounded -ge 1000) {
        $thousands = [decimal]::Round($Amount / 1000, 1, [MidpointRounding]::AwayFromZero)
        return '$' + $thousands.ToString('0.#', $culture) + 'K'
    }
    return '$' + $rounded.ToString('0', $culture)
}

function Get-CopilotCredentialContext {
    param([hashtable]$Options, [hashtable]$EnvironmentValues)
    if ($null -eq $EnvironmentValues) {
        $EnvironmentValues = @{}
        foreach ($name in @('GH_CONFIG_DIR', 'XDG_CONFIG_HOME', 'APPDATA', 'GH_TOKEN', 'GITHUB_TOKEN',
            'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN')) {
            $EnvironmentValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
    }
    $configDirectory = if ($EnvironmentValues['GH_CONFIG_DIR']) { $EnvironmentValues['GH_CONFIG_DIR'] }
        elseif ($EnvironmentValues['XDG_CONFIG_HOME']) { Join-Path $EnvironmentValues['XDG_CONFIG_HOME'] 'gh' }
        elseif ($EnvironmentValues['APPDATA']) { Join-Path $EnvironmentValues['APPDATA'] 'GitHub CLI' }
        else { Join-Path $HOME '.config\gh' }
    $configPath = [IO.Path]::GetFullPath((Join-Path $configDirectory 'hosts.yml'))
    $metadata = [IO.FileInfo]::new($configPath)
    $stamp = if ($metadata.Exists) { "$($metadata.LastWriteTimeUtc.Ticks):$($metadata.Length)" } else { 'missing' }
    $cloud = $Options.Hostname -eq 'github.com' -or $Options.Hostname.EndsWith('.ghe.com')
    $token = if ($cloud) {
        if ($EnvironmentValues['GH_TOKEN']) { $EnvironmentValues['GH_TOKEN'] } else { $EnvironmentValues['GITHUB_TOKEN'] }
    }
    else {
        if ($EnvironmentValues['GH_ENTERPRISE_TOKEN']) { $EnvironmentValues['GH_ENTERPRISE_TOKEN'] } else { $EnvironmentValues['GITHUB_ENTERPRISE_TOKEN'] }
    }
    # Only the fingerprint is used as a filename; token/config contents are never persisted.
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        "$($Options.Hostname)`n$($Options.GhExecutable)`n$($Options.RefreshMinutes)`n$configPath`n$stamp`n$token")
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Get-CopilotCachePath {
    param([hashtable]$Options, [string]$ContextKey)
    if ($ContextKey -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid credential context.' }
    return Join-Path $Options.CacheDirectory ($ContextKey + '.json')
}

function Read-CopilotDocument {
    param([string]$Path, [int]$Limit = 16384)
    if (![IO.File]::Exists($Path)) { return $null }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -gt $Limit) { throw 'Copilot cache exceeded its size limit.' }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true))
        try { $json = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $document = ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
        if ($document -isnot [hashtable]) { throw 'Invalid Copilot cache document.' }
        return $document
    }
    finally { $stream.Dispose() }
}

function Write-CopilotDocument {
    param([string]$Path, [hashtable]$Document)
    $staging = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($staging, ($Document | ConvertTo-Json -Depth 5),
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($staging, $Path, $true)
    }
    finally { if ([IO.File]::Exists($staging)) { [IO.File]::Delete($staging) } }
}

function Read-CopilotCache {
    param([string]$Path, [hashtable]$Options, [string]$ContextKey)
    $cache = Read-CopilotDocument $Path
    if ($null -eq $cache) { return $null }
    if ($cache.version -ne 1 -or $cache.hostname -cne $Options.Hostname -or
        $cache.contextKey -cne $ContextKey -or $cache.status -cnotin @('fresh', 'unavailable') -or
        $null -eq (ConvertTo-CopilotUtc $cache.nextAttemptUtc)) { throw 'Invalid Copilot cache identity or schedule.' }
    if ($cache.status -eq 'fresh') {
        if ($cache.accountId -cnotmatch '^[1-9][0-9]*$' -or
            $null -eq (ConvertTo-CopilotUtc $cache.fetchedAtUtc) -or $cache.unlimited -isnot [bool]) {
            throw 'Invalid Copilot cache observation.'
        }
        [void](ConvertTo-CopilotAmount $cache.consumptionUsd)
        if ($null -ne $cache.allocationUsd) { [void](ConvertTo-CopilotAmount $cache.allocationUsd) }
        [void](ConvertTo-CopilotUtc $cache.sourceTimestampUtc)
        [void](ConvertTo-CopilotUtc $cache.resetAtUtc)
    }
    elseif ($cache.diagnostic -isnot [string] -or $cache.diagnostic.Length -gt 256) {
        throw 'Invalid Copilot cache diagnostic.'
    }
    return $cache
}

function Get-CopilotPresentation {
    param($Cache, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow, [int]$RefreshMinutes = 60)
    $result = @{ Text = 'unavailable'; Forecast = '?'; ForecastState = 'unknown' }
    if ($null -eq $Cache -or $Cache.status -ne 'fresh') { return $result }
    $fetched = ConvertTo-CopilotUtc $Cache.fetchedAtUtc
    if ($null -eq $fetched -or $fetched -gt $Now -or ($Now - $fetched).TotalMinutes -ge $RefreshMinutes) {
        return $result
    }
    $start = [DateTimeOffset]::new($fetched.Year, $fetched.Month, 1, 0, 0, 0, [TimeSpan]::Zero)
    $calendarReset = $start.AddMonths(1)
    $reset = ConvertTo-CopilotUtc $Cache.resetAtUtc
    $periodEnd = if ($null -ne $reset) { $reset } else { $calendarReset }
    if ($Now -ge $periodEnd) { return $result }
    $used = ConvertTo-CopilotAmount $Cache.consumptionUsd
    $result.Text = Format-CopilotUsd $used
    if ($Cache.unlimited -isnot [bool]) { throw 'Invalid unlimited allocation flag.' }
    if ($Cache.unlimited -or $null -eq $Cache.allocationUsd) { return $result }
    $allocation = ConvertTo-CopilotAmount $Cache.allocationUsd
    if ($allocation -eq 0) { return $result }
    $percent = [decimal]::Round($used / $allocation * 100, 0, [MidpointRounding]::AwayFromZero)
    $result.Text += ' (~' + $percent.ToString('0', [Globalization.CultureInfo]::InvariantCulture) + '%)'
    if ($null -ne $reset -and $reset -ne $calendarReset) { return $result }
    $observed = ConvertTo-CopilotUtc $Cache.sourceTimestampUtc
    if ($null -eq $observed) { $observed = $fetched }
    if ($observed -lt $start -or $observed -gt $fetched -or
        ($Now - $observed).TotalMinutes -ge $RefreshMinutes -or ($observed - $start).TotalDays -lt 1) {
        return $result
    }
    $estimate = $used * [decimal]($calendarReset - $start).Ticks / [decimal]($observed - $start).Ticks
    $result.Forecast = Format-CopilotUsd $estimate
    $result.ForecastState = if ($estimate -le $allocation * [decimal]0.8) { 'green' }
        elseif ($estimate -le $allocation) { 'yellow' } else { 'red' }
    return $result
}

function ConvertFrom-CopilotQuota {
    param([string]$Json, [DateTimeOffset]$Now, [hashtable]$Options, [string]$ContextKey, [string]$AccountId)
    $wire = ConvertFrom-Json -InputObject $Json -AsHashtable -ErrorAction Stop
    $quota = $wire['quota_snapshots']['premium_interactions']
    if ($quota -isnot [hashtable] -or $quota.token_based_billing -isnot [bool] -or
        !$quota.token_based_billing -or $quota.unlimited -isnot [bool] -or $quota.has_quota -isnot [bool]) {
        throw 'Unsupported or invalid token-based quota.'
    }
    $used = ConvertTo-CopilotAmount $quota['credits_used']
    $allocation = if ($null -ne $quota['entitlement']) { (ConvertTo-CopilotAmount $quota['entitlement']) / 100 } else { $null }
    $reset = ConvertTo-CopilotUtc $wire['quota_reset_date_utc']
    if ($null -eq $reset) { $reset = ConvertTo-CopilotUtc $wire['quota_reset_date'] }
    if ($null -ne $reset -and $reset -le $Now) { throw 'Expired billing period.' }
    $source = ConvertTo-CopilotUtc $quota['timestamp_utc']
    if ($null -ne $source -and $source -gt $Now) { throw 'Future usage timestamp.' }
    return @{
        version = 1; hostname = $Options.Hostname; contextKey = $ContextKey; accountId = $AccountId
        status = 'fresh'; fetchedAtUtc = $Now.ToString('O')
        sourceTimestampUtc = $(if ($null -ne $source) { $source.ToString('O') } else { $null })
        resetAtUtc = $(if ($null -ne $reset) { $reset.ToString('O') } else { $null })
        consumptionUsd = $used / 100; allocationUsd = $allocation; unlimited = $quota.unlimited
        nextAttemptUtc = $Now.AddMinutes($Options.RefreshMinutes).ToString('O')
    }
}

function Invoke-CopilotGh {
    param([hashtable]$Options, [string]$Endpoint)
    $info = [Diagnostics.ProcessStartInfo]::new($Options.GhExecutable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['GH_PROMPT_DISABLED'] = '1'
    $info.Environment['GH_NO_UPDATE_NOTIFIER'] = '1'
    $info.Environment['GH_PAGER'] = 'cat'
    $info.Environment['NO_COLOR'] = '1'
    foreach ($name in @('GH_DEBUG', 'DEBUG', 'GH_FORCE_TTY', 'CLICOLOR_FORCE')) { [void]$info.Environment.Remove($name) }
    foreach ($argument in @('api', '--hostname', $Options.Hostname, '--method', 'GET', '--include', $Endpoint)) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw 'Could not start gh.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($Options.RequestTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'GitHub request timed out.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        if ($output.Length -gt 1048576) { throw 'Response exceeded its size limit.' }
        $parts = $output -split '\r?\n\r?\n', 2
        if ($parts.Count -ne 2 -or $parts[0] -notmatch '^HTTP/[^\s]+\s+(\d+)') {
            throw 'gh returned no valid HTTP response. Check gh authentication.'
        }
        $status = [int]$Matches[1]
        $headers = @{}
        foreach ($line in ($parts[0] -split '\r?\n' | Select-Object -Skip 1)) {
            if ($line -match '^([^:]+):[ \t]*(.*)$') { $headers[$Matches[1].ToLowerInvariant()] = $Matches[2].Trim() }
        }
        return @{ StatusCode = $status; Headers = $headers; Body = $parts[1]; ExitCode = $process.ExitCode }
    }
    finally { $process.Dispose() }
}

function Get-CopilotRetryAt {
    param([hashtable]$Response, [DateTimeOffset]$Now)
    $retry = $Now.AddMinutes(5)
    $headers = $Response.Headers
    if ($headers['retry-after']) {
        $seconds = 0L
        $server = if ([long]::TryParse($headers['retry-after'], [ref]$seconds)) {
            $Now.AddSeconds([Math]::Max(0, $seconds))
        }
        else { ConvertTo-CopilotUtc $headers['retry-after'] }
        if ($server -gt $retry) { $retry = $server }
    }
    if ($headers['x-ratelimit-remaining'] -eq '0' -and $headers['x-ratelimit-reset']) {
        $server = [DateTimeOffset]::FromUnixTimeSeconds([long]$headers['x-ratelimit-reset'])
        if ($server -gt $retry) { $retry = $server }
    }
    return $retry
}

function Test-CopilotRefreshLease {
    param($Lease, [string]$ContextKey, [DateTimeOffset]$Now)
    return $null -ne $Lease -and $Lease.contextKey -ceq $ContextKey -and
        (ConvertTo-CopilotUtc $Lease.startedAtUtc) -le $Now -and
        (ConvertTo-CopilotUtc $Lease.expiresAtUtc) -gt $Now
}

function Invoke-CopilotRefresh {
    param(
        [hashtable]$Options, [string]$ContextKey,
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow,
        [scriptblock]$Transport = { param($Options, $Endpoint) Invoke-CopilotGh $Options $Endpoint }
    )
    $path = Get-CopilotCachePath $Options $ContextKey
    [void][IO.Directory]::CreateDirectory($Options.CacheDirectory)
    $lock = $null
    $ownsLease = $false
    try {
        try {
            $lock = [IO.File]::Open($path + '.lock', [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            if (($_.Exception.HResult -band 0xFFFF) -in @(32, 33)) { return }
            throw
        }
        $cache = $null
        try { $cache = Read-CopilotCache $path $Options $ContextKey }
        catch { Write-Warning 'Invalid Copilot cache; it will be replaced by a validated refresh result.' }
        if ($null -ne $cache -and (ConvertTo-CopilotUtc $cache.nextAttemptUtc) -gt $Now) { return }
        Write-CopilotDocument ($path + '.refresh') @{
            contextKey = $ContextKey; startedAtUtc = $Now.ToString('O')
            expiresAtUtc = $Now.AddSeconds(2 * $Options.RequestTimeoutSeconds + 30).ToString('O')
        }
        $ownsLease = $true
        $retry = $Now.AddMinutes(5)
        $diagnostic = 'Could not run gh or the request timed out. Check gh installation and authentication.'
        try {
            if ((Get-CopilotCredentialContext $Options) -cne $ContextKey) {
                $diagnostic = 'Credentials changed before refresh. Retry with the currently selected gh account.'
                throw $diagnostic
            }
            $identityResponse = & $Transport $Options 'user'
            $retry = Get-CopilotRetryAt $identityResponse $Now
            if ($identityResponse.StatusCode -ne 200 -or $identityResponse.ExitCode -ne 0) {
                $diagnostic = 'GitHub identity request failed (HTTP ' + $identityResponse.StatusCode + '). Check gh authentication.'
                throw $diagnostic
            }
            $diagnostic = 'GitHub returned an invalid account identity. Current spend is unavailable.'
            $identity = ConvertFrom-Json -InputObject $identityResponse.Body -AsHashtable -ErrorAction Stop
            $accountId = [string]$identity.id
            if ($accountId -cnotmatch '^[1-9][0-9]*$') { throw $diagnostic }
            $diagnostic = 'Could not read Copilot usage. Check connectivity and endpoint access.'
            $response = & $Transport $Options 'copilot_internal/user'
            $retry = Get-CopilotRetryAt $response $Now
            if ($response.StatusCode -ne 200 -or $response.ExitCode -ne 0) {
                $diagnostic = 'Copilot request failed (HTTP ' + $response.StatusCode +
                    '). Check gh authentication, account access and host support.'
                throw $diagnostic
            }
            $diagnostic = 'Unsupported billing or invalid/expired quota data. Current spend is unavailable.'
            $fetchedAt = if ($PSBoundParameters.ContainsKey('Now')) { $Now } else { [DateTimeOffset]::UtcNow }
            $cache = ConvertFrom-CopilotQuota $response.Body $fetchedAt $Options $ContextKey $accountId
            $diagnostic = 'Credentials changed during refresh. Retry with the currently selected gh account.'
            if ((Get-CopilotCredentialContext $Options) -cne $ContextKey) { throw $diagnostic }
        }
        catch {
            $cache = @{
                version = 1; hostname = $Options.Hostname; contextKey = $ContextKey
                status = 'unavailable'; nextAttemptUtc = $retry.ToString('O'); diagnostic = $diagnostic
            }
        }
        Write-CopilotDocument $path $cache
    }
    finally {
        try {
            if ($ownsLease -and [IO.File]::Exists($path + '.refresh')) { [IO.File]::Delete($path + '.refresh') }
        }
        finally { if ($null -ne $lock) { $lock.Dispose() } }
    }
}

function Start-CopilotRefreshJob {
    param([hashtable]$Options, [string]$ContextKey)
    return Start-Job -ScriptBlock {
        param($ScriptPath, $Options, $ContextKey)
        $ErrorActionPreference = 'Stop'
        . $ScriptPath
        Invoke-CopilotRefresh $Options $ContextKey
    } -ArgumentList $Options.ScriptPath, $Options, $ContextKey
}

function Initialize-CopilotPrompt {
    [CmdletBinding()]
    param(
        [ValidatePattern('^(?=.{1,253}$)[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*$')]
        [string]$Hostname = 'github.com',
        [string]$CacheDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GHCPSpendPrompt'),
        [ValidateRange(1, 1440)][int]$RefreshMinutes = 60,
        [ValidateRange(1, 60)][int]$RequestTimeoutSeconds = 15,
        [string]$GhExecutable = 'gh',
        [switch]$InstallHook
    )
    $command = Get-Command $GhExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $directory = [IO.Path]::GetFullPath($CacheDirectory)
    if ($directory -eq [IO.Path]::GetPathRoot($directory) -or
        $directory.TrimEnd('\') -eq $HOME.TrimEnd('\')) { throw 'Use a dedicated Copilot cache directory.' }
    if (Get-Variable -Name CopilotPromptState -Scope Script -ErrorAction SilentlyContinue) { Disable-CopilotPrompt }
    $script:CopilotPromptState = @{
        Options = @{
            Hostname = $Hostname.ToLowerInvariant(); CacheDirectory = $directory
            RefreshMinutes = $RefreshMinutes; RequestTimeoutSeconds = $RequestTimeoutSeconds
            GhExecutable = $command.Source; ScriptPath = $PSCommandPath
        }
        ContextKey = ''; Cache = $null; Lease = $null; Job = $null
        DiskReadAt = [DateTimeOffset]::MinValue; RetryAt = [DateTimeOffset]::MinValue
        LastDiagnostic = ''; DiskDiagnostic = ''; JobFailure = $false
        HookInstalled = $false; PreviousHook = $null; PreviousAlias = $null
    }
    if ($InstallHook) {
        $previous = Get-Command Set-PoshContext -ErrorAction SilentlyContinue
        if ($previous) {
            $script:CopilotPromptState.PreviousHook = if ($previous -is [Management.Automation.AliasInfo]) {
                $previous.ResolvedCommand
            }
            else { $previous }
        }
        $script:CopilotPromptState.PreviousAlias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
        New-Alias -Name Set-PoshContext -Value Invoke-CopilotPromptContext -Scope Global -Force
        $script:CopilotPromptState.HookInstalled = $true
    }
}

function Update-CopilotPrompt {
    [CmdletBinding()]
    param([DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $state = $null
    try {
        $state = Get-Variable CopilotPromptState -Scope Script -ValueOnly -ErrorAction SilentlyContinue
        if ($null -eq $state) { throw 'Initialize-CopilotPrompt must be called first.' }
        if ($null -ne $state.Job -and $state.Job.State -notin @('Running', 'NotStarted')) {
            try {
                Receive-Job $state.Job -ErrorAction Stop | Out-Null
                $state.JobFailure = $false
            }
            catch {
                $state.JobFailure = $true
                throw 'Copilot background refresh failed. Check cache directory access and gh installation.'
            }
            finally {
                Remove-Job $state.Job
                $state.Job = $null
                $state.DiskReadAt = [DateTimeOffset]::MinValue
            }
        }
        if (($Now - $state.DiskReadAt).TotalSeconds -ge 1) {
            $key = Get-CopilotCredentialContext $state.Options
            if ($key -cne $state.ContextKey) {
                $state.ContextKey = $key
                $state.RetryAt = [DateTimeOffset]::MinValue
                $state.JobFailure = $false
            }
            $path = Get-CopilotCachePath $state.Options $key
            $state.Cache = $null
            $state.Lease = $null
            $state.DiskDiagnostic = ''
            $state.DiskReadAt = $Now
            try { $state.Cache = Read-CopilotCache $path $state.Options $key }
            catch { $state.DiskDiagnostic = 'Copilot cache is invalid or unreadable; current spend is unavailable.' }
            try {
                $state.Lease = Read-CopilotDocument ($path + '.refresh') 2048
                [void](Test-CopilotRefreshLease $state.Lease $key $Now)
            }
            catch {
                $state.Lease = $null
                $state.DiskDiagnostic = 'Copilot refresh marker is invalid or unreadable.'
            }
        }
        $refreshing = ($null -ne $state.Job -and $state.Job.State -in @('Running', 'NotStarted')) -or
            (Test-CopilotRefreshLease $state.Lease $state.ContextKey $Now)
        $due = $null -eq $state.Cache -or (ConvertTo-CopilotUtc $state.Cache.nextAttemptUtc) -le $Now
        if ($due -and !$refreshing -and $Now -ge $state.RetryAt) {
            $state.RetryAt = $Now.AddMinutes(5)
            $state.Job = Start-CopilotRefreshJob $state.Options $state.ContextKey
            $refreshing = $true
        }
        $view = Get-CopilotPresentation $state.Cache $Now $state.Options.RefreshMinutes
        if ($state.JobFailure) { $view = Get-CopilotPresentation $null $Now }
        $env:COPILOT_SPEND = $view.Text
        $env:COPILOT_FORECAST = $view.Forecast
        $env:COPILOT_FORECAST_STATE = $view.ForecastState
        $env:COPILOT_CONNECTION_STATE = if ($refreshing) { 'in_progress' }
            elseif ($state.JobFailure -or $null -eq $state.Cache -or $state.Cache.status -ne 'fresh') { 'not_connected' }
            else { 'connected' }
        $diagnostic = if ($null -ne $state.Cache -and $state.Cache.status -eq 'unavailable') {
            $state.Cache.diagnostic
        }
        else { $state.DiskDiagnostic }
        if ($diagnostic -and $diagnostic -cne $state.LastDiagnostic) { Write-Warning $diagnostic }
        $state.LastDiagnostic = $diagnostic
    }
    catch {
        $env:COPILOT_SPEND = 'unavailable'
        $env:COPILOT_FORECAST = '?'
        $env:COPILOT_FORECAST_STATE = 'unknown'
        $env:COPILOT_CONNECTION_STATE = 'not_connected'
        $diagnostic = 'Copilot prompt update failed. Check initialization, cache access and gh installation.'
        if ($null -eq $state -or $state.LastDiagnostic -cne $diagnostic) { Write-Warning $diagnostic }
        if ($null -ne $state) { $state.LastDiagnostic = $diagnostic }
    }
    finally { $global:LASTEXITCODE = $savedExitCode }
}

function Invoke-CopilotPromptContext {
    param($OriginalStatus)
    $savedExitCode = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    try {
        if ($script:CopilotPromptState.PreviousHook) { & $script:CopilotPromptState.PreviousHook $OriginalStatus }
        Update-CopilotPrompt
    }
    finally { $global:LASTEXITCODE = $savedExitCode }
}

function Disable-CopilotPrompt {
    if (!(Get-Variable -Name CopilotPromptState -Scope Script -ErrorAction SilentlyContinue)) { return }
    $state = $script:CopilotPromptState
    if ($null -ne $state.Job) {
        Stop-Job $state.Job
        Remove-Job $state.Job
    }
    $alias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
    if ($state.HookInstalled -and $alias -and $alias.Definition -eq 'Invoke-CopilotPromptContext') {
        Remove-Alias Set-PoshContext -Scope Global -Force
        if ($state.PreviousAlias) {
            New-Alias Set-PoshContext $state.PreviousAlias.Definition -Scope Global -Force -Option $state.PreviousAlias.Options
        }
    }
    foreach ($name in @('COPILOT_SPEND', 'COPILOT_FORECAST', 'COPILOT_FORECAST_STATE', 'COPILOT_CONNECTION_STATE')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    Remove-Variable CopilotPromptState -Scope Script
}
