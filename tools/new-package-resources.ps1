param([Parameter(Mandatory)][string] $Layout)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$layoutPath = (Resolve-Path -LiteralPath $Layout).Path
$makepri = & "$PSScriptRoot\get-windows-sdk-tool.ps1" -Name makepri.exe
$fileList = Join-Path $layoutPath 'shell-assets.resfiles'
if (Test-Path -LiteralPath $fileList) { throw "Resource input list already exists: $fileList" }
try {
    Get-ChildItem -LiteralPath (Join-Path $layoutPath 'Assets') -File |
        Sort-Object Name | ForEach-Object { "Assets\$($_.Name)" } |
        Set-Content -LiteralPath $fileList -Encoding UTF8
    # Shell qualifiers belong to the final package identity, not the unpackaged WinUI resource map.
    & $makepri new /pr $layoutPath /cf "$PSScriptRoot\..\packaging\priconfig.xml" `
        /mn (Join-Path $layoutPath 'AppxManifest.xml') /of (Join-Path $layoutPath 'resources.pri') /o |
        Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Shell resource indexing failed.' }
}
finally { if (Test-Path -LiteralPath $fileList) { Remove-Item -LiteralPath $fileList -Force } }
