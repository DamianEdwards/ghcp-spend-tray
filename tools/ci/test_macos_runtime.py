from pathlib import Path
import plistlib
import unittest


ROOT = Path(__file__).resolve().parents[2]


class MacOSRuntimeTests(unittest.TestCase):
    def test_release_toolchain_artifact_runs_on_older_os_without_rebuilding(self):
        workflow = (ROOT / ".github/workflows/verify.yml").read_text()
        runtime = workflow.split("\n  macos_runtime:\n")[1].split("\n  verify:\n")[0]
        self.assertIn("runs-on: macos-15\n", runtime)
        self.assertNotIn("intel", runtime)
        self.assertIn('"$(uname -m)" != arm64', runtime)
        self.assertIn('"$RUNNER_ARCH" != ARM64', runtime)
        self.assertNotIn("matrix:", runtime)
        self.assertIn("needs: [changes, macos]", runtime)
        self.assertIn("if: needs.changes.outputs.macos == 'true'", runtime)
        self.assertIn("name: macos-development", runtime)
        self.assertIn("actions/download-artifact@", runtime)
        self.assertIn("tools/macos/smoke-test.py artifacts/macos-runtime/GHCPSpendTray.app", runtime)
        self.assertNotIn("--arch", runtime)
        self.assertIn("tools/macos/test-package.sh artifacts/macos-runtime/GHCPSpendTray.app", runtime)
        self.assertNotIn("build.sh", runtime)
        self.assertNotIn("setup-dotnet", runtime)
        self.assertIn("needs: [changes, markdown, tests, package, macos_tests, macos, macos_runtime]", workflow)
        self.assertIn("@('macos_tests', 'macos', 'macos_runtime')", (ROOT / "tools/assert-verification.ps1").read_text())

    def test_build_release_and_analysis_share_current_stable_toolchain(self):
        setup = (ROOT / ".github/actions/setup-macos/action.yml").read_text()
        self.assertIn("DEVELOPER_DIR: /Applications/Xcode_26.6.app/Contents/Developer", setup)
        self.assertIn('echo "DEVELOPER_DIR=$DEVELOPER_DIR" >> "$GITHUB_ENV"', setup)
        self.assertIn('echo "SDKROOT=$(xcrun --sdk macosx --show-sdk-path)" >> "$GITHUB_ENV"', setup)
        self.assertIn("xcrun swiftc --version", setup)
        self.assertIn('"$(uname -m)" != arm64', setup)
        self.assertIn('"$RUNNER_ARCH" != ARM64', setup)
        self.assertNotIn("xcode-select --switch", setup)
        verify = (ROOT / ".github/workflows/verify.yml").read_text()
        build = verify.split("\n  macos:\n")[1].split("\n  macos_runtime:\n")[0]
        self.assertIn("runs-on: macos-26", build)
        self.assertNotIn("matrix:", build)
        self.assertIn("uses: ./.github/actions/setup-macos", build)
        self.assertIn("run: bash tools/macos/verify.sh --app", build)
        shared = verify.split("\n  macos_tests:\n")[1].split("\n  macos:\n")[0]
        self.assertIn("runs-on: macos-26", shared)
        self.assertIn("uses: ./.github/actions/setup-macos", shared)
        self.assertIn("run: bash tools/macos/verify.sh --shared-tests", shared)
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
        self.assertIn("arm64-apple-macos15.0", build)
        self.assertNotIn("apple-macos14.0", build)
        package = (ROOT / "tools/macos/test-package.sh").read_text()
        self.assertIn("LSMinimumSystemVersion", package)
        self.assertIn('"$minimum" != 15.0', package)
        self.assertIn('minimumOS="15.0"', (ROOT / "tools/macos/release.py").read_text())

    def test_mac_distribution_is_arm64_only_without_changing_windows(self):
        build = (ROOT / "tools/macos/build.sh").read_text()
        self.assertIn("-r osx-arm64", build)
        self.assertNotIn("osx-x64", build)
        self.assertNotIn("lipo -create", build)
        self.assertNotIn("x86_64", build)
        self.assertIn("rm -rf artifacts/macos/x64", build)
        self.assertIn('cp "$output/GHCPSpendTray" "$app/Contents/MacOS/GHCPSpendTray"', build)
        self.assertIn('cp "$output/GHCPSpendTray.MacBridge.dylib" "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"', build)
        verify = (ROOT / "tools/macos/verify.sh").read_text()
        self.assertIn("rid=osx-arm64", verify)
        self.assertNotIn("osx-x64", verify)
        for script in ("build.sh", "verify.sh", "test-package.sh"):
            self.assertIn("architecture.py", (ROOT / "tools/macos" / script).read_text())
        package = (ROOT / "tools/macos/test-package.sh").read_text()
        self.assertIn('architecture.py" binary "$binary"', package)
        self.assertIn("otool -arch arm64", package)
        self.assertIn("vtool -arch arm64", package)
        bridge = (ROOT / "src/GHCPSpendTray.MacBridge/GHCPSpendTray.MacBridge.csproj").read_text()
        self.assertIn("StartsWith('osx-')", bridge)
        self.assertIn("'$(RuntimeIdentifier)' != 'osx-arm64'", bridge)
        frontend = (ROOT / "src/GHCPSpendTray.Mac/AppMain.swift").read_text()
        self.assertIn("#if !arch(arm64)", frontend)
        self.assertIn("#error(", frontend)
        self.assertIn("PASS: macOS arm64", frontend)
        with (ROOT / "packaging/macos/Info.plist").open("rb") as file:
            metadata = plistlib.load(file)
        self.assertEqual(metadata["LSArchitecturePriority"], ["arm64"])
        self.assertTrue(metadata["LSRequiresNativeExecution"])
        workflow = (ROOT / ".github/workflows/verify.yml").read_text()
        self.assertIn("runs-on: windows-2025", workflow)
        self.assertIn("both MSIX architectures", workflow)
        self.assertNotIn("continue-on-error:", workflow)
        self.assertIn("if: always()", workflow.split("\n  verify:\n")[1])

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

    def test_guarded_quit_retains_the_bridge_event_loop_and_real_smoke_coverage(self):
        source = (ROOT / "src/GHCPSpendTray.Mac/AppMain.swift").read_text()
        termination = source.split("func applicationShouldTerminate(")[1].split("func applicationWillTerminate(")[0]
        self.assertIn("model.requestLeaving({ sender.terminate(nil) })", termination)
        self.assertIn("return .terminateCancel", termination)
        self.assertNotIn(".terminateLater", termination)
        smoke = source.split("private func runSmoke(")[1]
        for phase in ("saving before native Settings close", "rejecting invalid Save on native Quit",
                      "keeping draft on native Quit", "discarding before native Settings close",
                      "saving before native Quit"):
            self.assertIn(phase, smoke)
        completed = source.split("func applicationWillTerminate(")[1].split("nonisolated func userNotificationCenter(")[0]
        self.assertIn("model.settings?.pollMinutes == 20", completed)
        self.assertIn('smoke-result.txt', completed)
        launcher = (ROOT / "tools/macos/smoke-test.py").read_text()
        self.assertIn("protected close and save-before-Quit", launcher)
        self.assertIn('"AccountWithBudget"', launcher)

    def test_popup_readiness_checks_effective_window_size_not_cached_request(self):
        source = (ROOT / "src/GHCPSpendTray.Mac/AppMain.swift").read_text()
        wait = source.split("private func waitForPopupAnchor(")[1].split("private func popupSmokeDiagnostic(")[0]
        self.assertIn("windowContentBounds: windowContentView.bounds", wait)
        self.assertNotIn("contentSize: popover.contentSize", wait)
        geometry = (ROOT / "src/GHCPSpendTray.Mac/PopupSmokeReadiness.swift").read_text()
        self.assertIn("Self.sameSize(contentBounds.size, windowContentBounds.size)", geometry)
        self.assertIn("Self.sameSize(contentBounds.size, preferredContentSize)", geometry)
        self.assertIn("static let anchorTolerance: CGFloat = 8", geometry)
        diagnostic = source.split("private func popupSmokeDiagnostic(")[1]
        self.assertIn("contentSize=\\(popover.contentSize)", diagnostic)

    def test_sparkle_fixture_awaits_callbacks_and_retains_failure_diagnostics(self):
        fixture = (ROOT / "tests/GHCPSpendTray.MacTests/SparkleTests.swift").read_text()
        self.assertIn("try await server.start()", fixture)
        self.assertIn("try await probe.completion.value", fixture)
        self.assertIn("responseData == feedData", fixture)
        self.assertNotIn("python3", fixture)
        self.assertNotIn("Task.sleep", fixture)
        self.assertNotIn("port.txt", fixture)
        listener = (ROOT / "tests/GHCPSpendTray.MacTests/LoopbackAppcastServer.swift").read_text()
        self.assertIn('parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)', listener)
        self.assertIn("case .ready:", listener)
        self.assertIn("readiness.complete(.success(url))", listener)
        self.assertIn("connection.cancel()", listener)
        for name in ("verify", "release"):
            workflow = (ROOT / f".github/workflows/{name}.yml").read_text()
            macos = workflow.split("\n  macos:\n")[1].split("\n  macos_")[0]
            self.assertIn("id: native_verify", macos)
            self.assertIn("always() && steps.native_verify.outcome", macos)
            self.assertIn("path: artifacts/macos-test-diagnostics/", macos)
        verify = (ROOT / "tools/macos/verify.sh").read_text()
        self.assertIn("set -euo pipefail", verify)
        self.assertIn("2>&1 | tee artifacts/macos-test-diagnostics/platform-tests.log", verify)
