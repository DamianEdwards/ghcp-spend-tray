from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from subprocess import CompletedProcess

from signing_identity import check, identities, select_identity


FINGERPRINT = "A" * 40
NAME = "Developer ID Application: Synthetic Publisher (1234567890)"
IDENTITY = (FINGERPRINT, NAME)


class SigningIdentityTests(unittest.TestCase):
    def test_identity_list_includes_invalid_and_valid_sections(self):
        output = f"""
  Matching identities
  1) {FINGERPRINT} "{NAME}" (CSSMERR_TP_NOT_TRUSTED)
     1 identities found
  Valid identities only
     0 valid identities found
"""
        self.assertEqual(identities(output), [IDENTITY])

    def test_select_by_fingerprint_or_exact_name(self):
        for selector in (FINGERPRINT, FINGERPRINT.lower(), NAME):
            self.assertEqual(select_identity(selector, [IDENTITY, IDENTITY], [IDENTITY]), FINGERPRINT)

    def test_missing_private_key_is_actionable(self):
        with self.assertRaisesRegex(ValueError, "matching private key"):
            select_identity(FINGERPRINT, [], [])

    def test_wrong_identity_does_not_fall_back(self):
        with self.assertRaisesRegex(ValueError, "does not match"):
            select_identity("B" * 40, [IDENTITY], [IDENTITY])

    def test_invalid_trust_is_not_missing_key(self):
        with self.assertRaisesRegex(ValueError, "intermediate certificate chain"):
            select_identity(FINGERPRINT, [IDENTITY], [])

    def test_non_developer_id_is_rejected(self):
        wrong = (FINGERPRINT, "Apple Development: Synthetic Publisher (1234567890)")
        with self.assertRaisesRegex(ValueError, "not a Developer ID Application"):
            select_identity(FINGERPRINT, [wrong], [wrong])

    def test_ambiguous_name_requires_fingerprint(self):
        other = ("B" * 40, NAME)
        with self.assertRaisesRegex(ValueError, "unique SHA-1"):
            select_identity(NAME, [IDENTITY, other], [IDENTITY, other])

    def test_inspection_is_scoped_to_temporary_keychain(self):
        output = f'  1) {FINGERPRINT} "{NAME}"\n  1 valid identities found'
        with patch("signing_identity.subprocess.run", return_value=CompletedProcess([], 0, output, "")) as run:
            self.assertEqual(check("/synthetic/signing.keychain-db", NAME), FINGERPRINT)
            self.assertEqual(run.call_count, 2)
            for call in run.call_args_list:
                self.assertEqual(call.args[0][-1], "/synthetic/signing.keychain-db")
                self.assertIn("find-identity", call.args[0])
                self.assertNotIn("export", call.args[0])
            self.assertIn("basic", run.call_args_list[0].args[0])
            self.assertIn("codesigning", run.call_args_list[1].args[0])

    def test_command_failure_does_not_echo_keychain_output(self):
        with patch("signing_identity.subprocess.run",
                   return_value=CompletedProcess([], 1, "", "synthetic-private-error")):
            with self.assertRaisesRegex(ValueError, "^macOS could not inspect"):
                check("/synthetic/signing.keychain-db", NAME)

    def test_release_checks_identity_before_private_key_permissions(self):
        script = (Path(__file__).resolve().parent / "sign-package.sh").read_text()
        self.assertLess(script.index("security import "), script.index("python3 tools/macos/signing_identity.py"))
        self.assertLess(script.index("python3 tools/macos/signing_identity.py"), script.index("security set-key-partition-list"))
        self.assertLess(script.index("security set-key-partition-list"), script.index("codesign --force"))
        self.assertIn('stage "import P12 certificate and private key"', script)
        self.assertIn('stage "submit application for notarization"', script)
        self.assertNotIn("set -x", script)


@unittest.skipUnless(sys.platform == "darwin", "Synthetic Keychain integration requires macOS")
class KeychainIntegrationTests(unittest.TestCase):
    def test_certificate_only_and_untrusted_pair_fail_differently(self):
        with tempfile.TemporaryDirectory(prefix="ghcp-signing-test-") as directory:
            root = Path(directory)
            keychain = str(root / "test.keychain-db")
            password = "synthetic-test-password"

            def run(*command):
                return subprocess.run(command, capture_output=True, text=True, check=True)

            run("/usr/bin/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                "-keyout", str(root / "key.pem"), "-out", str(root / "certificate.pem"),
                "-days", "1", "-subj", f"/CN={NAME}")
            run("/usr/bin/openssl", "pkcs12", "-export", "-nokeys",
                "-in", str(root / "certificate.pem"), "-out", str(root / "certificate-only.p12"),
                "-passout", "pass:" + password)
            run("/usr/bin/openssl", "pkcs12", "-export", "-inkey", str(root / "key.pem"),
                "-in", str(root / "certificate.pem"), "-out", str(root / "identity.p12"),
                "-passout", "pass:" + password)
            run("security", "create-keychain", "-p", password, keychain)
            try:
                run("security", "import", str(root / "certificate-only.p12"), "-k", keychain,
                    "-f", "pkcs12", "-P", password, "-T", "/usr/bin/codesign")
                with self.assertRaisesRegex(ValueError, "matching private key"):
                    check(keychain, NAME)
                run("security", "import", str(root / "identity.p12"), "-k", keychain,
                    "-f", "pkcs12", "-P", password, "-T", "/usr/bin/codesign")
                with self.assertRaisesRegex(ValueError, "intermediate certificate chain"):
                    check(keychain, NAME)
            finally:
                run("security", "delete-keychain", keychain)


if __name__ == "__main__":
    unittest.main()
