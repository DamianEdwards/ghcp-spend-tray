#Requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$AppExecutable,
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [switch]$Render
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$helper = (Get-Command $AppExecutable -CommandType Application | Select-Object -First 1).Source
$fixture = (Get-Command $FixtureExecutable -CommandType Application | Select-Object -First 1).Source
$directory = Join-Path ([IO.Path]::GetTempPath()) ("GHCPPrompt-adapter ' `$() " + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$checks = 0
function Assert-Equal($Actual, $Expected, $Name) {
    if ($Actual -cne $Expected) { throw "$Name expected '$Expected', received '$Actual'." }
    $script:checks++
}
function Get-AppInitialization {
    # A development WinExe needs explicit pipes; installed console aliases also
    # support the documented direct profile pipeline.
    $info = [Diagnostics.ProcessStartInfo]::new($helper)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @('init', 'pwsh', '--data-dir', $directory)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(10000)) { $process.Kill(); throw 'App init command timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $stderr.GetAwaiter().GetResult()) { throw 'App init command failed.' }
        return $output
    }
    finally { $process.Dispose() }
}
try {
    & $fixture --prepare-cache $directory
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic cache fixture failed.' }
    $script:PreviousCalls = 0
    function global:Invoke-SyntheticContext { param($Status); $script:PreviousCalls++; $global:LASTEXITCODE = 99 }
    $previousAlias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
    New-Alias Set-PoshContext Invoke-SyntheticContext -Scope Global -Force
    $initialization = Get-AppInitialization
    Invoke-Expression $initialization
    Assert-Equal ((Get-Alias Set-PoshContext).Definition) 'Invoke-CopilotPromptContext' 'Emitted init installs hook'
    $global:LASTEXITCODE = 42
    Set-PoshContext $false
    Assert-Equal $global:LASTEXITCODE 42 'Exit status preserved'
    Assert-Equal $script:PreviousCalls 1 'Existing context composed'
    Assert-Equal $env:COPILOT_SPEND '$40 (~80%)' 'App custom budget consumed'
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'connected' 'Connection field'
    Invoke-Expression (Get-AppInitialization)
    Set-PoshContext $true
    Assert-Equal $script:PreviousCalls 2 'Repeated initialization is not recursive'
    Disable-CopilotPrompt
    Assert-Equal (Get-Alias Set-PoshContext).Definition 'Invoke-SyntheticContext' 'Previous alias restored'
    Assert-Equal ([string]::IsNullOrEmpty($env:COPILOT_SPEND)) $true 'Exports removed'
    if ($Render) {
        $theme = Join-Path $directory 'theme.omp.json'
        $segment = Get-Content (Join-Path $PSScriptRoot 'copilot.segment.json') -Raw | ConvertFrom-Json
        @{ version = 4; blocks = @(@{ type = 'prompt'; alignment = 'left'; segments = @($segment) }) } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $theme -Encoding utf8
        foreach ($case in @(@('green','166;227;161'), @('yellow','249;226;175'), @('red','243;139;168'),
            @('in_progress','ec4c'), @('not_connected','ec42'))) {
            & $fixture --prepare-cache $directory $case[0]
            Initialize-CopilotPrompt -AppExecutable $helper -DataDirectory $directory
            Update-CopilotPrompt
            $output = (oh-my-posh print primary --config $theme --shell pwsh) -join "`n"
            Assert-Equal $LASTEXITCODE 0 'Oh My Posh render exit'
            if ($case[0] -in @('green','yellow','red')) {
                Assert-Equal ($output.Contains($case[1])) $true 'Forecast ANSI color'
                Assert-Equal ($output.Contains([string][char]0xEC1E)) $true 'Connected Copilot glyph'
            }
            else {
                Assert-Equal ($output.Contains([string][char][Convert]::ToInt32($case[1],16))) $true 'Connection glyph'
            }
            Assert-Equal ($output.Contains([char]::ConvertFromUtf32(0xF15B6))) $true 'Chart glyph'
            Assert-Equal ($output.Contains($env:COPILOT_SPEND)) $true 'App presentation text'
        }
    }
    "PASS: $checks PowerShell adapter assertions against app-owned usage."
}
finally {
    Disable-CopilotPrompt
    Remove-Alias Set-PoshContext -Scope Global -Force -ErrorAction SilentlyContinue
    if ($previousAlias) { New-Alias Set-PoshContext $previousAlias.Definition -Scope Global -Force }
    Remove-Item Function:\Invoke-SyntheticContext -ErrorAction SilentlyContinue
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($directory)
}
$global:LASTEXITCODE = 0
