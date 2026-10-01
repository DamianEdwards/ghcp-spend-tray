param([Parameter(Mandatory)][string] $Layout)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing
$layoutPath = (Resolve-Path -LiteralPath $Layout).Path
[xml]$manifest = Get-Content -LiteralPath (Join-Path $layoutPath 'AppxManifest.xml') -Raw
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$visual = $manifest.SelectSingleNode('//uap:VisualElements', $ns)
if ($null -eq $visual -or $visual.BackgroundColor -cne 'transparent' -or
    $visual.Square44x44Logo -cne 'Assets\Square44x44Logo.png') {
    throw 'The manifest must select the transparent Square44x44Logo shell resource.'
}
$priPath = Join-Path $layoutPath 'resources.pri'
if (-not (Test-Path -LiteralPath $priPath)) { throw 'Missing package shell resources.pri.' }
$makepri = & "$PSScriptRoot\get-windows-sdk-tool.ps1" -Name makepri.exe
$dumpPath = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-pri-" + [guid]::NewGuid().ToString('N') + '.xml')
try {
    & $makepri dump /if $priPath /of $dumpPath /dt detailed /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect package shell resources.pri.' }
    [xml]$dump = Get-Content -LiteralPath $dumpPath -Raw
}
finally { if (Test-Path -LiteralPath $dumpPath) { Remove-Item -LiteralPath $dumpPath -Force } }
$identity = $manifest.Package.Identity.Name
$map = $dump.SelectSingleNode('/PriInfo/ResourceMap')
if ($null -eq $map -or $map.name -cne $identity) { throw 'Shell PRI identity does not match the package.' }
# File presence alone is insufficient: the package's primary map must resolve startup XAML.
foreach ($relativePath in @('Reactor\Hosting\ReactorApplication.xbf',
    'Microsoft.UI.Xaml\Themes\generic.xbf', 'Microsoft.UI.Xaml\Themes\themeresources.xbf')) {
    $runtimeUri = "ms-resource://$identity/Files/$($relativePath.Replace('\', '/'))"
    $runtimeResource = @($map.SelectNodes('.//NamedResource') | Where-Object { $_.uri -ceq $runtimeUri })
    if ($runtimeResource.Count -ne 1) { throw "Missing startup PRI resource: $relativePath" }
    $runtimeCandidate = $runtimeResource[0].SelectNodes('Candidate')
    if ($runtimeCandidate.Count -ne 1 -or
        $runtimeCandidate[0].SelectNodes('QualifierSet/Qualifier').Count -ne 0) {
        throw "Invalid startup PRI candidate: $relativePath"
    }
    if ($runtimeCandidate[0].type -ceq 'EmbeddedData') {
        $xbf = [Convert]::FromBase64String($runtimeCandidate[0].Base64Value)
        if ($xbf.Length -lt 4 -or $xbf[0] -ne 0x58 -or $xbf[1] -ne 0x42 -or
            $xbf[2] -ne 0x46 -or $xbf[3] -ne 0) {
            throw "Invalid embedded startup XAML: $relativePath"
        }
    } elseif ($runtimeCandidate[0].type -ceq 'Path' -and $runtimeCandidate[0].Value -ceq $relativePath) {
        if (-not (Test-Path -LiteralPath (Join-Path $layoutPath $relativePath) -PathType Leaf)) {
            throw "Missing startup XAML payload: $relativePath"
        }
    } else {
        throw "Invalid startup PRI candidate: $relativePath"
    }
}
$uri = "ms-resource://$identity/Files/Assets/Square44x44Logo.png"
$resource = @($map.SelectNodes('.//NamedResource') | Where-Object { $_.uri -ceq $uri })
if ($resource.Count -ne 1) { throw 'Shell PRI does not index Files/Assets/Square44x44Logo.png.' }
$candidates = $resource[0].SelectNodes('Candidate')
$sizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in @(44) + $sizes) {
    $forms = if ($size -eq 44) { @('') } else { @('', 'unplated', 'lightunplated') }
    foreach ($form in $forms) {
        $suffix = if ($size -eq 44) { '' } else { ".targetsize-$size" }
        if ($form) { $suffix += "_altform-$form" }
        $relativePath = "Assets\Square44x44Logo$suffix.png"
        $path = Join-Path $layoutPath $relativePath
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing shell icon: $relativePath" }
        $image = [Drawing.Bitmap]::new($path)
        try {
            if ($image.Width -ne $size -or $image.Height -ne $size) { throw "Wrong shell icon dimensions: $relativePath" }
            if ($image.GetPixel(0, 0).A -ne 0 -or $image.GetPixel($size - 1, 0).A -ne 0 -or
                $image.GetPixel(0, $size - 1).A -ne 0 -or $image.GetPixel($size - 1, $size - 1).A -ne 0) {
                throw "Shell icon corners must be transparent: $relativePath"
            }
            if ($image.GetPixel([int]($size / 2), [int]($size / 2)).A -ne 255) {
                throw "Shell icon must retain opaque artwork: $relativePath"
            }
        }
        finally { $image.Dispose() }
        if ($form) {
            $defaultPath = Join-Path $layoutPath "Assets\Square44x44Logo.targetsize-$size.png"
            if ((Get-FileHash -LiteralPath $path).Hash -cne (Get-FileHash -LiteralPath $defaultPath).Hash) {
                throw "Shell theme variants must preserve the same artwork: $relativePath"
            }
        }
        $candidate = @($candidates | Where-Object { $_.Value -ceq $relativePath -and $_.type -eq 'Path' })
        if ($candidate.Count -ne 1) { throw "Missing shell PRI candidate: $relativePath" }
        $qualifiers = $candidate[0].SelectNodes('QualifierSet/Qualifier')
        $target = $candidate[0].SelectSingleNode('QualifierSet/Qualifier[@name="TargetSize"]')
        $alternate = $candidate[0].SelectSingleNode('QualifierSet/Qualifier[@name="AlternateForm"]')
        $expectedCount = 0
        if ($size -ne 44) {
            $expectedCount++
            if ($null -eq $target -or $target.value -ne "$size") { throw "Incorrect TargetSize: $relativePath" }
        }
        if ($form) {
            $expectedCount++
            if ($null -eq $alternate -or $alternate.value -ine $form) { throw "Incorrect AlternateForm: $relativePath" }
        }
        if ($qualifiers.Count -ne $expectedCount) { throw "Unexpected shell qualifiers: $relativePath" }
    }
}
Write-Output 'PASS: package-identity startup XAML resources and transparent shell artwork at all 14 target sizes in both themes.'
