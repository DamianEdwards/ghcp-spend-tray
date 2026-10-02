$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\GHCPSpendTray.App\Assets'
foreach ($entry in @(
    @('ghcpspendtray-logo.png', 512, 0), @('ghcpspendtray-badge.png', 512, 255),
    @('Square44x44Logo.png', 44, 0), @('Square150x150Logo.png', 150, 0), @('StoreLogo.png', 50, 0))) {
    $image = [Drawing.Bitmap]::new((Join-Path $assets $entry[0]))
    try {
        if ($image.Width -ne $entry[1] -or $image.Height -ne $entry[1] -or
            $image.GetPixel(0, 0).A -ne $entry[2]) {
            throw "Incorrect static artwork dimensions or alpha: $($entry[0])"
        }
        $center = $image.GetPixel([int]($image.Width / 2), [int]($image.Height / 2))
        if ($center.A -ne 255 -or $center.R -lt 240 -or $center.G -lt 240 -or $center.B -lt 240) {
            throw "Static artwork must retain the white dollar stem: $($entry[0])"
        }
    }
    finally { $image.Dispose() }
}
$icon = [IO.File]::ReadAllBytes((Join-Path $assets 'GHCPSpendTray.ico'))
$sizes = @(16, 20, 24, 32, 48, 64, 256)
if ($icon.Length -lt 6 + 16 * $sizes.Count -or [BitConverter]::ToUInt16($icon, 0) -ne 0 -or
    [BitConverter]::ToUInt16($icon, 2) -ne 1 -or [BitConverter]::ToUInt16($icon, 4) -ne $sizes.Count) {
    throw 'Unexpected application ICO directory.'
}
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $entry = 6 + 16 * $i
    $size = $sizes[$i]
    $dimension = if ($size -eq 256) { 0 } else { $size }
    $length = [BitConverter]::ToUInt32($icon, $entry + 8)
    if ($icon[$entry] -ne $dimension -or $icon[$entry + 1] -ne $dimension -or
        [BitConverter]::ToUInt16($icon, $entry + 4) -ne 1 -or
        [BitConverter]::ToUInt16($icon, $entry + 6) -ne 32 -or
        [BitConverter]::ToUInt32($icon, $entry + 12) -ne $offset -or
        $offset + $length -gt $icon.Length) {
        throw "Invalid application ICO frame at ${size}px."
    }
    $png = [IO.File]::ReadAllBytes((Join-Path $assets "Square44x44Logo.targetsize-$size.png"))
    if ([Convert]::ToBase64String($icon, $offset, $length) -cne [Convert]::ToBase64String($png)) {
        throw "Application ICO and packaged artwork differ at ${size}px."
    }
    $offset += $length
}
if ($offset -ne $icon.Length) { throw 'Unexpected trailing ICO data.' }
Write-Output 'PASS: connected-dollar logo/badge alpha and matching executable/shell artwork in all 7 ICO frames.'
