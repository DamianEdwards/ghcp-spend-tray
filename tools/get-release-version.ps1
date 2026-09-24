param([Parameter(Mandatory)][string] $Version)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw 'Use a three-part numeric version, e.g. 0.2.0. Mark previews with the release prerelease option.'
}
$parts = $Version.Split('.')
foreach ($part in $parts) {
    if ($part.Length -gt 5 -or [int]$part -gt 65535) { throw 'Each MSIX version component must be between 0 and 65535.' }
}
if ($Version -eq '0.0.0') { throw 'Release version must be greater than zero.' }
[pscustomobject]@{ Version = $Version; PackageVersion = "$Version.0"; Tag = "v$Version" }
