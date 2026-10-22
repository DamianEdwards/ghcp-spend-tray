from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
SECRET = "GHCP_CONSUMPTION_API_URL: ${{ secrets.GHCP_CONSUMPTION_API_URL }}"


class ConsumptionEndpointBuildTests(unittest.TestCase):
    def jobs(self, workflow):
        text = (ROOT / ".github/workflows" / workflow).read_text()
        return dict(re.findall(
            r"^  (\w+):\n(.*?)(?=^  \w+:\n|\Z)",
            text.split("\njobs:\n", 1)[1], re.MULTILINE | re.DOTALL,
        ))

    def test_all_build_lanes_receive_the_secret(self):
        for workflow, names in {
            "verify.yml": ("tests", "package", "macos_tests", "macos"),
            "release.yml": ("windows", "macos"),
            "store-package.yml": ("package",),
        }.items():
            for name in names:
                with self.subTest(workflow=workflow, job=name):
                    self.assertIn(SECRET, self.jobs(workflow)[name])

    def test_analysis_keeps_private_endpoint_out_of_codeql_diagnostics(self):
        self.assertNotIn("secrets.", (ROOT / ".github/workflows/codeql.yml").read_text())

    def test_core_embeds_the_environment_property_as_assembly_metadata(self):
        project = (ROOT / "src/GHCPSpendTray.Core/GHCPSpendTray.Core.csproj").read_text()
        self.assertIn('Condition="\'$(GHCP_CONSUMPTION_API_URL)\' != \'\'"', project)
        self.assertIn('Include="EnterpriseConsumptionApiUrl" Value="$(GHCP_CONSUMPTION_API_URL)"', project)
        self.assertIn('BeforeTargets="GenerateAssemblyInfo"', project)


if __name__ == "__main__":
    unittest.main()
