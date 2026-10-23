param([Parameter(Mandatory)][string]$AppExecutable, [switch]$Render)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$application = (Resolve-Path -LiteralPath $AppExecutable).Path
Push-Location $root
try {
    dotnet build tests\GHCPSpendTray.PromptTests\GHCPSpendTray.PromptTests.csproj -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Usage query test build failed.' }
    $fixture = Join-Path $root 'tests\GHCPSpendTray.PromptTests\bin\Release\net10.0\GHCPSpendTray.PromptTests.exe'
    & $fixture --app $application
    if ($LASTEXITCODE -ne 0) { throw 'App command-line query tests failed.' }
    & "$root\integrations\oh-my-posh\Test-CopilotPrompt.ps1" -AppExecutable $application -FixtureExecutable $fixture -Render:$Render
}
finally { Pop-Location }
