from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MacOSRuntimeTests(unittest.TestCase):
    def test_release_toolchain_artifact_runs_on_older_os_without_rebuilding(self):
        workflow = (ROOT / ".github/workflows/verify.yml").read_text()
        runtime = workflow.split("\n  macos_runtime:\n")[1].split("\n  verify:\n")[0]
        self.assertIn("runner: [macos-15, macos-15-intel]", runtime)
        self.assertIn("runs-on: ${{ matrix.runner }}", runtime)
        self.assertIn("needs: [changes, macos]", runtime)
        self.assertIn("if: needs.changes.outputs.macos == 'true'", runtime)
        self.assertIn("name: macos-development", runtime)
        self.assertIn("actions/download-artifact@", runtime)
        self.assertIn("tools/macos/smoke-test.py artifacts/macos-runtime/GHCPSpendTray.app", runtime)
        self.assertNotIn("build.sh", runtime)
        self.assertNotIn("setup-dotnet", runtime)
        self.assertIn("needs: [changes, markdown, tests, package, macos, macos_runtime]", workflow)
        self.assertIn("@('macos', 'macos_runtime')", (ROOT / "tools/assert-verification.ps1").read_text())

    def test_build_release_and_analysis_share_current_stable_toolchain(self):
        setup = (ROOT / ".github/actions/setup-macos/action.yml").read_text()
        self.assertIn("DEVELOPER_DIR: /Applications/Xcode_26.6.app/Contents/Developer", setup)
        self.assertIn('echo "DEVELOPER_DIR=$DEVELOPER_DIR" >> "$GITHUB_ENV"', setup)
        self.assertIn('echo "SDKROOT=$(xcrun --sdk macosx --show-sdk-path)" >> "$GITHUB_ENV"', setup)
        self.assertIn("xcrun swiftc --version", setup)
        self.assertNotIn("xcode-select --switch", setup)
        verify = (ROOT / ".github/workflows/verify.yml").read_text()
        build = verify.split("\n  macos:\n")[1].split("\n  macos_runtime:\n")[0]
        self.assertIn("runner: [macos-26, macos-26-intel]", build)
        self.assertIn("uses: ./.github/actions/setup-macos", build)
        self.assertEqual(build.count("if: matrix.runner == 'macos-26'"), 2)
        release = (ROOT / ".github/workflows/release.yml").read_text().split("\n  macos:\n")[1]
        self.assertIn("runs-on: macos-26", release)
        self.assertIn("uses: ./.github/actions/setup-macos", release)
        codeql = (ROOT / ".github/workflows/codeql.yml").read_text()
        self.assertIn("uses: ./.github/actions/setup-macos", codeql)
        status = (ROOT / ".github/workflows/notarization-status.yml").read_text()
        self.assertIn("runs-on: macos-26", status)
        self.assertIn("uses: ./.github/actions/setup-macos", status)

    def test_notification_callbacks_cannot_inherit_main_actor(self):
        source = (ROOT / "src/GHCPSpendTray.Mac/Notifications.swift").read_text()
        self.assertIn("getNotificationSettings { @Sendable settings in", source)
        self.assertIn("requestAuthorization(options: [.alert, .sound]) { @Sendable allowed, error in", source)
        self.assertIn("add(request) { @Sendable error in", source)

    def test_smoke_reads_real_notification_settings_without_requesting_access(self):
        source = (ROOT / "src/GHCPSpendTray.Mac/AppMain.swift").read_text()
        smoke = source.split("private func runSmoke(")[1]
        self.assertIn("await NativeNotifications().status()", smoke)
        self.assertNotIn("requestAuthorization", smoke)
        self.assertNotIn("testNotification()", smoke)
        launcher = (ROOT / "tools/macos/smoke-test.py").read_text()
        self.assertIn("PASS: native notification settings callback.", launcher)
        self.assertIn("process.check_returncode()", launcher)
