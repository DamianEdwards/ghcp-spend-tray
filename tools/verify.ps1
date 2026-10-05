param(
    [switch] $NativeTests,
    [ValidateSet('All', 'Application', 'CorePlatformShared')]
    [string] $TestShard = 'All'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$originalPath = $env:PATH
$installerTools = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $installerTools 'vswhere.exe')) { $env:PATH = "$installerTools;$env:PATH" }
Push-Location $root
try {
    if ($TestShard -ne 'Application') { & "$PSScriptRoot\test-release-tooling.ps1" }
    $buildTarget = if ($TestShard -eq 'Application') {
        'tests\GHCPSpendTray.AppTests\GHCPSpendTray.AppTests.csproj'
    } else { 'GHCPSpendTray.slnx' }
    dotnet build $buildTarget -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildTarget" }
    $names = switch ($TestShard) {
        'Application' { @('GHCPSpendTray.AppTests') }
        'CorePlatformShared' { @('GHCPSpendTray.Tests', 'GHCPSpendTray.PlatformTests', 'GHCPSpendTray.SharedTests') }
        'All' { @('GHCPSpendTray.Tests', 'GHCPSpendTray.PlatformTests', 'GHCPSpendTray.AppTests', 'GHCPSpendTray.SharedTests') }
    }
    foreach ($name in $names) {
        $project = "tests\$name\$name.csproj"
        dotnet run --project $project -c Release --no-build | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { throw "$name failed." }
        if ($NativeTests) {
            $output = Join-Path $root "artifacts\tests\$name"
            dotnet publish $project -c Release -r win-x64 -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true -o $output --nologo -v:q
            if ($LASTEXITCODE -ne 0) { throw "$name Native AOT publish failed." }
            & (Join-Path $output "$name.exe") | Select-Object -Last 1
            if ($LASTEXITCODE -ne 0) { throw "$name Native AOT execution failed." }
        }
    }
}
finally { Pop-Location; $env:PATH = $originalPath }
