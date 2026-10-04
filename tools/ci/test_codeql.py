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
        init = self.workflow.index("uses: github/codeql-action/init@")
        build = self.workflow.index("bash tools/macos/build.sh")
        update_crypto = self.workflow.index("tools/macos/update-crypto.swift")
        analyze = self.workflow.index("uses: github/codeql-action/analyze@")
        self.assertLess(init, build)
        self.assertLess(build, update_crypto)
        self.assertLess(update_crypto, analyze)
        self.assertIn("global-json-file: global.json", self.workflow)
        self.assertIn("build-mode: ${{ matrix.build-mode }}", self.workflow)

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


if __name__ == "__main__":
    unittest.main()
