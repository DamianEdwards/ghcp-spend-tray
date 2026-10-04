import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

import architecture


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("smoke_test", ROOT / "tools/macos/smoke-test.py")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class ArchitectureTests(unittest.TestCase):
    def test_native_apple_silicon_host_required(self):
        with patch.object(architecture.platform, "system", return_value="Darwin"), \
                patch.object(architecture.platform, "machine", return_value="arm64"):
            architecture.require_host()
        for system, machine in (("Darwin", "x86_64"), ("Linux", "aarch64"), ("Windows", "ARM64")):
            with self.subTest(system=system, machine=machine), \
                    patch.object(architecture.platform, "system", return_value=system), \
                    patch.object(architecture.platform, "machine", return_value=machine):
                with self.assertRaisesRegex(ValueError, "native Apple silicon"):
                    architecture.require_host()

    def test_only_exact_arm64_distribution_slice_passes(self):
        for output in ("arm64\n", "arm64"):
            with patch.object(architecture.subprocess, "check_output", return_value=output) as probe:
                architecture.require_binary(Path("/synthetic/app"))
                probe.assert_called_once_with(["lipo", "-archs", "/synthetic/app"], text=True)
        for output in ("x86_64\n", "x86_64 arm64\n", "arm64 x86_64\n", "", "arm64e\n", "arm64 arm64"):
            with self.subTest(output=output), patch.object(architecture.subprocess, "check_output", return_value=output):
                with self.assertRaisesRegex(ValueError, "exactly arm64"):
                    architecture.require_binary("/synthetic/app")

    def test_failed_macho_inspection_is_not_success(self):
        with patch.object(architecture.subprocess, "check_output",
                          side_effect=subprocess.CalledProcessError(1, ["lipo"])):
            with self.assertRaises(subprocess.CalledProcessError):
                architecture.require_binary("/synthetic/app")

    def test_smoke_checks_host_and_both_binaries_before_launching(self):
        with patch.object(SMOKE, "require_host", side_effect=ValueError("unsupported host")), \
                patch.object(SMOKE, "require_binary") as probe, patch.object(SMOKE.subprocess, "Popen") as launch:
            with self.assertRaisesRegex(ValueError, "unsupported host"):
                SMOKE.smoke("/synthetic/GHCPSpendTray.app")
            probe.assert_not_called()
            launch.assert_not_called()
        with patch.object(SMOKE, "require_host"), \
                patch.object(SMOKE, "require_binary", side_effect=[None, ValueError("universal bridge")]) as probe, \
                patch.object(SMOKE.subprocess, "Popen") as launch:
            with self.assertRaisesRegex(ValueError, "universal bridge"):
                SMOKE.smoke("/synthetic/GHCPSpendTray.app")
            self.assertEqual([call.args[0].name for call in probe.call_args_list],
                             ["GHCPSpendTray", "GHCPSpendTray.MacBridge.dylib"])
            launch.assert_not_called()

    def test_smoke_launches_native_arm64_only(self):
        class StopAfterCommand(Exception):
            pass

        with patch.object(SMOKE, "require_host"), patch.object(SMOKE, "require_binary"), \
                patch.object(SMOKE.subprocess, "Popen", side_effect=StopAfterCommand) as launch:
            with self.assertRaises(StopAfterCommand):
                SMOKE.smoke("/synthetic/GHCPSpendTray.app")
            self.assertEqual(launch.call_args.args[0][:2], ["arch", "-arm64"])

    def test_removed_smoke_architecture_argument_fails_explicitly(self):
        result = subprocess.run(["python3", str(ROOT / "tools/macos/smoke-test.py"),
                                 "/synthetic/app", "--arch", "x86_64"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn("unrecognized arguments", result.stderr)


if __name__ == "__main__":
    unittest.main()
