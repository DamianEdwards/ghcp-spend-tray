from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(platform.system() == "Darwin" and platform.machine() == "arm64",
                     "Real Mach-O package fixtures require native Apple silicon and Apple tools.")
class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="ghcp-synthetic-package-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.directory = Path(cls.temporary.name)
        cls.original = cls.directory / "original.app"
        contents = cls.original / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        (contents / "Frameworks").mkdir()
        cls.executable = Path("Contents/MacOS/GHCPSpendTray")
        cls.bridge = Path("Contents/Frameworks/GHCPSpendTray.MacBridge.dylib")
        with (ROOT / "packaging/macos/Info.plist").open("rb") as file:
            metadata = plistlib.load(file)
        metadata["CFBundleShortVersionString"] = metadata["CFBundleVersion"] = "0.2.0"
        with (contents / "Info.plist").open("wb") as file:
            plistlib.dump(metadata, file)
        cls.compile(cls.original / cls.executable, "arm64", executable=True)
        cls.compile(cls.original / cls.bridge, "arm64")
        # Unsupported slices exist only as disposable negative-test fixtures.
        cls.intel = cls.directory / "intel.dylib"
        cls.compile(cls.intel, "x86_64")
        cls.universal = cls.directory / "universal.dylib"
        cls.command("lipo", "-create", str(cls.original / cls.bridge), str(cls.intel),
                    "-output", str(cls.universal))
        cls.newer = cls.directory / "newer.dylib"
        cls.compile(cls.newer, "arm64", minimum="26.0")
        cls.command("codesign", "--force", "--sign", "-", str(cls.original / cls.bridge))
        cls.command("codesign", "--force", "--sign", "-", str(cls.original))

    @staticmethod
    def command(*arguments):
        subprocess.run(arguments, check=True, capture_output=True, text=True)

    @classmethod
    def compile(cls, path, architecture, executable=False, minimum="15.0"):
        arguments = ["xcrun", "clang", "-target", f"{architecture}-apple-macos{minimum}", "-x", "c", "-",
                     "-o", str(path)]
        if not executable:
            arguments += ["-dynamiclib", "-Wl,-install_name,@rpath/GHCPSpendTray.MacBridge.dylib"]
        source = "int main(void) { return 0; }" if executable else "int synthetic_fixture(void) { return 0; }"
        subprocess.run(arguments, input=source, check=True, capture_output=True, text=True)

    def setUp(self):
        self.bundle = self.directory / f"{self._testMethodName}.app"
        shutil.copytree(self.original, self.bundle)

    def package(self):
        return subprocess.run(["bash", str(ROOT / "tools/macos/test-package.sh"), str(self.bundle), "0.2.0"],
                              capture_output=True, text=True)

    def test_arm64_signed_synthetic_package_passes(self):
        result = self.package()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("PASS: Apple-silicon-only", result.stdout)

    def test_intel_and_universal_slices_rejected_in_each_binary(self):
        for relative in (self.executable, self.bridge):
            for bad in (self.intel, self.universal):
                with self.subTest(binary=relative, bad=bad.name):
                    shutil.copyfile(bad, self.bundle / relative)
                    result = self.package()
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("must contain exactly arm64", result.stderr)
                    shutil.copyfile(self.original / relative, self.bundle / relative)

    def test_newer_bridge_minimum_is_rejected(self):
        shutil.copyfile(self.newer, self.bundle / self.bridge)
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unexpected arm64 minimum OS: 26.0", result.stderr)

    def test_absolute_non_system_dependency_is_rejected(self):
        self.command("install_name_tool", "-id", "/synthetic/unsupported/bridge.dylib", str(self.bundle / self.bridge))
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unexpected arm64 dynamic dependency", result.stderr)

    def test_missing_binary_is_rejected(self):
        (self.bundle / self.bridge).unlink()
        self.assertNotEqual(self.package().returncode, 0)

    def test_unsigned_bundle_is_rejected(self):
        self.command("codesign", "--remove-signature", str(self.bundle))
        result = self.package()
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("PASS:", result.stdout)


if __name__ == "__main__":
    unittest.main()
