param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string] $Architecture,
    [string] $AssetsPath = "$PSScriptRoot\..\artifacts\publish-store\project.assets.json"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$libraries = @($assets.libraries.PSObject.Properties | Where-Object {
    $_.Name -cmatch '^Microsoft\.WindowsAppSDK\.Runtime/'
})
if ($libraries.Count -ne 1) { throw 'Store packaging requires exactly one resolved Microsoft.WindowsAppSDK.Runtime package. Publish with -Store first.' }
$packagePath = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder $libraries[0].Value.path.Replace('/', '\')
    if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
}
if (-not $packagePath) { throw 'The resolved Windows App Runtime NuGet package is missing.' }
[xml]$reference = Get-Content -LiteralPath (Join-Path $packagePath 'buildTransitive\Microsoft.WindowsAppSDK.AppXReference.props') -Raw
$nameNode = $reference.SelectSingleNode('//*[local-name()="WinAppSDKPackageName"]')
if ($null -eq $nameNode -or $nameNode.InnerText -cnotmatch '^Microsoft\.WindowsAppRuntime\.[0-9]+$') {
    throw 'The Windows App Runtime package does not declare a supported framework identity.'
}
$package = Join-Path $packagePath "tools\MSIX\win10-$Architecture\$($nameNode.InnerText).msix"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $entry) { throw 'The Windows App Runtime framework package has no manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
    if ($manifest.Package.Properties.Framework -cne 'true' -or
        $identity.Name -cne $nameNode.InnerText -or $identity.ProcessorArchitecture -cne $Architecture -or
        $identity.Publisher -cne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
        $identity.Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
        [version]$identity.Version -le [version]'0.0.0.0') {
        throw 'Unexpected Windows App Runtime framework identity, publisher, architecture or version.'
    }
    [pscustomobject]@{
        Name = $identity.Name
        Publisher = $identity.Publisher
        MinVersion = $identity.Version
        Architecture = $Architecture
        PackagePath = $package
    }
}
finally { $archive.Dispose() }
