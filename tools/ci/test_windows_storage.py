import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools/windows-storage-benchmark.ps1"


class StorageBenchmarkWorkflowTests(unittest.TestCase):
    def test_experiment_is_isolated_and_replicated(self):
        workflow = (ROOT / ".github/workflows/windows-storage-benchmark.yml").read_text()
        self.assertIn("runs-on: windows-2025", workflow)
        self.assertIn("workload: [Tests, Packaging]", workflow)
        self.assertIn("order: [baseline-first, relocated-first]", workflow)
        self.assertIn("sample: [1, 2]", workflow)
        self.assertIn("fail-fast: false", workflow)
        self.assertIn("branches-ignore: [main]", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertIn("if: always()", workflow)
        self.assertNotIn("actions/cache@", workflow)
        self.assertNotIn("secrets.", workflow)

    def test_only_temp_and_caches_move_and_processes_are_isolated(self):
        script = SCRIPT.read_text()
        self.assertIn("'C:\\storage-experiment'", script)
        self.assertIn("'D:\\storage-experiment'", script)
        self.assertIn('NUGET_PACKAGES = "$cacheRoot\\nuget\\packages"', script)
        self.assertIn('TEMP = "$cacheRoot\\temp"', script)
        self.assertIn("$start.Environment[$entry.Key] = $entry.Value", script)
        self.assertIn('"storage-benchmark-source-$Layout-$CacheState"', script)
        self.assertNotIn("Remove-Item", script)
        self.assertNotIn("Format-Volume", script)
        self.assertNotIn("Set-MpPreference", script)
        self.assertIn("Measure-Phase 'native-tests'", script)
        self.assertIn("Measure-Phase 'self-contained-package'", script)
        self.assertIn("Measure-Phase 'store-package'", script)


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell is required")
class StorageBenchmarkScriptTests(unittest.TestCase):
    def test_script_parses_and_rejects_non_hosted_execution(self):
        command = r"""
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseFile($env:BENCHMARK_SCRIPT, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count) { throw ($errors | Out-String) }
$env:RUNNER_ENVIRONMENT = 'self-hosted'
try {
    & $env:BENCHMARK_SCRIPT -Phase Compare
    throw 'Guard did not reject execution.'
}
catch {
    if ($_.Exception.Message -notlike '*only on disposable GitHub-hosted*') { throw }
}
exit 0
"""
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
            env={**os.environ, "BENCHMARK_SCRIPT": str(SCRIPT)}, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_timing_records_failure_and_propagates_it(self):
        command = r"""
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($env:BENCHMARK_SCRIPT, [ref]$tokens, [ref]$errors)
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Measure-Phase'
}, $true)
. ([scriptblock]::Create($function.Extent.Text))
$Layout = 'synthetic'
$CacheState = 'cold'
$timings = [Collections.Generic.List[object]]::new()
$timingPath = Join-Path $env:BENCHMARK_TEST_DIRECTORY 'timings.json'
$env:GITHUB_STEP_SUMMARY = Join-Path $env:BENCHMARK_TEST_DIRECTORY 'summary.md'
$env:STORAGE_SAMPLE = '1'
Measure-Phase 'success' {}
try { Measure-Phase 'failure' { throw 'synthetic failure' } }
catch { if ($_.Exception.Message -ne 'synthetic failure') { throw } }
Get-Content -LiteralPath $timingPath -Raw
"""
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
                env={**os.environ, "BENCHMARK_SCRIPT": str(SCRIPT),
                     "BENCHMARK_TEST_DIRECTORY": directory},
                capture_output=True, text=True,
            )
        self.assertEqual(result.returncode, 0, result.stderr)
        timings = json.loads(result.stdout)
        self.assertEqual([(item["phase"], item["success"]) for item in timings],
                         [("success", True), ("failure", False)])
        self.assertTrue(all(item["seconds"] >= 0 for item in timings))

    def test_paired_statistics_and_order_balancing(self):
        command = r"""
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($env:BENCHMARK_SCRIPT, [ref]$tokens, [ref]$errors)
foreach ($name in @('Get-PairedResults', 'Get-ComparisonPasses')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
$runs = @(
    [pscustomobject]@{ layout = 'baseline'; cache = 'cold'; seconds = 200; success = $true }
    [pscustomobject]@{ layout = 'relocated'; cache = 'cold'; seconds = 180; success = $true }
    [pscustomobject]@{ layout = 'baseline'; cache = 'warm'; seconds = 100; success = $true }
    [pscustomobject]@{ layout = 'relocated'; cache = 'warm'; seconds = 110; success = $true }
)
$invalid = @(
    ,@($runs[0], $runs[1], $runs[2])
    ,@($runs[0], $runs[1], $runs[2], $runs[3], $runs[3])
    ,@($runs[0], $runs[1], $runs[2], [pscustomobject]@{layout='relocated'; cache='warm'; seconds=0; success=$true})
    ,@($runs[0], $runs[1], $runs[2], [pscustomobject]@{layout='relocated'; cache='warm'; seconds=110; success=$false})
)
$rejected = foreach ($case in $invalid) {
    $caught = $false
    try { Get-PairedResults $case | Out-Null } catch { $caught = $true }
    $caught
}
@{
    pairs = @(Get-PairedResults $runs)
    baselineFirst = @(Get-ComparisonPasses baseline)
    relocatedFirst = @(Get-ComparisonPasses relocated)
    rejected = @($rejected)
} | ConvertTo-Json -Depth 6 -Compress
"""
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
            env={**os.environ, "BENCHMARK_SCRIPT": str(SCRIPT)},
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual([(pair["savedSeconds"], pair["savedPercent"]) for pair in data["pairs"]],
                         [(20, 10), (-10, -10)])
        self.assertEqual(data["rejected"], [True] * 4)
        for key, first, other in (("baselineFirst", "baseline", "relocated"),
                                  ("relocatedFirst", "relocated", "baseline")):
            self.assertEqual([(item["layout"], item["cache"]) for item in data[key]],
                             [(first, "cold"), (other, "cold"), (other, "warm"), (first, "warm")])


if __name__ == "__main__":
    unittest.main()
