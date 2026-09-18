param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    foreach ($rid in $Runtime) {
        $output = Join-Path $root "artifacts\publish\$rid"
        dotnet publish src\GHSpend.App\GHSpend.App.csproj -c Release -r $rid -o $output --nologo -v:minimal -p:IlcTreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed for $rid." }
        $exe = Join-Path $output 'ghspend.exe'
        $bytes = [System.IO.File]::ReadAllBytes($exe)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        $expected = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
        if ($machine -ne $expected) { throw "Unexpected PE architecture for $rid." }
        # The CLR runtime header must be empty in a native executable.
        $optional = $peOffset + 24
        $clrRva = [BitConverter]::ToUInt32($bytes, $optional + 112 + (14 * 8))
        if ($clrRva -ne 0) { throw "$rid still contains a CLR runtime header." }
        Get-FileHash $exe -Algorithm SHA256 | Select-Object Path, Hash
    }
}
finally { Pop-Location }
