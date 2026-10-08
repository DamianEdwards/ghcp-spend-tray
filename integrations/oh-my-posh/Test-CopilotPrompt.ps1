#Requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$HelperExecutable,
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [switch]$Render
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$global:LASTEXITCODE = 0
. (Join-Path $PSScriptRoot 'CopilotPrompt.ps1')
$helper = (Get-Command $HelperExecutable -CommandType Application | Select-Object -First 1).Source
$fixture = (Get-Command $FixtureExecutable -CommandType Application | Select-Object -First 1).Source
$directory = Join-Path ([IO.Path]::GetTempPath()) ('GHCPPrompt-adapter-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$saved = @{}
foreach ($name in @('GH_TOKEN','GITHUB_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','GH_CONFIG_DIR')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:GH_TOKEN = 'synthetic-token'; $env:GH_CONFIG_DIR = $directory
$env:GITHUB_TOKEN = ''; $env:GH_ENTERPRISE_TOKEN = ''; $env:GITHUB_ENTERPRISE_TOKEN = ''
$checks = 0
function Assert-Equal($Actual, $Expected, $Name) {
    if ($Actual -cne $Expected) { throw "$Name expected '$Expected', received '$Actual'." }
    $script:checks++
}
try {
    & $fixture --prepare-cache $directory $helper
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic cache fixture failed.' }
    $script:PreviousCalls = 0
    function global:Invoke-SyntheticContext { param($Status); $script:PreviousCalls++; $global:LASTEXITCODE = 99 }
    $previousAlias = Get-Alias Set-PoshContext -Scope Global -ErrorAction SilentlyContinue
    New-Alias Set-PoshContext Invoke-SyntheticContext -Scope Global -Force
    Initialize-CopilotPrompt -HelperExecutable $helper -GhExecutable $helper -CacheDirectory $directory -InstallHook
    $global:LASTEXITCODE = 42
    Set-PoshContext $false
    Assert-Equal $global:LASTEXITCODE 42 'Exit status preserved'
    Assert-Equal $script:PreviousCalls 1 'Existing context composed'
    Assert-Equal $env:COPILOT_SPEND '$40 (~40%)' 'Published helper output consumed'
    Assert-Equal $env:COPILOT_CONNECTION_STATE 'connected' 'Connection field'
    Initialize-CopilotPrompt -HelperExecutable $helper -GhExecutable $helper -CacheDirectory $directory -InstallHook
    Set-PoshContext $true
    Assert-Equal $script:PreviousCalls 2 'Repeated initialization is not recursive'
    Disable-CopilotPrompt
    Assert-Equal (Get-Alias Set-PoshContext).Definition 'Invoke-SyntheticContext' 'Previous alias restored'
    Assert-Equal ([string]::IsNullOrEmpty($env:COPILOT_SPEND)) $true 'Exports removed'
    if ($Render) {
        $theme = Join-Path $directory 'theme.omp.json'
        & $helper demo-theme (Join-Path $PSScriptRoot 'copilot.segment.json') $theme
        foreach ($case in @(@('green','166;227;161'), @('yellow','249;226;175'), @('red','243;139;168'),
            @('in_progress','ec4c'), @('not_connected','ec3e'))) {
            $fields = (& $helper demo $case[0]) -split "`t"
            $env:COPILOT_SPEND, $env:COPILOT_FORECAST, $env:COPILOT_FORECAST_STATE, $env:COPILOT_CONNECTION_STATE = $fields[1..4]
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
            Assert-Equal ($output.Contains($fields[1])) $true 'Published presentation text'
        }
    }
    "PASS: $checks PowerShell adapter assertions against published helper."
}
finally {
    Disable-CopilotPrompt
    Remove-Alias Set-PoshContext -Scope Global -Force -ErrorAction SilentlyContinue
    if ($previousAlias) { New-Alias Set-PoshContext $previousAlias.Definition -Scope Global -Force }
    Remove-Item Function:\Invoke-SyntheticContext -ErrorAction SilentlyContinue
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($directory)
}
$global:LASTEXITCODE = 0
