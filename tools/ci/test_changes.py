import unittest
from changes import classify


class ChangesTests(unittest.TestCase):
    def test_platform_boundaries(self):
        for path in ("src/GHCPSpendTray.Mac/Views.swift", "src/GHCPSpendTray.MacBridge/Exports.cs",
                     "packaging/macos/Info.plist", "tests/GHCPSpendTray.MacTests/PlatformTests.swift",
                     "tools/macos/build.sh"):
            self.assertEqual(classify([path]), dict(windows=False, macos=True, markdown=False), path)
        for path in ("src/GHCPSpendTray.App/Program.cs", "tests/GHCPSpendTray.AppTests/Program.cs",
                     "packaging/AppxManifest.xml", "packaging/priconfig.xml", "tools/publish.ps1", ".github/workflows/store-package.yml",
                     "GHCPSpendTray.slnx"):
            self.assertEqual(classify([path]), dict(windows=True, macos=False, markdown=False), path)

    def test_shared_and_unknown(self):
        for path in ("src/GHCPSpendTray.Core/Models.cs", "src/GHCPSpendTray.Application/UiModels.cs",
                     "global.json", "Directory.Build.props", ".github/workflows/verify.yml",
                     "tools/ci/changes.py", "tests/GHCPSpendTray.SharedTests/Program.cs", "new-file",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png",
                     "src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-32.png",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo-small.svg", "tools/assert-verification.ps1",
                     ".github/workflows/release.yml", "tools/release/plan.py"):
            self.assertEqual(classify([path]), dict(windows=True, macos=True, markdown=False), path)

    def test_documentation(self):
        self.assertEqual(classify(["README.md", "docs/RELEASING.md"]), dict(windows=False, macos=False, markdown=True))
        self.assertEqual(classify([]), dict(windows=False, macos=False, markdown=False))
        self.assertEqual(classify(["README.md", "tools/macos/build.sh"]), dict(windows=False, macos=True, markdown=True))


if __name__ == "__main__":
    unittest.main()
