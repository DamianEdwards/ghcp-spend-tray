param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [string] $PublisherDisplayName,
    [switch] $RequireSigned,
    [switch] $Store,
    [string] $RuntimeAssetsPath = "$PSScriptRoot\..\artifacts\publish-store\project.assets.json"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expected = & "$PSScriptRoot\get-release-version.ps1" -Version $Version
$bundlePath = (Resolve-Path -LiteralPath $Bundle).Path
if ($RequireSigned) {
    $signtool = & "$PSScriptRoot\get-windows-sdk-tool.ps1" -Name signtool.exe
    & $signtool verify /pa /all /v $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'Bundle signature verification failed.' }
}
function Read-ZipText($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Missing package entry: $Name" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Assert-Identity($Identity) {
    if ($Identity.Name -cne $IdentityName -or $Identity.Publisher -cne $Publisher -or
        $Identity.Version -ne $expected.PackageVersion) { throw 'Unexpected package identity or version.' }
}
$archive = [IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    [xml]$bundleManifest = Read-ZipText $archive 'AppxMetadata/AppxBundleManifest.xml'
    Assert-Identity $bundleManifest.Bundle.Identity
    $packages = @($bundleManifest.Bundle.Packages.Package)
    if ($packages.Count -ne 2 -or ($packages.Architecture | Sort-Object) -join ',' -ne 'arm64,x64') {
        throw 'The bundle must contain exactly x64 and ARM64 application packages.'
    }
    foreach ($package in $packages) {
        $entry = $archive.GetEntry($package.FileName)
        if ($null -eq $entry) { throw 'Bundle payload is missing.' }
        $stream = [IO.MemoryStream]::new()
        $source = $entry.Open()
        try { $source.CopyTo($stream) } finally { $source.Dispose() }
        $stream.Position = 0
        $app = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
        try {
            [xml]$manifest = Read-ZipText $app 'AppxManifest.xml'
            Assert-Identity $manifest.Package.Identity
            if ($PublisherDisplayName -and $manifest.Package.Properties.PublisherDisplayName -cne $PublisherDisplayName) {
                throw 'Unexpected publisher display name.'
            }
            if ($manifest.Package.Identity.ProcessorArchitecture -ne $package.Architecture) { throw 'Architecture mismatch.' }
            $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
            $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
            $ns.AddNamespace('d', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
            $ns.AddNamespace('u10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
            $dependencies = @($manifest.SelectNodes('//f:Dependencies/f:PackageDependency', $ns))
            if ($Store) {
                $runtime = & "$PSScriptRoot\get-windows-app-runtime.ps1" -Architecture $package.Architecture -AssetsPath $RuntimeAssetsPath
                if ($dependencies.Count -ne 1 -or $dependencies[0].Name -cne $runtime.Name -or
                    $dependencies[0].Publisher -cne $runtime.Publisher -or
                    $dependencies[0].MinVersion -cne $runtime.MinVersion -or
                    $dependencies[0].GetAttribute('ProcessorArchitecture') -cnotin @('', $runtime.Architecture)) {
                    throw 'Store packages must declare the exact resolved Windows App Runtime framework dependency.'
                }
                foreach ($name in @('Microsoft.UI.Xaml.dll', 'DWriteCore.dll', 'Microsoft.WindowsAppRuntime.dll',
                    'Microsoft.UI.Xaml/Themes/generic.xbf', 'Microsoft.UI.Xaml/Themes/themeresources.xbf')) {
                    if ($name -in $app.Entries.FullName) { throw "Store packages must not bundle Windows App Runtime payload: $name" }
                }
            } elseif ($dependencies.Count -ne 0) {
                throw 'Self-contained packages must not declare framework dependencies.'
            }
            $startup = $manifest.SelectSingleNode('//d:StartupTask', $ns)
            if ($null -eq $startup -or $startup.TaskId -ne 'GHCPSpendTrayStartup' -or $startup.Enabled -ne 'false' -or
                $startup.ParentNode.GetAttribute('Parameters', $ns.LookupNamespace('u10')) -ne '--startup') {
                throw 'Startup must be opt-in and launch with --startup.'
            }
            $application = $manifest.SelectSingleNode('//f:Application', $ns)
            if ($application.Executable -ne 'GHCPSpendTray.exe' -or
                $application.GetAttribute('TrustLevel', $ns.LookupNamespace('u10')) -ne 'mediumIL') { throw 'Expected a full-trust desktop application.' }
            $requiredFiles = @('GHCPSpendTray.exe', 'GHCPSpendTray.pri', 'Reactor.pri', 'resources.pri',
                'Assets/GHCPSpendTray.ico', 'Assets/Square44x44Logo.png',
                'Assets/Square150x150Logo.png', 'Assets/StoreLogo.png', 'LICENSE.txt')
            if ($Store) { $requiredFiles += 'Microsoft.WindowsAppRuntime.Bootstrap.dll' }
            else { $requiredFiles += @('Microsoft.UI.Xaml.dll', 'DWriteCore.dll') }
            foreach ($name in $requiredFiles) {
                if ($name -notin $app.Entries.FullName) { throw "Missing application payload: $name" }
            }
            $iconLayout = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-package-icons-" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $iconLayout 'Assets') -Force | Out-Null
            try {
                foreach ($iconEntry in $app.Entries | Where-Object {
                    $_.FullName -cmatch '^(AppxManifest\.xml|resources\.pri|Assets/[^/\\]+\.png|Reactor/Hosting/ReactorApplication\.xbf|Microsoft\.UI\.Xaml/Themes/(generic|themeresources)\.xbf)$'
                }) {
                    $destination = Join-Path $iconLayout $iconEntry.FullName.Replace('/', '\')
                    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($iconEntry,
                        $destination)
                }
                & "$PSScriptRoot\test-package-icons.ps1" -Layout $iconLayout -Store:$Store
            }
            finally { Remove-Item -LiteralPath $iconLayout -Recurse -Force }
            if (@($app.Entries | Where-Object { $_.FullName -match '\.pdb$' }).Count -ne 0) { throw 'Debug symbols belong outside the app package.' }
            $exeStream = $app.GetEntry('GHCPSpendTray.exe').Open()
            $bytesStream = [IO.MemoryStream]::new()
            try { $exeStream.CopyTo($bytesStream); $bytes = $bytesStream.ToArray() }
            finally { $exeStream.Dispose(); $bytesStream.Dispose() }
            $pe = [BitConverter]::ToInt32($bytes, 0x3c)
            $machine = if ($package.Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
            if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne $machine -or
                [BitConverter]::ToUInt32($bytes, $pe + 24 + 112 + 14 * 8) -ne 0) {
                throw 'Package executable is not Native AOT for the expected architecture.'
            }
        }
        finally { $app.Dispose(); $stream.Dispose() }
    }
}
finally { $archive.Dispose() }
Write-Output "PASS: bundle identity, architectures, Native AOT payload, startup XAML, transparent shell resources and opt-in startup ($Version)."
