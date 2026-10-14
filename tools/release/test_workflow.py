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

    def test_update_feed_is_retryable_after_mac_release_already_published(self):
        job = self.job("macos_updates")
        self.assertIn("needs: [plan, macos]", job)
        self.assertIn("always()", job)
        self.assertIn("needs.plan.outputs.macos_version != ''", job)
        self.assertIn("!inputs.prerelease", job)
        self.assertIn("needs.macos.result == 'skipped'", job)
        self.assertIn("uses: ./.github/workflows/macos-updates.yml", job)
        feed = (ROOT / ".github/workflows/macos-updates.yml").read_text()
        for text in ("workflow_call:", "workflow_dispatch:", "cancel-in-progress: false",
                     "if: github.ref == 'refs/heads/main'", "environment: production",
                     "updates.py feed", "actions/upload-pages-artifact@", "actions/deploy-pages@"):
            self.assertIn(text, feed)
        self.assertIn("needs: generate", feed)
        self.assertNotIn("release.py publish", feed)
        self.assertNotIn("notarize.sh", feed)
        self.assertNotIn("contents: write", feed)

    def test_sparkle_signs_final_notarized_dmg_before_checksums(self):
        signing = (ROOT / "tools/macos/sign-package.sh").read_text()
        self.assertLess(signing.index('stapler staple "$dmg"'), signing.index("updates.py generate"))
        self.assertLess(signing.index("updates.py generate"), signing.index('root.joinpath("SHA256SUMS")'))
        self.assertIn("SPARKLE_PUBLIC_ED_KEY: ${{ vars.SPARKLE_PUBLIC_ED_KEY }}", self.job("macos"))
        self.assertIn("SPARKLE_PRIVATE_ED_KEY: ${{ secrets.SPARKLE_PRIVATE_ED_KEY }}", self.job("macos"))
        self.assertNotIn("SPARKLE_", self.job("plan"))
        self.assertNotIn("SPARKLE_", self.job("windows"))


if __name__ == "__main__":
    unittest.main()
