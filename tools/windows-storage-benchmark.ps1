param(
    [Parameter(Mandatory)]
    [ValidateSet('Compare', 'Run')]
    [string] $Phase,
    [ValidateSet('Tests', 'Packaging')]
    [string] $Workload = 'Tests',
    [ValidateSet('baseline-first', 'relocated-first')]
    [string] $Order = 'baseline-first',
    [ValidateSet('baseline', 'relocated')]
    [string] $Layout = 'baseline',
    [ValidateSet('cold', 'warm')]
    [string] $CacheState = 'cold'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This experiment runs only on disposable GitHub-hosted Windows runners.'
}

$reportDirectory = if ($Phase -eq 'Compare') {
    Join-Path $env:RUNNER_TEMP 'storage-benchmark'
} else {
    if (-not $env:BENCHMARK_REPORT_DIRECTORY -or -not $env:BENCHMARK_ROOT -or
        (Split-Path $PSScriptRoot -Parent) -ne $env:BENCHMARK_ROOT) {
        throw 'Run the workload only through Compare in an isolated checkout.'
    }
    $env:BENCHMARK_REPORT_DIRECTORY
}
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timingPath = Join-Path $reportDirectory 'timings.json'
$timings = [Collections.Generic.List[object]]::new()

function Measure-Phase([string] $Name, [scriptblock] $Action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $success = $false
    try {
        & $Action
        $success = $true
    }
    finally {
        $watch.Stop()
        $timings.Add([pscustomobject]@{
            layout = $Layout
            cache = $CacheState
            phase = $Name
            seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            success = $success
        })
        ConvertTo-Json -InputObject @($timings.ToArray()) -Depth 5 |
            Set-Content -LiteralPath $timingPath -Encoding utf8NoBOM
        "| $Layout | $CacheState | $Name | $($watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)) | $success |" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}

function Get-PairedResults([object[]] $Runs) {
    foreach ($cache in @('cold', 'warm')) {
        $baseline = @($Runs | Where-Object { $_.layout -eq 'baseline' -and $_.cache -eq $cache })
        $relocated = @($Runs | Where-Object { $_.layout -eq 'relocated' -and $_.cache -eq $cache })
        if ($baseline.Count -ne 1 -or $relocated.Count -ne 1 -or
            $baseline[0].seconds -le 0 -or $relocated[0].seconds -le 0 -or
            -not $baseline[0].success -or -not $relocated[0].success) {
            throw "Incomplete or failed paired measurements: $cache"
        }
        [pscustomobject]@{
            cache = $cache
            baselineSeconds = $baseline[0].seconds
            relocatedSeconds = $relocated[0].seconds
            savedSeconds = [Math]::Round($baseline[0].seconds - $relocated[0].seconds, 3)
            savedPercent = [Math]::Round(100 * (1 - $relocated[0].seconds / $baseline[0].seconds), 3)
        }
    }
}

function Get-ComparisonPasses([string] $FirstLayout) {
    $other = if ($FirstLayout -eq 'baseline') { 'relocated' } else { 'baseline' }
    @(
        @{ layout = $FirstLayout; cache = 'cold' }
        @{ layout = $other; cache = 'cold' }
        @{ layout = $other; cache = 'warm' }
        @{ layout = $FirstLayout; cache = 'warm' }
    )
}

function Get-StorageSnapshot {
    $defender = Get-MpComputerStatus
    $preferences = Get-MpPreference
    [ordered]@{
        image = $env:ImageVersion
        imageOS = $env:ImageOS
        architecture = $env:RUNNER_ARCH
        processor = (Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        workspace = $env:GITHUB_WORKSPACE
        benchmarkRoot = $env:BENCHMARK_ROOT
        runnerTemp = $env:RUNNER_TEMP
        temp = $env:TEMP
        nugetPackages = $env:NUGET_PACKAGES
        nugetHttpCache = $env:NUGET_HTTP_CACHE_PATH
        disks = @(Get-Disk | Select-Object Number, FriendlyName, BusType, PartitionStyle, Size, IsBoot, IsSystem)
        volumes = @(Get-Volume | Where-Object DriveLetter |
            Select-Object DriveLetter, FileSystem, FileSystemLabel, Size, SizeRemaining)
        defender = @{
            realTimeProtection = $defender.RealTimeProtectionEnabled
            performanceMode = $preferences.PerformanceModeStatus
            exclusionPaths = $preferences.ExclusionPath
        }
    }
}

Get-StorageSnapshot | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path $reportDirectory 'storage.json') -Encoding utf8NoBOM

if ($Phase -eq 'Compare') {
    if ([IO.Path]::GetPathRoot($env:GITHUB_WORKSPACE) -ne 'D:\' -or
        [IO.Path]::GetPathRoot($env:TEMP) -ne 'C:\') {
        throw 'Expected a D: workspace and C: default temp; inspect storage.json before adapting this experiment.'
    }
    foreach ($drive in @('C', 'D')) {
        if ((Get-Volume -DriveLetter $drive).FileSystem -ne 'NTFS') { throw "$drive`: is not NTFS." }
        if (Test-Path -LiteralPath "$drive`:\storage-experiment") {
            throw "Experiment directory already exists on $drive`:"
        }
    }
    @(
        "## Paired temp/NuGet benchmark: $Workload / $Order / sample $env:STORAGE_SAMPLE"
        ''
        "Image: $env:ImageVersion. Both layouts execute on this same runner."
        'Only TEMP/TMP and NuGet cache locations change: isolated C: versus D: directories.'
        'Every pass uses a clean source checkout on D:. Warm means NuGet caches, not build outputs.'
        'Cold layouts run in the chosen order; warm layouts run in reverse order.'
        'No Actions cache, disk formatting, Defender changes, signing, submission, or UI smoke.'
        ''
        '| Layout | Cache | Phase | Seconds | Success |'
        '|---|---|---|---:|---|'
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    $first = if ($Order -eq 'baseline-first') { 'baseline' } else { 'relocated' }
    $passes = Get-ComparisonPasses $first
    foreach ($pass in $passes) {
        $Layout = $pass.layout
        $CacheState = $pass.cache
        $cacheRoot = if ($Layout -eq 'baseline') { 'C:\storage-experiment' } else { 'D:\storage-experiment' }
        Measure-Phase 'cache-directories' {
            if ($CacheState -eq 'cold' -and (Test-Path -LiteralPath $cacheRoot)) {
                throw "Cold cache already exists: $cacheRoot"
            }
            if ($CacheState -eq 'warm' -and -not (Test-Path -LiteralPath "$cacheRoot\nuget\packages")) {
                throw "Warm cache does not exist: $cacheRoot"
            }
            foreach ($directory in @('temp', 'nuget\packages', 'nuget\http-cache', 'nuget\scratch', 'nuget\plugins-cache')) {
                New-Item -ItemType Directory -Path (Join-Path $cacheRoot $directory) -Force | Out-Null
            }
        }
        $benchmarkRoot = Join-Path $env:RUNNER_TEMP "storage-benchmark-source-$Layout-$CacheState"
        Measure-Phase 'workspace-copy' {
            & git clone --quiet --local --no-hardlinks $env:GITHUB_WORKSPACE $benchmarkRoot
            if ($LASTEXITCODE -ne 0) { throw 'Could not clone the pinned experiment source.' }
            $sourceCommit = & git -C $benchmarkRoot rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $env:GITHUB_SHA) {
                throw 'Experiment source does not match the workflow commit.'
            }
        }
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $benchmarkRoot
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File',
            "$benchmarkRoot\tools\windows-storage-benchmark.ps1", '-Phase', 'Run',
            '-Workload', $Workload, '-Layout', $Layout, '-CacheState', $CacheState)) {
            $start.ArgumentList.Add($argument)
        }
        foreach ($entry in @{
            TEMP = "$cacheRoot\temp"
            TMP = "$cacheRoot\temp"
            NUGET_PACKAGES = "$cacheRoot\nuget\packages"
            NUGET_HTTP_CACHE_PATH = "$cacheRoot\nuget\http-cache"
            NUGET_SCRATCH = "$cacheRoot\nuget\scratch"
            NUGET_PLUGINS_CACHE_PATH = "$cacheRoot\nuget\plugins-cache"
            BENCHMARK_ROOT = $benchmarkRoot
            BENCHMARK_REPORT_DIRECTORY = (Join-Path $reportDirectory "$Layout-$CacheState")
        }.GetEnumerator()) {
            $start.Environment[$entry.Key] = $entry.Value
        }
        Measure-Phase 'workload-process' {
            $process = [Diagnostics.Process]::Start($start)
            try {
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) { throw "Workload failed: $Layout / $CacheState ($($process.ExitCode))" }
            }
            finally { $process.Dispose() }
        }
    }
    $pairs = @(Get-PairedResults @($timings | Where-Object phase -eq 'workload-process'))
    [ordered]@{
        workload = $Workload
        order = $Order
        sample = $env:STORAGE_SAMPLE
        pairs = $pairs
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $reportDirectory 'comparison.json') -Encoding utf8NoBOM
    @('', '### Within-runner savings (positive is faster on D:)', '',
        '| NuGet cache | C: seconds | D: seconds | Saved seconds | Saved percent |',
        '|---|---:|---:|---:|---:|') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    foreach ($pair in $pairs) {
        "| $($pair.cache) | $($pair.baselineSeconds) | $($pair.relocatedSeconds) | $($pair.savedSeconds) | $($pair.savedPercent)% |" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
    exit 0
}

& dotnet nuget locals all --list |
    Set-Content -LiteralPath (Join-Path $reportDirectory 'nuget-locations.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not identify NuGet cache paths.' }
Push-Location $env:BENCHMARK_ROOT
try {
    if ($Workload -eq 'Tests') {
        Measure-Phase 'native-tests' { & "$PSScriptRoot\verify.ps1" -NativeTests }
    } else {
        Measure-Phase 'self-contained-package' { & "$PSScriptRoot\package.ps1" }
        Measure-Phase 'store-package' { & "$PSScriptRoot\package.ps1" -Store }
        Measure-Phase 'self-contained-after-store' {
            & dotnet build src\GHCPSpendTray.App\GHCPSpendTray.App.csproj -c Release -r win-x64 --nologo -v:q
            if ($LASTEXITCODE -ne 0) { throw 'Self-contained build after Store packaging failed.' }
        }
    }
}
finally { Pop-Location }
