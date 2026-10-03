$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$helper = Join-Path $PSScriptRoot 'get-windows-app-runtime.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-runtime-" + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $fixture 'packages'
$package = Join-Path $cache 'microsoft.windowsappsdk.runtime\2.5.1'
$assetsPath = Join-Path $fixture 'project.assets.json'
$publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
New-Item -ItemType Directory -Path (Join-Path $package 'buildTransitive') -Force | Out-Null
$referencePath = Join-Path $package 'buildTransitive\Microsoft.WindowsAppSDK.AppXReference.props'
Set-Content -LiteralPath $referencePath -Value @'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><WinAppSDKPackageName>Microsoft.WindowsAppRuntime.2</WinAppSDKPackageName></PropertyGroup>
</Project>
'@
function Write-Assets([hashtable] $Libraries) {
    @{
        libraries = $Libraries
        packageFolders = @{ (Join-Path $fixture 'missing-cache') = @{}; $cache = @{} }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $assetsPath
}
function Write-Framework(
    [string] $Architecture = 'x64',
    [string] $Name = 'Microsoft.WindowsAppRuntime.2',
    [string] $Publisher = $publisher,
    [string] $Version = '2.5.1.0',
    [string] $DeclaredArchitecture = $Architecture,
    [string] $Framework = 'true'
) {
    $directory = Join-Path $package "tools\MSIX\win10-$Architecture"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $stream = [IO.File]::Create((Join-Path $directory 'Microsoft.WindowsAppRuntime.2.msix'))
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('AppxManifest.xml').Open())
        try {
            $writer.Write(@"
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="$Name" Publisher="$Publisher" Version="$Version" ProcessorArchitecture="$DeclaredArchitecture" />
  <Properties><Framework>$Framework</Framework></Properties>
</Package>
"@)
        }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}
function Assert-Rejected([string] $ExpectedError) {
    try { & $helper -Architecture x64 -AssetsPath $assetsPath | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedError*") { throw }
        return
    }
    throw "Invalid Windows App Runtime metadata was accepted; expected: $ExpectedError"
}
try {
    $libraries = @{ 'Microsoft.WindowsAppSDK.Runtime/2.5.1' = @{ path = 'microsoft.windowsappsdk.runtime/2.5.1' } }
    Write-Assets $libraries
    foreach ($architecture in @('x64', 'arm64')) {
        Write-Framework -Architecture $architecture
        $runtime = & $helper -Architecture $architecture -AssetsPath $assetsPath
        if ($runtime.Name -cne 'Microsoft.WindowsAppRuntime.2' -or $runtime.Publisher -cne $publisher -or
            $runtime.MinVersion -cne '2.5.1.0' -or $runtime.Architecture -cne $architecture -or
            -not (Test-Path -LiteralPath $runtime.PackagePath)) {
            throw 'Runtime framework metadata was not derived from the resolved architecture package.'
        }
    }
    Write-Framework -Version '2.6.3.0'
    if ((& $helper -Architecture x64 -AssetsPath $assetsPath).MinVersion -cne '2.6.3.0') {
        throw 'Runtime dependency version was hardcoded instead of derived from the framework manifest.'
    }
    foreach ($change in @(
        @{ Name = 'Microsoft.WindowsAppRuntime.999' },
        @{ Publisher = 'CN=Synthetic publisher' },
        @{ Version = 'invalid' },
        @{ Version = '0.0.0.0' },
        @{ DeclaredArchitecture = 'arm64' },
        @{ Framework = 'false' }
    )) {
        Write-Framework @change
        Assert-Rejected 'Unexpected Windows App Runtime framework'
    }
    Write-Framework
    Write-Assets @{}
    Assert-Rejected 'requires exactly one resolved'
    Write-Assets ($libraries + @{ 'Microsoft.WindowsAppSDK.Runtime/2.4.0' = @{ path = 'missing' } })
    Assert-Rejected 'requires exactly one resolved'
    Write-Assets @{ 'Microsoft.WindowsAppSDK.Runtime/2.5.1' = @{ path = 'missing' } }
    Assert-Rejected 'NuGet package is missing'
    Write-Assets $libraries
    Set-Content -LiteralPath $referencePath -Value '<Project />'
    Assert-Rejected 'does not declare a supported framework identity'
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
Write-Output 'PASS: resolved x64/ARM64 framework identity, version updates, missing/ambiguous runtime and invalid package metadata.'
