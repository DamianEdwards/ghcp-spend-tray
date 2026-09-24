param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),
    [string] $Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$originalPath = $env:PATH
$release = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
$installerTools = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $installerTools 'vswhere.exe')) { $env:PATH = "$installerTools;$env:PATH" }
Push-Location $root
try {
    foreach ($rid in $Runtime) {
        $output = Join-Path $root "artifacts\publish\$rid"
        if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
        dotnet publish src\GHCPSpendTray.App\GHCPSpendTray.App.csproj -c Release -r $rid -o $output --nologo -v:minimal -p:IlcTreatWarningsAsErrors=true "-p:Version=$Version" "-p:FileVersion=$($release.PackageVersion)"
        if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed for $rid." }
        $exe = Join-Path $output 'GHCPSpendTray.exe'
        $bytes = [System.IO.File]::ReadAllBytes($exe)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        $expected = if ($rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
        if ($machine -ne $expected) { throw "Unexpected PE architecture for $rid." }
        # The CLR runtime header must be empty in a native executable.
        $optional = $peOffset + 24
        $clrRva = [BitConverter]::ToUInt32($bytes, $optional + 112 + (14 * 8))
        if ($clrRva -ne 0) { throw "$rid still contains a CLR runtime header." }
        foreach ($required in @('GHCPSpendTray.pri', 'Reactor.pri', 'Microsoft.UI.Xaml.dll', 'DWriteCore.dll', 'Assets\GHCPSpendTray.ico')) {
            if (-not (Test-Path (Join-Path $output $required))) { throw "Missing Reactor runtime resource: $required" }
        }
        $unwanted = @(Get-ChildItem -LiteralPath $output -File | Where-Object {
            $_.Name -match '^(onnxruntime.*|DirectML|Microsoft\.Windows\.(AI|Search|Widgets).*|Microsoft\.Asg\.SemanticIndex.*)\.dll$'
        })
        if ($unwanted.Count) {
            throw "Unexpected optional SDK payload: $($unwanted.Name -join ', '). Clean this architecture's publish directory if it contains files from an older build."
        }
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $output 'LICENSE.txt') -Force
        $assets = Get-Content (Join-Path $root 'src\GHCPSpendTray.App\obj\project.assets.json') -Raw | ConvertFrom-Json
        $unwantedPackages = @($assets.libraries.PSObject.Properties.Name | Where-Object {
            $_ -match '^Microsoft\.WindowsAppSDK(/|\.(AI|ML|Search|Widgets)/)'
        })
        if ($unwantedPackages.Count) {
            throw "The slim build must not reference optional SDK packages: $($unwantedPackages -join ', ')"
        }
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $package = $null
            foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                $candidate = Join-Path $folder ($library.Value.path.Replace('/', '\'))
                if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
            }
            if (-not $package) { throw "Resolved package directory missing: $($library.Name)" }
            $notices = Get-ChildItem -LiteralPath $package -File |
                Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|THIRD.PARTY)' -or $_.Extension -eq '.nuspec' }
            if ($notices) {
                $destination = Join-Path $output ("licenses\" + $library.Name.Replace('/', '-'))
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                $notices | Copy-Item -Destination $destination -Force
            }
        }
        $symbols = Join-Path $root "artifacts\symbols\$rid"
        New-Item -ItemType Directory -Path $symbols -Force | Out-Null
        Get-ChildItem -LiteralPath $output -File -Filter '*.pdb' | Move-Item -Destination $symbols -Force
        Get-FileHash $exe -Algorithm SHA256 | Select-Object Path, Hash
        Write-Output "Self-contained package payload: $output"
        $payloadBytes = (Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object Length -Sum).Sum
        Write-Output ("Payload: {0:N2} MiB; debug symbols: {1}" -f ($payloadBytes / 1MB), $symbols)
    }
}
finally { Pop-Location; $env:PATH = $originalPath }
