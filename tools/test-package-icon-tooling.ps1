$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$layout = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-icons-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $layout | Out-Null
function Assert-Rejected([string] $ExpectedError) {
    try { & "$PSScriptRoot\test-package-icons.ps1" -Layout $layout | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedError*") { throw }
        return
    }
    throw "Invalid shell resources were accepted; expected: $ExpectedError"
}
try {
    Copy-Item -LiteralPath (Join-Path $root 'src\GHCPSpendTray.App\Assets') -Destination $layout -Recurse
    $manifestPath = Join-Path $layout 'AppxManifest.xml'
    Copy-Item -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Destination $manifestPath
    & "$PSScriptRoot\new-package-resources.ps1" -Layout $layout
    & "$PSScriptRoot\test-package-icons.ps1" -Layout $layout
    foreach ($form in @('unplated', 'lightunplated')) {
        $path = Join-Path $layout "Assets\Square44x44Logo.targetsize-24_altform-$form.png"
        $bytes = [IO.File]::ReadAllBytes($path)
        Remove-Item -LiteralPath $path
        Assert-Rejected 'Missing shell icon:'
        # Restore the file after indexing to prove mere payload presence is insufficient.
        & "$PSScriptRoot\new-package-resources.ps1" -Layout $layout
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-Rejected 'Missing shell PRI candidate:'
        & "$PSScriptRoot\new-package-resources.ps1" -Layout $layout
    }
    $path = Join-Path $layout 'Assets\Square44x44Logo.targetsize-16.png'
    $bytes = [IO.File]::ReadAllBytes($path)
    Copy-Item -LiteralPath (Join-Path $layout 'Assets\Square44x44Logo.png') -Destination $path -Force
    Assert-Rejected 'Wrong shell icon dimensions:'
    $opaque = [Drawing.Bitmap]::new(16, 16)
    $graphics = [Drawing.Graphics]::FromImage($opaque)
    try {
        $graphics.Clear([Drawing.Color]::Orange)
        $opaque.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $opaque.Dispose() }
    Assert-Rejected 'Shell icon corners must be transparent:'
    [IO.File]::WriteAllBytes($path, $bytes)
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $manifest.Package.Identity.Name = 'GHCPSpendTray.IconFixture'
    $manifest.Save($manifestPath)
    Assert-Rejected 'Shell PRI identity does not match'
    & "$PSScriptRoot\new-package-resources.ps1" -Layout $layout
    & "$PSScriptRoot\test-package-icons.ps1" -Layout $layout
    $manifest.Package.Applications.Application.VisualElements.BackgroundColor = '#0080FF'
    $manifest.Save($manifestPath)
    Assert-Rejected 'The manifest must select the transparent'
    $manifest.Package.Applications.Application.VisualElements.BackgroundColor = 'transparent'
    $manifest.Package.Applications.Application.VisualElements.Square44x44Logo = 'Assets\StoreLogo.png'
    $manifest.Save($manifestPath)
    Assert-Rejected 'The manifest must select the transparent'
}
finally { Remove-Item -LiteralPath $layout -Recurse -Force }
Write-Output 'PASS: missing light/dark assets, unindexed assets, wrong dimensions, opaque backgrounds and disconnected manifest/PRI rejected.'
