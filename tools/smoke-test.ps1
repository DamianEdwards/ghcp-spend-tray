param(
    [string] $Executable = "$PSScriptRoot\..\artifacts\publish\win-x64\ghspend.exe",
    [switch] $KeepData,
    [switch] $Empty
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Executable).Path
$folder = Join-Path ([System.IO.Path]::GetTempPath()) ("GHSpend-smoke-" + [Guid]::NewGuid().ToString('N'))
$before = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).GHSpend
$passed = $false
try {
    $arguments = @('--portable', '--data-dir', "`"$folder`"", '--smoke-test')
    if ($Empty) { $arguments += '--demo-empty' }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id
        throw "Smoke test timed out. Inspect the application error window or logs."
    }
    $result = Join-Path $folder 'native-smoke-result.txt'
    if (-not (Test-Path $result)) { throw "The native app exited without a smoke-test result (exit $($process.ExitCode))." }
    $content = Get-Content $result -Raw
    if ($process.ExitCode -ne 0 -or -not $content.StartsWith('PASS:')) { throw $content }
    $after = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).GHSpend
    if ($before -cne $after) { throw 'The GHSpend startup entry changed during the test.' }
    $passed = $true
    $content
    'Startup registration unchanged.'
}
finally {
    if ($passed -and -not $KeepData) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
    else {
        "Smoke-test directory retained for inspection: $folder"
    }
}
