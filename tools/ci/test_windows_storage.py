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
        self.assertIn("layout: [default, ntfs, devdrive]", workflow)
        self.assertIn("sample: [1, 2]", workflow)
        self.assertIn("fail-fast: false", workflow)
        self.assertIn("branches-ignore: [main]", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertIn("if: always()", workflow)
        self.assertNotIn("actions/cache@", workflow)
        self.assertNotIn("secrets.", workflow)

    def test_only_new_vhd_is_formatted_and_default_caches_are_preserved(self):
        script = SCRIPT.read_text()
        self.assertIn("Get-DiskImage -ImagePath $vhd | Get-Disk", script)
        self.assertIn("$disk.IsBoot -or $disk.IsSystem -or $disk.PartitionStyle -ne 'RAW'", script)
        self.assertIn("$script:storageRoot = 'R:\\storage-experiment'", script)
        self.assertIn("if ($Layout -ne 'default')", script)
        self.assertIn("NUGET_PACKAGES = 'nuget\\packages'", script)
        self.assertIn("TEMP = 'temp'", script)
        self.assertNotIn("Remove-Item", script)
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
    & $env:BENCHMARK_SCRIPT -Phase Setup -Layout default
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


if __name__ == "__main__":
    unittest.main()
