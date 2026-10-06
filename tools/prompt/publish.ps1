param(
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [switch]$Tests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$originalPath = $env:PATH
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ tools and the Windows SDK.' }
$installation = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (!$installation) { throw 'Visual Studio C++ build tools were not found.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'amd64_arm64' } else { 'amd64' }
$dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$output = Join-Path $root "artifacts\prompt\$RuntimeIdentifier"
Push-Location $root
try {
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + (Split-Path $dotnet -Parent) +
        ";$env:WINDIR\System32;$env:WINDIR;$env:WINDIR\System32\Wbem"
    & $env:ComSpec /c "call `"$vcvars`" $architecture >nul && `"$dotnet`" publish src\GHCPSpendTray.Prompt\GHCPSpendTray.Prompt.csproj -c Release -r $RuntimeIdentifier -p:IlcTreatWarningsAsErrors=true --nologo -v:q -o `"$output`""
    if ($LASTEXITCODE -ne 0) { throw 'Native helper publish failed.' }
    if ($Tests) {
        $testOutput = Join-Path $root "artifacts\tests\prompt\$RuntimeIdentifier"
        & $env:ComSpec /c "call `"$vcvars`" $architecture >nul && `"$dotnet`" publish tests\GHCPSpendTray.PromptTests\GHCPSpendTray.PromptTests.csproj -c Release -r $RuntimeIdentifier -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true --nologo -v:q -o `"$testOutput`""
        if ($LASTEXITCODE -ne 0) { throw 'Native harness publish failed.' }
    }
    foreach ($name in @('CopilotPrompt.ps1','CopilotPrompt.bash','CopilotPrompt.zsh','copilot.segment.json','demo.ps1','demo.bash','README.md')) {
        Copy-Item -LiteralPath (Join-Path $root "integrations\oh-my-posh\$name") -Destination $output -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output -Force
    Write-Output "Standalone Native AOT bundle: $output"
}
finally { Pop-Location; $env:PATH = $originalPath }
