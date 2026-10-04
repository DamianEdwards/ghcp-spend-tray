from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]


class CodeQLTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = (ROOT / ".github/workflows/codeql.yml").read_text()

    def test_explicit_language_coverage(self):
        self.assertEqual(re.findall(r"- language: (.+)", self.workflow),
                         ["actions", "csharp", "javascript-typescript", "python", "swift"])
        self.assertNotIn("language: c-cpp", self.workflow)
        self.assertNotIn("codeql-action/autobuild", self.workflow)

    def test_swift_traces_real_build_after_initialization(self):
        swift = re.search(r"- language: swift\n\s+runner: (\S+)\n\s+build-mode: (\S+)", self.workflow)
        self.assertIsNotNone(swift)
        self.assertEqual(swift.groups(), ("macos-26", "manual"))
        bridge = self.workflow.index("run: bash tools/macos/build.sh --bridge-only")
        init = self.workflow.index("uses: github/codeql-action/init@")
        build = self.workflow.index("run: bash tools/macos/build.sh --frontend-only")
        analyze = self.workflow.index("uses: github/codeql-action/analyze@")
        self.assertLess(bridge, init)
        self.assertLess(init, build)
        self.assertLess(build, analyze)
        self.assertIn("global-json-file: global.json", self.workflow)
        self.assertIn("build-mode: ${{ matrix.build-mode }}", self.workflow)

    def test_split_build_preserves_normal_build_and_checks_prerequisite(self):
        build = (ROOT / "tools/macos/build.sh").read_text()
        self.assertIn("build_mode=all", build)
        self.assertIn('--bridge-only|--frontend-only) build_mode="${1#--}"; shift', build)
        bridge = build.split('if [[ "$build_mode" != frontend-only ]]; then\n', 1)[1]
        self.assertIn("dotnet publish src/GHCPSpendTray.MacBridge", bridge.split("\nfi\n", 1)[0])
        self.assertIn('elif [[ ! -f "$output/GHCPSpendTray.MacBridge.dylib" ]]; then', bridge)
        self.assertIn("exit 1", bridge.split("\nfi\n", 1)[0])
        early_exit = build.split('if [[ "$build_mode" == bridge-only ]]; then\n', 1)[1].split("\nfi\n", 1)[0]
        self.assertIn('architecture.py binary "$output/GHCPSpendTray.MacBridge.dylib"', early_exit)
        self.assertIn("exit 0", early_exit)
        self.assertLess(build.index(early_exit), build.index("xcrun swiftc"))
        self.assertIn("src/GHCPSpendTray.Mac/*.swift", build)
        self.assertIn("bash tools/macos/test-package.sh", build)

    def test_scans_only_on_schedule_or_manual_dispatch(self):
        triggers = self.workflow.split("on:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertEqual(re.findall(r"^  ([a-z_]+):", triggers, re.MULTILINE),
                         ["schedule", "workflow_dispatch"])
        self.assertIn("cron: '37 10 * * 3'", triggers)
        self.assertNotIn("paths-ignore:", self.workflow)
        self.assertNotIn("continue-on-error:", self.workflow)
        self.assertIn("category: /language:${{ matrix.language }}", self.workflow)

    def test_analysis_has_no_deployment_credentials(self):
        self.assertNotIn("secrets.", self.workflow)
        self.assertNotIn("environment:", self.workflow)
        self.assertIn("persist-credentials: false", self.workflow)
        self.assertIn("security-events: write", self.workflow)
        for action in re.findall(r"uses: (\S+)", self.workflow):
            if action.startswith("./"):
                self.assertEqual(action, "./.github/actions/setup-macos")
            else:
                self.assertRegex(action, r"@[0-9a-f]{40}$")

    def test_failed_swift_extraction_retains_diagnostics(self):
        diagnostics = self.workflow.split("- name: Retain failed Swift extraction diagnostics\n", 1)[1]
        self.assertIn("if: failure() && matrix.language == 'swift'", diagnostics)
        self.assertIn("codeql_databases/swift/log/", diagnostics)
        self.assertIn("codeql_databases/swift/diagnostic/", diagnostics)
        self.assertIn("codeql_databases/log/build-tracer.log", diagnostics)
        self.assertIn("if-no-files-found: error", diagnostics)
        self.assertIn("retention-days: 7", diagnostics)


if __name__ == "__main__":
    unittest.main()
