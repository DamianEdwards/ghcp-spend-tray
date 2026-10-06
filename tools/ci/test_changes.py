import unittest
from changes import classify


class ChangesTests(unittest.TestCase):
    def test_platform_boundaries(self):
        for path in ("src/GHCPSpendTray.Mac/Views.swift", "src/GHCPSpendTray.MacBridge/Exports.cs",
                     "packaging/macos/Info.plist", "tests/GHCPSpendTray.MacTests/PlatformTests.swift",
                     "tools/macos/build.sh", "tools/macos/architecture.py", "tools/macos/test_architecture.py",
                     ".github/actions/setup-macos/action.yml", ".github/workflows/notarization-status.yml"):
            self.assertEqual(classify([path]), dict(windows=False, macos=True, markdown=False, prompt=False), path)
        for path in ("src/GHCPSpendTray.App/Program.cs", "tests/GHCPSpendTray.AppTests/Program.cs",
                     "packaging/AppxManifest.xml", "packaging/priconfig.xml", "tools/publish.ps1",
                     "tools/get-windows-app-runtime.ps1", "tools/test-windows-app-runtime.ps1",
                     "tools/test-package-deployment.ps1", ".github/workflows/store-package.yml",
                     "GHCPSpendTray.slnx"):
            self.assertEqual(classify([path]), dict(windows=True, macos=False, markdown=False, prompt=False), path)

    def test_shared_and_unknown(self):
        for path in ("src/GHCPSpendTray.Core/Models.cs", "src/GHCPSpendTray.Application/UiModels.cs",
                     "global.json", "Directory.Build.props", ".github/workflows/verify.yml", ".github/workflows/codeql.yml",
                     "tools/ci/changes.py", "tests/GHCPSpendTray.SharedTests/Program.cs", "new-file",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png",
                     "src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-32.png",
                     "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo-small.svg", "tools/assert-verification.ps1",
                     ".github/workflows/release.yml", "tools/release/plan.py"):
            self.assertEqual(classify([path]), dict(windows=True, macos=True, markdown=False, prompt=True), path)

    def test_documentation(self):
        self.assertEqual(classify(["README.md", "docs/RELEASING.md"]), dict(windows=False, macos=False, markdown=True, prompt=False))
        self.assertEqual(classify([".markdownlint.jsonc"]), dict(windows=False, macos=False, markdown=True, prompt=False))
        self.assertEqual(classify([]), dict(windows=False, macos=False, markdown=False, prompt=False))
        self.assertEqual(classify(["README.md", "tools/macos/build.sh"]), dict(windows=False, macos=True, markdown=True, prompt=False))

    def test_prompt_integration_does_not_build_apps(self):
        for name in ("CopilotPrompt.ps1", "copilot.segment.json", "demo.ps1", "Test-CopilotPrompt.ps1",
                     "CopilotPrompt.bash", "demo.bash", "Test-CopilotPrompt.bash", "CopilotPrompt.zsh", "Test-CopilotPrompt.zsh"):
            path = f"integrations/oh-my-posh/{name}"
            self.assertEqual(classify([path]), dict(windows=False, macos=False, markdown=False, prompt=True), path)
        self.assertEqual(classify(["integrations/oh-my-posh/README.md"]),
                         dict(windows=False, macos=False, markdown=True, prompt=False))
        for path in ("src/GHCPSpendTray.Prompt/Program.cs", "tests/GHCPSpendTray.PromptTests/Program.cs",
                     "tools/prompt/publish.ps1", "tools/prompt/publish.sh"):
            self.assertEqual(classify([path]), dict(windows=False, macos=False, markdown=False, prompt=True), path)
        self.assertEqual(classify(["integrations/oh-my-posh/CopilotPrompt.ps1", "README.md"]),
                         dict(windows=False, macos=False, markdown=True, prompt=True))
        self.assertEqual(classify(["integrations/oh-my-posh/new-file"]),
                         dict(windows=True, macos=True, markdown=False, prompt=True))
        self.assertEqual(classify(["integrations/oh-my-posh/copilot.segment.json", "src/GHCPSpendTray.App/Program.cs"]),
                         dict(windows=True, macos=False, markdown=False, prompt=True))

    def test_documentation_images_do_not_build_apps(self):
        for extension in ("png", "jpg", "jpeg", "gif", "svg", "webp", "avif", "PNG"):
            with self.subTest(extension=extension):
                self.assertEqual(classify([f"docs/images/screenshot.{extension}"]),
                                 dict(windows=False, macos=False, markdown=False, prompt=False))
        self.assertEqual(classify(["README.md", "docs/images/windows-flyout.png", "docs/images/macos-popup.png"]),
                         dict(windows=False, macos=False, markdown=True, prompt=False))

    def test_documentation_images_do_not_hide_code_or_unknown_changes(self):
        for path in ("docs/images/generate.py", "docs/images/new-file", "docs/images-extra/screenshot.png",
                     "docs/screenshot.png", "screenshot.png", "tools/ci/changes.py"):
            with self.subTest(path=path):
                self.assertEqual(classify(["docs/images/screenshot.png", path]),
                                 dict(windows=True, macos=True, markdown=False, prompt=True))
        self.assertEqual(classify(["docs/images/screenshot.png", "src/GHCPSpendTray.App/Program.cs"]),
                         dict(windows=True, macos=False, markdown=False, prompt=False))
        self.assertEqual(classify(["docs/images/screenshot.png", "tools/macos/build.sh"]),
                         dict(windows=False, macos=True, markdown=False, prompt=False))


if __name__ == "__main__":
    unittest.main()
