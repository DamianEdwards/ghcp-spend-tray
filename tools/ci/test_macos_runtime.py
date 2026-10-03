from pathlib import Path
import plistlib
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MacOSRuntimeTests(unittest.TestCase):
    def test_release_toolchain_artifact_runs_on_older_os_without_rebuilding(self):
        workflow = (ROOT / ".github/workflows/verify.yml").read_text()
        runtime = workflow.split("\n  macos_runtime:\n")[1].split("\n  verify:\n")[0]
        self.assertIn("runs-on: macos-15-intel", runtime)
        self.assertNotIn("matrix:", runtime)
        self.assertIn("needs: [changes, macos]", runtime)
        self.assertIn("if: needs.changes.outputs.macos == 'true'", runtime)
        self.assertIn("name: macos-development", runtime)
        self.assertIn("actions/download-artifact@", runtime)
        self.assertIn("tools/macos/smoke-test.py artifacts/macos-runtime/GHCPSpendTray.app", runtime)
        self.assertIn("GHCPSpendTray.app --arch x86_64", runtime)
        self.assertIn("tools/macos/test-package.sh artifacts/macos-runtime/GHCPSpendTray.app", runtime)
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
        self.assertIn("runs-on: macos-26", build)
        self.assertNotIn("matrix:", build)
        self.assertIn("uses: ./.github/actions/setup-macos", build)
        self.assertEqual(verify.count("run: bash tools/macos/verify.sh"), 1)
        release = (ROOT / ".github/workflows/release.yml").read_text().split("\n  macos:\n")[1]
        self.assertIn("runs-on: macos-26", release)
        self.assertIn("uses: ./.github/actions/setup-macos", release)
        codeql = (ROOT / ".github/workflows/codeql.yml").read_text()
        self.assertIn("uses: ./.github/actions/setup-macos", codeql)
        status = (ROOT / ".github/workflows/notarization-status.yml").read_text()
        self.assertIn("runs-on: macos-26", status)
        self.assertIn("uses: ./.github/actions/setup-macos", status)

    def test_two_major_version_policy_is_consistent(self):
        with (ROOT / "packaging/macos/Info.plist").open("rb") as file:
            self.assertEqual(plistlib.load(file)["LSMinimumSystemVersion"], "15.0")
        build = (ROOT / "tools/macos/build.sh").read_text()
        self.assertIn("MACOSX_DEPLOYMENT_TARGET=15.0", build)
        self.assertIn('$native_arch-apple-macos15.0', build)
        self.assertNotIn("apple-macos14.0", build)
        package = (ROOT / "tools/macos/test-package.sh").read_text()
        self.assertIn("LSMinimumSystemVersion", package)
        self.assertIn('"$minimum" != 15.0', package)
        self.assertIn('minimumOS="15.0"', (ROOT / "tools/macos/sign-package.sh").read_text())

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
        self.assertIn(".check_returncode()", launcher)
        self.assertIn('["/usr/bin/sample", str(process.pid)', launcher)
        self.assertIn("process.kill()", launcher)
