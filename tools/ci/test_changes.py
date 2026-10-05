import unittest
from changes import classify


class ChangesTests(unittest.TestCase):
    def test_platform_boundaries(self):
        for path in ("src/GHCPSpendTray.Mac/Views.swift", "src/GHCPSpendTray.MacBridge/Exports.cs",
                     "packaging/macos/Info.plist", "tests/GHCPSpendTray.MacTests/PlatformTests.swift",
                     "tools/macos/build.sh", "tools/macos/architecture.py", "tools/macos/test_architecture.py",
                     ".github/actions/setup-macos/action.yml", ".github/workflows/notarization-status.yml"):
            self.assertEqual(classify([path]), dict(windows=False, macos=True, linux=False, markdown=False), path)
        for path in ("src/GHCPSpendTray.App/Program.cs", "tests/GHCPSpendTray.AppTests/Program.cs",
                     "packaging/AppxManifest.xml", "packaging/priconfig.xml", "tools/publish.ps1",
                     "tools/get-windows-app-runtime.ps1", "tools/test-windows-app-runtime.ps1",
                     "tools/test-package-deployment.ps1", ".github/workflows/store-package.yml",
                     "GHCPSpendTray.slnx"):
            self.assertEqual(classify([path]), dict(windows=True, macos=False, linux=False, markdown=False), path)
        for path in ("src/GHCPSpendTray.Linux/Program.cs", "packaging/linux/install.py",
                     "tests/GHCPSpendTray.LinuxTests/Program.cs", "tools/linux/verify.sh",
                     "tools/linux/package-native.py", "src/GHCPSpendTray.Linux.Gnome/extension.js",
                     "src/GHCPSpendTray.Linux.Kde/contents/ui/main.qml",
                     "src/GHCPSpendTray.Linux.Hyprland/shell.qml",
                     "src/GHCPSpendTray.Linux.Qml/UsageView.qml",
                     "src/GHCPSpendTray.Linux.Gnome/schemas/demo.gschema.xml"):
            self.assertEqual(classify([path]), dict(windows=False, macos=False, linux=True, markdown=False), path)

    def test_shared_and_unknown(self):
        for path in ("src/GHCPSpendTray.Core/Models.cs", "src/GHCPSpendTray.Application/UiModels.cs",
                     "global.json", "Directory.Build.props", ".github/workflows/verify.yml", ".github/workflows/codeql.yml",
                     "tools/ci/changes.py", "tests/GHCPSpendTray.SharedTests/Program.cs", "new-file",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png",
                     "src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-32.png",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo-small.svg", "tools/assert-verification.ps1",
                     ".github/workflows/release.yml", "tools/release/plan.py"):
            self.assertEqual(classify([path]), dict(windows=True, macos=True, linux=True, markdown=False), path)

    def test_documentation(self):
        self.assertEqual(classify(["README.md", "docs/RELEASING.md"]), dict(windows=False, macos=False, linux=False, markdown=True))
        self.assertEqual(classify([".markdownlint.jsonc"]), dict(windows=False, macos=False, linux=False, markdown=True))
        self.assertEqual(classify([]), dict(windows=False, macos=False, linux=False, markdown=False))
        self.assertEqual(classify(["README.md", "tools/macos/build.sh"]), dict(windows=False, macos=True, linux=False, markdown=True))
        self.assertEqual(classify(["README.md", "tools/linux/verify.sh"]), dict(windows=False, macos=False, linux=True, markdown=True))

    def test_documentation_images_do_not_build_apps(self):
        for extension in ("png", "jpg", "jpeg", "gif", "svg", "webp", "avif", "PNG"):
            with self.subTest(extension=extension):
                self.assertEqual(classify([f"docs/images/screenshot.{extension}"]),
                                 dict(windows=False, macos=False, linux=False, markdown=False))
        self.assertEqual(classify(["README.md", "docs/images/windows-flyout.png", "docs/images/macos-popup.png"]),
                         dict(windows=False, macos=False, linux=False, markdown=True))

    def test_documentation_images_do_not_hide_code_or_unknown_changes(self):
        for path in ("docs/images/generate.py", "docs/images/new-file", "docs/images-extra/screenshot.png",
                     "docs/screenshot.png", "screenshot.png", "tools/ci/changes.py"):
            with self.subTest(path=path):
                self.assertEqual(classify(["docs/images/screenshot.png", path]),
                                 dict(windows=True, macos=True, linux=True, markdown=False))
        self.assertEqual(classify(["docs/images/screenshot.png", "src/GHCPSpendTray.App/Program.cs"]),
                         dict(windows=True, macos=False, linux=False, markdown=False))
        self.assertEqual(classify(["docs/images/screenshot.png", "tools/macos/build.sh"]),
                         dict(windows=False, macos=True, linux=False, markdown=False))


if __name__ == "__main__":
    unittest.main()
