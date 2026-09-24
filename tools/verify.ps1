param([switch] $NativeTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$originalPath = $env:PATH
$installerTools = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $installerTools 'vswhere.exe')) { $env:PATH = "$installerTools;$env:PATH" }
Push-Location $root
try {
    & "$PSScriptRoot\test-release-tooling.ps1"
    dotnet build GHCPSpendTray.slnx -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    foreach ($name in @('GHCPSpendTray.Tests', 'GHCPSpendTray.PlatformTests', 'GHCPSpendTray.AppTests')) {
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
