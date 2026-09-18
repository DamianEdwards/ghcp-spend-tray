param([switch] $NativeTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build GHSpend.slnx -c Release --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    foreach ($name in @('GHSpend.Tests', 'GHSpend.PlatformTests', 'GHSpend.AppTests')) {
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
finally { Pop-Location }
