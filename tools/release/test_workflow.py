from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]


class ReleaseWorkflowTests(unittest.TestCase):
    """Check the dispatch/job contract as well as the planner's unit tests."""

    @classmethod
    def setUpClass(cls):
        cls.workflow = (ROOT / ".github/workflows/release.yml").read_text()

    def job(self, name):
        match = re.search(rf"^  {name}:\n(.*?)(?=^  \w+:\n|\Z)", self.workflow, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(match, name)
        return match[1]

    def test_one_dispatch_with_exact_platform_choices(self):
        self.assertTrue(self.workflow.startswith("name: Release\n"))
        self.assertFalse((ROOT / ".github/workflows/release-macos.yml").exists())
        for platform in ("windows", "macos"):
            match = re.search(rf"^      {platform}_bump:\n(.*?)(?=^      \w+:\n)", self.workflow, re.MULTILINE | re.DOTALL)
            self.assertIsNotNone(match)
            self.assertIn("type: choice", match[1])
            self.assertIn("default: Minor", match[1])
            self.assertEqual(re.findall(r"^          - (.+)$", match[1], re.MULTILINE),
                             ["no release", "Major", "Minor", "Patch"])
        self.assertNotIn("inputs.version", self.workflow)
        preview = re.search(r"^      prerelease:\n((?:^        .*\n)+)", self.workflow, re.MULTILINE)
        self.assertIsNotNone(preview)
        self.assertIn("type: boolean", preview[1])
        self.assertIn("default: false", preview[1])

    def test_skipped_platform_never_needs_approval_or_credentials(self):
        for platform in ("windows", "macos"):
            job = self.job(platform)
            self.assertIn("needs: plan", job)
            self.assertIn(f"if: needs.plan.outputs.{platform}_release == 'true'", job)
            self.assertIn("environment: production\n", job)
            self.assertIn("RELEASE_VERSION: ${{ needs.plan.outputs." + platform + "_version }}", job)
            self.assertIn("PRERELEASE: ${{ inputs.prerelease }}", job)
        self.assertNotIn("secrets.MACOS_", self.job("windows"))
        self.assertNotIn("secrets.AZURE_", self.job("macos"))
        self.assertNotIn("secrets.", self.job("plan"))
        self.assertNotIn("environment:", self.job("plan"))

    def test_plan_is_preserved_before_dependent_jobs(self):
        job = self.job("plan")
        self.assertIn("if: github.run_attempt > 1", job)
        self.assertIn("actions/download-artifact@", job)
        self.assertIn("if: github.run_attempt == 1", job)
        self.assertIn("actions/upload-artifact@", job)
        self.assertEqual(job.count("name: release-plan-${{ github.run_id }}"), 2)
        self.assertIn("retention-days: 90", job)
        self.assertIn("group: ghcpspendtray-release\n  cancel-in-progress: false", self.workflow)

    def test_publication_guards_and_provenance_are_preserved(self):
        windows = self.job("windows")
        macos = self.job("macos")
        self.assertIn("test-release-source.ps1", windows)
        self.assertIn("release.py validate", macos)
        self.assertIn("-RequireSigned", windows)
        self.assertIn("sign-package.sh", macos)
        for job in (windows, macos):
            self.assertIn("actions/attest@", job)
            self.assertIn("ref: ${{ github.sha }}", job)
        self.assertIn("releaseRunId = $env:GITHUB_RUN_ID", windows)
        signing = (ROOT / "tools/macos/sign-package.sh").read_text()
        self.assertIn('release_metadata(os.environ["GHCP_MAC_VERSION"], os.environ["GITHUB_SHA"], os.environ["GITHUB_RUN_ID"])', signing)
        self.assertIn("releaseRunId=run_id", (ROOT / "tools/macos/release.py").read_text())


if __name__ == "__main__":
    unittest.main()
