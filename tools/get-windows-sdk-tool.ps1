param([Parameter(Mandatory)][ValidateSet('makeappx.exe', 'signtool.exe')][string] $Name)
$ErrorActionPreference = 'Stop'
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$tool = Get-ChildItem -LiteralPath $kits -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and [version]$_.Name -ge [version]'10.0.22621.0' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $tool) { throw "Install Windows SDK 10.0.22621.0 or newer, including $Name." }
$tool
