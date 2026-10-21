param(
    [Parameter(Mandatory)]
    [ValidateSet('Setup', 'Run')]
    [string] $Phase,
    [Parameter(Mandatory)]
    [ValidateSet('default', 'ntfs', 'devdrive')]
    [string] $Layout
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This experiment runs only on disposable GitHub-hosted Windows runners.'
}

$reportDirectory = Join-Path $env:RUNNER_TEMP 'storage-benchmark'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timingPath = Join-Path $reportDirectory 'timings.json'
$timings = [Collections.Generic.List[object]]::new()
if (Test-Path -LiteralPath $timingPath) {
    foreach ($timing in (Get-Content -LiteralPath $timingPath -Raw | ConvertFrom-Json)) {
        $timings.Add($timing)
    }
}

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
            phase = $Name
            seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            success = $success
        })
        ConvertTo-Json -InputObject @($timings.ToArray()) -Depth 5 |
            Set-Content -LiteralPath $timingPath -Encoding utf8NoBOM
        "| $Layout | $env:STORAGE_SAMPLE | $Name | $($watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)) | $success |" |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
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

if ($Phase -eq 'Setup') {
    if ($env:BENCHMARK_ROOT) { throw 'Benchmark storage is already configured.' }
    Get-StorageSnapshot | ConvertTo-Json -Depth 7 |
        Set-Content -LiteralPath (Join-Path $reportDirectory 'storage-before.json') -Encoding utf8NoBOM
    @(
        "## Windows storage benchmark: $Layout / sample $env:STORAGE_SAMPLE"
        ''
        "Image: $env:ImageVersion. All variants start without an Actions NuGet cache."
        'Workloads run sequentially; later phases reuse earlier packages and build outputs.'
        'Dev Drive uses a dynamic VHD on D:, not a reformatted physical disk.'
        'Defender settings are observed, never changed; drive exclusions may differ.'
        ''
        '| Layout | Sample | Phase | Seconds | Success |'
        '|---|---|---|---:|---|'
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY

    $storageRoot = Join-Path ([IO.Path]::GetPathRoot($env:GITHUB_WORKSPACE)) 'storage-experiment'
    Measure-Phase 'storage-setup' {
        if ($Layout -ne 'default' -and [IO.Path]::GetPathRoot($storageRoot) -ne 'D:\') {
            throw 'The relocated layouts require the expected D: workspace disk; inspect storage-before.json.'
        }
        if (Test-Path -LiteralPath $storageRoot) { throw "Experiment directory already exists: $storageRoot" }
        if ($Layout -eq 'devdrive') {
            if (Get-Volume | Where-Object DriveLetter -eq 'R') { throw 'R: is already in use.' }
            if ((Get-Volume -DriveLetter D).SizeRemaining -lt 70GB) { throw 'D: needs at least 70 GiB free for the experiment.' }
            New-Item -ItemType Directory -Path $storageRoot | Out-Null
            $vhd = Join-Path $storageRoot 'devdrive.vhdx'
            $commands = Join-Path $reportDirectory 'create-vhd.txt'
            @(
                "create vdisk file=`"$vhd`" maximum=65536 type=expandable"
                "select vdisk file=`"$vhd`""
                'attach vdisk'
            ) | Set-Content -LiteralPath $commands -Encoding ascii
            & diskpart /s $commands
            if ($LASTEXITCODE -ne 0) { throw 'Could not create and attach the experiment VHD.' }
            $disk = Get-DiskImage -ImagePath $vhd | Get-Disk
            if ($disk.IsBoot -or $disk.IsSystem -or $disk.PartitionStyle -ne 'RAW') {
                throw 'Refusing to format anything except the new raw experiment VHD.'
            }
            $disk | Initialize-Disk -PartitionStyle GPT -PassThru |
                New-Partition -DriveLetter R -UseMaximumSize |
                Format-Volume -DevDrive -FileSystem ReFS -NewFileSystemLabel StorageBenchmark -Confirm:$false -Force |
                Out-Null
            & fsutil devdrv query R:
            if ($LASTEXITCODE -ne 0) { throw 'The experiment volume is not a Dev Drive.' }
            $script:storageRoot = 'R:\storage-experiment'
        }
        New-Item -ItemType Directory -Path $storageRoot -Force | Out-Null
    }
    $benchmarkRoot = Join-Path $storageRoot 'source'
    Measure-Phase 'workspace-copy' {
        & git clone --quiet --local --no-hardlinks $env:GITHUB_WORKSPACE $benchmarkRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not clone the pinned experiment source.' }
        $sourceCommit = & git -C $benchmarkRoot rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $env:GITHUB_SHA) {
            throw 'Experiment source does not match the workflow commit.'
        }
    }
    "BENCHMARK_ROOT=$benchmarkRoot" | Add-Content -LiteralPath $env:GITHUB_ENV
    if ($Layout -ne 'default') {
        foreach ($entry in @{
            TEMP = 'temp'
            TMP = 'temp'
            NUGET_PACKAGES = 'nuget\packages'
            NUGET_HTTP_CACHE_PATH = 'nuget\http-cache'
            NUGET_SCRATCH = 'nuget\scratch'
            NUGET_PLUGINS_CACHE_PATH = 'nuget\plugins-cache'
        }.GetEnumerator()) {
            $path = Join-Path $storageRoot $entry.Value
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            "$($entry.Key)=$path" | Add-Content -LiteralPath $env:GITHUB_ENV
        }
    }
    exit 0
}

if (-not $env:BENCHMARK_ROOT -or (Split-Path $PSScriptRoot -Parent) -ne $env:BENCHMARK_ROOT) {
    throw 'Run the workload from the isolated benchmark checkout after Setup.'
}
Get-StorageSnapshot | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path $reportDirectory 'storage-workload.json') -Encoding utf8NoBOM
& dotnet nuget locals all --list |
    Set-Content -LiteralPath (Join-Path $reportDirectory 'nuget-locations.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not identify NuGet cache paths.' }
Push-Location $env:BENCHMARK_ROOT
try {
    Measure-Phase 'native-tests' { & "$PSScriptRoot\verify.ps1" -NativeTests }
    Measure-Phase 'self-contained-package' { & "$PSScriptRoot\package.ps1" }
    Measure-Phase 'store-package' { & "$PSScriptRoot\package.ps1" -Store }
    Measure-Phase 'self-contained-after-store' {
        & dotnet build src\GHCPSpendTray.App\GHCPSpendTray.App.csproj -c Release -r win-x64 --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained build after Store packaging failed.' }
    }
}
finally { Pop-Location }
