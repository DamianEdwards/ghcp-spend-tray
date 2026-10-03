from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from keychain_search import update_search_list


class KeychainSearchTests(unittest.TestCase):
    def run_update(self, operation, current):
        with patch("keychain_search.subprocess.run", side_effect=[
            subprocess.CompletedProcess([], 0, current, ""),
            subprocess.CompletedProcess([], 0, "", ""),
        ]) as run:
            update_search_list(operation, "/tmp/signing.keychain-db")
            return run.call_args_list

    def test_add_preserves_keychains_and_quoted_spaces(self):
        calls = self.run_update("add", '    "/Users/test/Library/Keychains/login.keychain-db"\n    "/path/with spaces/other.keychain-db"\n')
        self.assertEqual(calls[1].args[0], [
            "/usr/bin/security", "list-keychains", "-d", "user", "-s", "/tmp/signing.keychain-db",
            "/Users/test/Library/Keychains/login.keychain-db", "/path/with spaces/other.keychain-db"])

    def test_remove_preserves_entries_added_during_signing(self):
        calls = self.run_update("remove", '"/tmp/signing.keychain-db" "/original.keychain-db" "/new.keychain-db"')
        self.assertEqual(calls[1].args[0][-2:], ["/original.keychain-db", "/new.keychain-db"])
        self.assertNotIn("/tmp/signing.keychain-db", calls[1].args[0])

    def test_repeated_operations_do_not_rewrite_list(self):
        for operation, current in (("add", '"/tmp/signing.keychain-db" "/original.keychain-db"'),
                                   ("remove", '"/original.keychain-db"')):
            with self.subTest(operation=operation):
                self.assertEqual(len(self.run_update(operation, current)), 1)

    def test_failures_are_explicit_without_dumping_output(self):
        with patch("keychain_search.subprocess.run", return_value=subprocess.CompletedProcess([], 1, "", "synthetic-secret")):
            with self.assertRaisesRegex(ValueError, "^Could not read"):
                update_search_list("add", "/tmp/signing.keychain-db")
        with patch("keychain_search.subprocess.run", side_effect=[
            subprocess.CompletedProcess([], 0, '"/original.keychain-db"', ""),
            subprocess.CompletedProcess([], 1, "", "synthetic-secret"),
        ]):
            with self.assertRaisesRegex(ValueError, "^Could not add"):
                update_search_list("add", "/tmp/signing.keychain-db")

    def test_release_adds_before_identity_check_and_cleans_up(self):
        script = (Path(__file__).resolve().parent / "sign-package.sh").read_text()
        self.assertLess(script.index('keychain_search.py add "$keychain"'), script.index("python3 tools/macos/signing_identity.py"))
        self.assertIn('keychain_search.py remove "$keychain"', script)
        self.assertIn("trap cleanup EXIT", script)


@unittest.skipUnless(sys.platform == "darwin", "Synthetic codesign test requires macOS")
class KeychainSigningTests(unittest.TestCase):
    def test_imported_identity_signs_with_searchable_keychain(self):
        def run(*command):
            return subprocess.run(command, capture_output=True, text=True, check=True, timeout=30)

        original = shlex.split(run("security", "list-keychains", "-d", "user").stdout)
        with tempfile.TemporaryDirectory(prefix="ghcp-keychain-search-test-") as directory:
            root = Path(directory)
            keychain = str(root / "test.keychain-db")
            password = "synthetic-test-password"
            run("/usr/bin/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                "-keyout", str(root / "key.pem"), "-out", str(root / "cert.pem"), "-days", "1",
                "-subj", "/CN=GHCPSpendTray Synthetic Signing Test",
                "-addext", "basicConstraints=critical,CA:FALSE",
                "-addext", "keyUsage=critical,digitalSignature", "-addext", "extendedKeyUsage=codeSigning")
            fingerprint = run("/usr/bin/openssl", "x509", "-in", str(root / "cert.pem"),
                              "-fingerprint", "-sha1", "-noout").stdout.split("=")[1].strip().replace(":", "")
            run("/usr/bin/openssl", "pkcs12", "-export", "-inkey", str(root / "key.pem"),
                "-in", str(root / "cert.pem"), "-out", str(root / "test.p12"), "-passout", "pass:" + password)
            run("security", "create-keychain", "-p", password, keychain)
            try:
                update_search_list("add", keychain)
                run("security", "unlock-keychain", "-p", password, keychain)
                run("security", "import", str(root / "test.p12"), "-k", keychain, "-f", "pkcs12",
                    "-P", password, "-T", "/usr/bin/codesign")
                run("security", "set-key-partition-list", "-S", "apple-tool:,apple:,codesign:", "-s", "-k", password, keychain)
                shutil.copyfile("/usr/bin/true", root / "synthetic-executable")
                run("codesign", "--force", "--sign", fingerprint, "--keychain", keychain, str(root / "synthetic-executable"))
                run("codesign", "--verify", "--strict", str(root / "synthetic-executable"))
            finally:
                try:
                    update_search_list("remove", keychain)
                finally:
                    run("security", "delete-keychain", keychain)
            restored = shlex.split(run("security", "list-keychains", "-d", "user").stdout)
            self.assertNotIn(keychain, restored)
            self.assertTrue(all(entry in restored for entry in original))
