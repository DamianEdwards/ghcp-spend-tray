from fnmatch import fnmatchcase
import json
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]


class DependabotTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        configuration = (ROOT / ".github/dependabot.yml").read_text()
        sections = re.split(r'^  - package-ecosystem: "([^"]+)"\n', configuration, flags=re.MULTILINE)
        cls.updates = dict(zip(sections[1::2], sections[2::2]))

    def test_nuget_covers_all_source_and_test_projects(self):
        nuget = self.updates["nuget"]
        directories = re.findall(r'^      - "(/[^"]+)"$', nuget, re.MULTILINE)
        projects = [project for directory in ("src", "tests") for project in (ROOT / directory).rglob("*.csproj")]
        self.assertTrue(projects)
        for project in projects:
            directory = "/" + project.parent.relative_to(ROOT).as_posix()
            self.assertTrue(any(fnmatchcase(directory, pattern) for pattern in directories), directory)
        self.assertIn('"Microsoft.WindowsAppSDK.*"', nuget)

    def test_npm_covers_locked_artwork_tooling(self):
        npm = self.updates["npm"]
        self.assertIn('directory: "/tools"', npm)
        manifest = json.loads((ROOT / "tools/package.json").read_text())
        lock = json.loads((ROOT / "tools/package-lock.json").read_text())
        self.assertEqual(manifest["devDependencies"], lock["packages"][""]["devDependencies"])

    def test_sdk_ignores_major_minor_updates_and_actions_remain_grouped(self):
        sdk = self.updates["dotnet-sdk"]
        self.assertIn('directory: "/"', sdk)
        self.assertIn('"version-update:semver-major"', sdk)
        self.assertIn('"version-update:semver-minor"', sdk)
        actions = self.updates["github-actions"]
        self.assertIn('directory: "/"', actions)
        self.assertIn("groups:\n      actions:", actions)

    def test_all_updates_are_weekly_on_wednesday(self):
        for ecosystem, configuration in self.updates.items():
            with self.subTest(ecosystem=ecosystem):
                self.assertIn('interval: "weekly"', configuration)
                self.assertIn('day: "wednesday"', configuration)
                self.assertRegex(configuration, r"open-pull-requests-limit: [1-9]")

    def test_swift_updates_follow_package_manifests(self):
        manifests = [manifest for directory in ("src", "tests") for manifest in (ROOT / directory).rglob("Package.swift")]
        self.assertEqual("swift" in self.updates, bool(manifests),
                         "Configure Swift Dependabot updates when introducing Swift Package Manager dependencies.")


if __name__ == "__main__":
    unittest.main()
