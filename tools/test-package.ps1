param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [switch] $RequireSigned
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
            if ($manifest.Package.Identity.ProcessorArchitecture -ne $package.Architecture) { throw 'Architecture mismatch.' }
            $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
            $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
            $ns.AddNamespace('d', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
            $ns.AddNamespace('u10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
            $startup = $manifest.SelectSingleNode('//d:StartupTask', $ns)
            if ($null -eq $startup -or $startup.TaskId -ne 'GHCPSpendTrayStartup' -or $startup.Enabled -ne 'false' -or
                $startup.ParentNode.GetAttribute('Parameters', $ns.LookupNamespace('u10')) -ne '--startup') {
                throw 'Startup must be opt-in and launch with --startup.'
            }
            $application = $manifest.SelectSingleNode('//f:Application', $ns)
            if ($application.Executable -ne 'GHCPSpendTray.exe' -or
                $application.GetAttribute('TrustLevel', $ns.LookupNamespace('u10')) -ne 'mediumIL') { throw 'Expected a full-trust desktop application.' }
            foreach ($name in @('GHCPSpendTray.exe', 'GHCPSpendTray.pri', 'Reactor.pri', 'Microsoft.UI.Xaml.dll',
                'DWriteCore.dll', 'Assets/GHCPSpendTray.ico', 'Assets/Square44x44Logo.png',
                'Assets/Square150x150Logo.png', 'Assets/StoreLogo.png', 'LICENSE.txt')) {
                if ($name -notin $app.Entries.FullName) { throw "Missing application payload: $name" }
            }
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
Write-Output "PASS: bundle identity, architectures, Native AOT payload, resources and opt-in startup ($Version)."
