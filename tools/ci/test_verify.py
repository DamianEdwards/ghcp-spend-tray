import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from types import SimpleNamespace
import unittest


ROOT = Path(__file__).resolve().parents[2]


class VerifyWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = (ROOT / ".github/workflows/verify.yml").read_text()
        cls.jobs = dict(re.findall(
            r"^  (\w+):\n(.*?)(?=^  \w+:\n|\Z)",
            cls.workflow.split("\njobs:\n", 1)[1], re.MULTILINE | re.DOTALL,
        ))

    def test_windows_shards_run_independently_without_fail_fast(self):
        tests = self.jobs["tests"]
        self.assertIn("needs: changes", tests)
        self.assertIn("fail-fast: false", tests)
        self.assertIn("shard: [Application, CorePlatformShared]", tests)
        self.assertIn("TEST_SHARD: ${{ matrix.shard }}", tests)
        self.assertIn(r".\tools\verify.ps1 -NativeTests -TestShard $env:TEST_SHARD", tests)

    def test_both_deployment_modes_keep_both_architectures_and_smoke(self):
        package = self.jobs["package"]
        self.assertIn("needs: changes", package)
        self.assertIn("fail-fast: false", package)
        modes = re.findall(
            r"- mode: (\S+)\n\s+store: (\S+)\n\s+package_directory: (\S+)"
            r"\n\s+release_directory: (\S+)\n\s+artifact: (\S+)", package,
        )
        self.assertEqual(modes, [
            ("self-contained", "false", "msix", "release", "unsigned-development-msix"),
            ("store", "true", "msix-store", "release-store", "unsigned-store-msix"),
        ])
        self.assertIn("[bool]::Parse($env:STORE_PACKAGE)", package)
        self.assertIn(r".\tools\package.ps1 -Store:$store", package)
        self.assertIn(r".\tools\test-package-deployment.ps1 -Store:$store", package)
        self.assertIn("Default self-contained build failed after Store publishing.", package)
        self.assertIn("if ([bool]::Parse($env:STORE_PACKAGE)) {", package)
        self.assertIn("$directory = $env:PACKAGE_DIRECTORY", package)
        self.assertIn(r".\tools\smoke-test-package.ps1 -Layout $layout", package)
        self.assertIn("-Iterations 3 -Constrained", package)
        self.assertIn("finally { Stop-Transcript }", package)
        self.assertIn("packaged-smoke-diagnostics-${{ matrix.mode }}", package)
        self.assertIn("name: ${{ matrix.artifact }}", package)
        self.assertIn("path: artifacts/${{ matrix.release_directory }}/*.msixbundle", package)
        staging = package.split("- name: Exercise Store packaging", 1)[1]
        self.assertIn("if: matrix.store", staging)
        self.assertIn("-Store -SkipPublish", staging)
        self.assertIn("stage-store-package.ps1", staging)
        publisher = (ROOT / "tools/publish.ps1").read_text()
        self.assertIn("@('win-x64', 'win-arm64')", publisher)
        smoke = (ROOT / "tools/smoke-test-package.ps1").read_text()
        self.assertIn("@('--package-smoke-test', '--package-smoke-test --demo-empty')", smoke)

    def test_mac_lanes_and_runtime_have_only_the_necessary_dependencies(self):
        for job in ("macos_tests", "macos"):
            self.assertIn("needs: changes", self.jobs[job])
            self.assertIn("if: needs.changes.outputs.macos == 'true'", self.jobs[job])
            self.assertIn("runs-on: macos-26", self.jobs[job])
            self.assertIn("uses: ./.github/actions/setup-macos", self.jobs[job])
        self.assertIn("verify.sh --shared-tests", self.jobs["macos_tests"])
        self.assertNotIn("upload-artifact", self.jobs["macos_tests"])
        self.assertIn("verify.sh --app", self.jobs["macos"])
        self.assertIn("name: macos-development", self.jobs["macos"])
        self.assertIn("needs: [changes, macos]", self.jobs["macos_runtime"])
        self.assertNotIn("macos_tests", self.jobs["macos_runtime"])
        self.assertIn(
            "needs: [changes, markdown, tests, package, macos_tests, macos, macos_runtime]",
            self.jobs["verify"],
        )
        self.assertIn("if: always()", self.jobs["verify"])
        self.assertNotIn("continue-on-error:", self.workflow)

    def test_caching_and_triggers_remain_unchanged(self):
        triggers = self.workflow.split("on:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertEqual(re.findall(r"^  (\w+):", triggers, re.MULTILINE),
                         ["push", "pull_request", "workflow_dispatch"])
        for job, suffix in (("tests", "tests"), ("package", "package"),
                            ("macos_tests", "macos"), ("macos", "macos")):
            self.assertIn(f"-{suffix}-${{{{ hashFiles('global.json', '**/*.csproj',",
                          self.jobs[job])
            self.assertIn("global-json-file: global.json", self.jobs[job])


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell is required for script routing tests")
class WindowsVerificationShardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with tempfile.TemporaryDirectory() as directory:
            tools = Path(directory) / "tools"
            tools.mkdir()
            script = tools / "verify.ps1"
            shutil.copyfile(ROOT / "tools/verify.ps1", script)
            (tools / "test-release-tooling.ps1").write_text('$global:calls.Add(@("tooling"))')
            command = """
$ErrorActionPreference = 'Stop'
$global:calls = [Collections.Generic.List[object]]::new()
function global:dotnet {
    $global:calls.Add(@($args))
    $global:LASTEXITCODE = if ($args[0] -eq $global:failure) { 1 } else { 0 }
}
$cases = @(
    @{ shard = 'Application'; failure = '' },
    @{ shard = 'CorePlatformShared'; failure = '' },
    @{ shard = 'All'; failure = '' },
    @{ shard = 'Unknown'; failure = '' },
    @{ shard = 'Application'; failure = 'build' },
    @{ shard = 'CorePlatformShared'; failure = 'run' }
)
$outcomes = foreach ($case in $cases) {
    $global:calls = [Collections.Generic.List[object]]::new()
    $global:failure = $case.failure
    $message = ''
    try { & '%SCRIPT%' -TestShard $case.shard | Out-Null }
    catch { $message = $_.Exception.Message }
    [pscustomobject]@{
        shard = $case.shard; failure = $case.failure
        calls = $global:calls.ToArray(); error = $message
    }
}
ConvertTo-Json -InputObject @($outcomes) -Depth 4 -Compress
"""
            command = command.replace("%SCRIPT%", str(script).replace("'", "''"))
            result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
                                    capture_output=True, text=True)
            if result.returncode != 0:
                raise RuntimeError(result.stderr)
            cls.outcomes = {
                (case["shard"], case["failure"]): SimpleNamespace(
                    returncode=1 if case["error"] else 0,
                    stdout=json.dumps(case["calls"]), stderr=case["error"],
                )
                for case in json.loads(result.stdout)
            }

    def invoke(self, shard, failure=""):
        return self.outcomes[shard, failure]

    def test_default_and_partial_shards_cover_each_harness_once(self):
        harnesses = {
            "Application": ["GHCPSpendTray.AppTests"],
            "CorePlatformShared": ["GHCPSpendTray.Tests", "GHCPSpendTray.PlatformTests",
                                  "GHCPSpendTray.SharedTests"],
            "All": ["GHCPSpendTray.Tests", "GHCPSpendTray.PlatformTests",
                    "GHCPSpendTray.AppTests", "GHCPSpendTray.SharedTests"],
        }
        for shard, expected in harnesses.items():
            with self.subTest(shard=shard):
                result = self.invoke(shard)
                self.assertEqual(result.returncode, 0, result.stderr)
                calls = json.loads(result.stdout)
                self.assertEqual([call[2] for call in calls if call[0] == "run"],
                                 [fr"tests\{name}\{name}.csproj" for name in expected])
                build = next(call for call in calls if call[0] == "build")
                self.assertEqual(build[1],
                                 r"tests\GHCPSpendTray.AppTests\GHCPSpendTray.AppTests.csproj"
                                 if shard == "Application" else "GHCPSpendTray.slnx")
                self.assertEqual(["tooling"] in calls, shard != "Application")
        self.assertEqual(sorted(harnesses["Application"] + harnesses["CorePlatformShared"]),
                         sorted(harnesses["All"]))

    def test_unknown_shards_and_command_failures_are_not_success(self):
        for shard, failure in (("Unknown", ""), ("Application", "build"),
                               ("CorePlatformShared", "run")):
            with self.subTest(shard=shard, failure=failure):
                result = self.invoke(shard, failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(result.stderr.strip())


@unittest.skipUnless(shutil.which("bash"), "Bash is required for script routing tests")
class MacVerificationLaneTests(unittest.TestCase):
    def invoke(self, arguments, failure=""):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tools = root / "tools/macos"
            tools.mkdir(parents=True)
            shutil.copyfile(ROOT / "tools/macos/verify.sh", tools / "verify.sh")
            (tools / "sdk.sh").write_text("# Synthetic SDK setup.\n")
            binaries = root / "bin"
            binaries.mkdir()
            stub = '#!/bin/bash\nprintf "%s %s\\n" "${0##*/}" "$*" >> "$TRACE"\n'
            stub += '[[ "${FAIL_COMMAND:-}" != "${0##*/}" ]] || exit 1\n'
            for name in ("python3", "dotnet", "xcrun"):
                path = binaries / name
                path.write_text(stub)
                path.chmod(0o755)
            (tools / "build.sh").write_text(
                '#!/bin/bash\nprintf "%s\\n" build-app >> "$TRACE"\n'
                '[[ "${FAIL_COMMAND:-}" != build-app ]] || exit 1\n',
            )
            for name in ("GHCPSpendTray.Tests", "GHCPSpendTray.SharedTests", "platform-tests"):
                path = root / ("artifacts/macos/platform-tests" if name == "platform-tests"
                               else f"artifacts/tests/osx-arm64/{name}/{name}")
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(f'#!/bin/bash\nprintf "%s\\n" native-{name} >> "$TRACE"\n')
                path.chmod(0o755)
            trace = root / "trace.txt"
            env = dict(os.environ, PATH=str(binaries) + os.pathsep + os.environ["PATH"],
                       TRACE=trace.as_posix(), FAIL_COMMAND=failure)
            result = subprocess.run(["bash", (tools / "verify.sh").as_posix(), *arguments],
                                    capture_output=True, text=True, env=env)
            return result, trace.read_text().splitlines() if trace.exists() else []

    def test_default_is_the_union_of_independent_lanes(self):
        results = {}
        for mode, arguments in (("all", []), ("shared", ["--shared-tests"]), ("app", ["--app"])):
            result, calls = self.invoke(arguments)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(calls[0], "python3 tools/macos/architecture.py host")
            results[mode] = calls[1:]
        self.assertEqual(results["all"], results["shared"] + results["app"])
        shared = results["shared"]
        self.assertEqual(sum(call.startswith("python3 -m unittest") for call in shared), 3)
        for name in ("GHCPSpendTray.Tests", "GHCPSpendTray.SharedTests"):
            self.assertTrue(any(call.startswith(f"dotnet run --project tests/{name}") for call in shared))
            publish = next(call for call in shared if call.startswith(f"dotnet publish tests/{name}"))
            self.assertIn("-r osx-arm64 -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true", publish)
            self.assertIn(f"native-{name}", shared)
        self.assertEqual(results["app"][0], "build-app")
        self.assertTrue(results["app"][1].startswith("xcrun swiftc -swift-version 6 -warnings-as-errors"))
        self.assertIn("native-platform-tests", results["app"])
        self.assertEqual(results["app"][-1],
                         "python3 tools/macos/smoke-test.py artifacts/macos/GHCPSpendTray.app")

    def test_invalid_options_and_command_failures_are_not_success(self):
        for arguments, failure in ((["--unknown"], ""), (["--app", "--shared-tests"], ""),
                                   ([""], ""), (["--shared-tests"], "dotnet"),
                                   (["--app"], "build-app"), (["--app"], "xcrun")):
            with self.subTest(arguments=arguments, failure=failure):
                result, calls = self.invoke(arguments, failure)
                self.assertNotEqual(result.returncode, 0)
                if not failure:
                    self.assertEqual(result.returncode, 2)
                    self.assertIn("Usage:", result.stderr)
                    self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
