param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [switch] $Store
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bundlePath = (Resolve-Path -LiteralPath $Bundle).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-deployment-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Read-ZipXml($Archive, [string] $Name) {
    $reader = [IO.StreamReader]::new($Archive.GetEntry($Name).Open())
    try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Write-ZipManifest($Archive, $Manifest) {
    $Archive.GetEntry('AppxManifest.xml').Delete()
    $writer = [IO.StreamWriter]::new($Archive.CreateEntry('AppxManifest.xml').Open())
    try { $writer.Write($Manifest.OuterXml) } finally { $writer.Dispose() }
}
function Remove-ZipEntry($Archive, [string] $Name) {
    $entries = @($Archive.Entries | Where-Object FullName -IEQ $Name)
    if ($entries.Count -ne 1) { throw "Expected exactly one fixture entry: $Name" }
    $entries[0].Delete()
}
function Assert-Rejected([string] $ExpectedError, [scriptblock] $Change) {
    $mutated = Join-Path $fixture 'mutated.msixbundle'
    Copy-Item -LiteralPath $bundlePath -Destination $mutated -Force
    $archive = [IO.Compression.ZipFile]::Open($mutated, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $manifest = Read-ZipXml $archive 'AppxMetadata/AppxBundleManifest.xml'
        $package = $manifest.Bundle.Packages.Package | Where-Object Architecture -EQ x64
        $entry = $archive.GetEntry($package.FileName)
        $stream = [IO.MemoryStream]::new()
        $source = $entry.Open()
        try { $source.CopyTo($stream) } finally { $source.Dispose() }
        try {
            $stream.Position = 0
            $app = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update, $true)
            try { & $Change $app } finally { $app.Dispose() }
            $entry.Delete()
            $destination = $archive.CreateEntry($package.FileName).Open()
            try { $stream.Position = 0; $stream.CopyTo($destination) } finally { $destination.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
    try {
        & "$PSScriptRoot\test-package.ps1" -Bundle $mutated -Version $Version `
            -IdentityName $IdentityName -Publisher $Publisher -Store:$Store | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedError*") { throw }
        return
    }
    throw "Invalid deployment payload was accepted; expected: $ExpectedError"
}
try {
    & "$PSScriptRoot\test-package.ps1" -Bundle $bundlePath -Version $Version `
        -IdentityName $IdentityName -Publisher $Publisher -Store:$Store | Out-Null
    if ($Store) {
        Assert-Rejected 'exact resolved Windows App Runtime framework dependency' {
            param($app)
            $manifest = Read-ZipXml $app 'AppxManifest.xml'
            [void]$manifest.Package.Dependencies.RemoveChild($manifest.Package.Dependencies.PackageDependency)
            Write-ZipManifest $app $manifest
        }
        foreach ($attribute in @('Name', 'Publisher', 'MinVersion', 'ProcessorArchitecture')) {
            Assert-Rejected 'exact resolved Windows App Runtime framework dependency' {
                param($app)
                $manifest = Read-ZipXml $app 'AppxManifest.xml'
                $value = switch ($attribute) {
                    'MinVersion' { '0.0.0.0' }
                    'ProcessorArchitecture' { 'x86' }
                    default { 'Synthetic.Invalid' }
                }
                $manifest.Package.Dependencies.PackageDependency.SetAttribute($attribute, $value)
                Write-ZipManifest $app $manifest
            }
        }
        Assert-Rejected 'exact resolved Windows App Runtime framework dependency' {
            param($app)
            $manifest = Read-ZipXml $app 'AppxManifest.xml'
            [void]$manifest.Package.Dependencies.AppendChild($manifest.Package.Dependencies.PackageDependency.CloneNode($true))
            Write-ZipManifest $app $manifest
        }
        foreach ($name in @('Microsoft.UI.Xaml.dll', 'DWriteCore.dll', 'Microsoft.WindowsAppRuntime.dll',
            'Microsoft.UI.Xaml/Themes/generic.xbf', 'Microsoft.UI.Xaml/Themes/themeresources.xbf')) {
            Assert-Rejected 'must not bundle Windows App Runtime payload' {
                param($app)
                [void]$app.CreateEntry($name)
            }
        }
        Assert-Rejected 'Missing application payload: Microsoft.WindowsAppRuntime.Bootstrap.dll' {
            param($app)
            Remove-ZipEntry $app 'Microsoft.WindowsAppRuntime.Bootstrap.dll'
        }
    } else {
        Assert-Rejected 'must not declare framework dependencies' {
            param($app)
            $manifest = Read-ZipXml $app 'AppxManifest.xml'
            $dependency = $manifest.CreateElement('PackageDependency', $manifest.DocumentElement.NamespaceURI)
            $dependency.SetAttribute('Name', 'Microsoft.WindowsAppRuntime.2')
            $dependency.SetAttribute('Publisher', 'CN=Synthetic publisher')
            $dependency.SetAttribute('MinVersion', '2.5.1.0')
            [void]$manifest.Package.Dependencies.AppendChild($dependency)
            Write-ZipManifest $app $manifest
        }
        foreach ($name in @('Microsoft.UI.Xaml.dll', 'DWriteCore.dll')) {
            Assert-Rejected "Missing application payload: $name" {
                param($app)
                Remove-ZipEntry $app $name
            }
        }
    }
    foreach ($name in @('GHCPSpendTray.pri', 'Reactor.pri', 'resources.pri')) {
        Assert-Rejected "Missing application payload: $name" {
            param($app)
            Remove-ZipEntry $app $name
        }
    }
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
Write-Output "PASS: deployment mode, framework dependency and application resource regressions (Store=$Store)."
