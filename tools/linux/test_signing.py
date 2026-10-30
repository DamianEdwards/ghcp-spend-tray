import hashlib
import importlib.util
import os
from pathlib import Path
import platform
import struct
import subprocess
import tempfile
import unittest

import sign_appimage as signing

ROOT = Path(__file__).resolve().parents[2]
PASSPHRASE = "synthetic-test-only"


def unsigned_fixture(path):
    data = bytearray(16384)
    data[:7] = b"\x7fELF\x02\x01\x01"
    data[8:11] = b"AI\x02"
    struct.pack_into("<H", data, 18, 62)
    struct.pack_into("<Q", data, 40, 64)
    struct.pack_into("<HHH", data, 58, 64, 4, 1)
    names = b"\0.shstrtab\0.sha256_sig\0.sig_key\0"
    data[320:320 + len(names)] = names
    for index, name, kind, offset, size in (
            (1, b".shstrtab", 3, 320, len(names)),
            (2, b".sha256_sig", 1, 512, 4096),
            (3, b".sig_key", 1, 4608, 8192)):
        struct.pack_into("<IIQQQQIIQQ", data, 64 + index * 64, names.index(name), kind,
                         0, 0, offset, size, 0, 0, 1, 0)
    data[-17:] = b"synthetic payload"
    path.write_bytes(data)
    path.with_name(path.name + ".sha256").write_text(f"{hashlib.sha256(data).hexdigest()}  {path.name}\n")


class SigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ring = signing.keyring()
        cls.home = cls.ring.__enter__()
        cls.addClassCleanup(cls.ring.__exit__, None, None, None)
        signing.gpg(cls.home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0",
                              "--quick-generate-key", "Synthetic Linux signing <test@example.invalid>",
                              "ed25519", "sign", "1d"], (PASSPHRASE + "\n").encode())
        keys = signing.gpg(cls.home, ["--with-colons", "--list-secret-keys"]).decode()
        cls.fingerprint = next(line.split(":")[9] for line in keys.splitlines() if line.startswith("fpr:"))
        cls.private = signing.gpg(cls.home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0",
                                           "--export-secret-keys", cls.fingerprint], (PASSPHRASE + "\n").encode())

    def test_embedded_and_detached_signatures_cover_exact_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / "test.AppImage"
            unsigned_fixture(image)
            original = image.read_bytes()
            output = root / "signed"
            signing.sign(image, output, self.fingerprint, self.private, PASSPHRASE)
            signed = output / image.name
            regions = signing.verify(signed, self.fingerprint)
            self.assertEqual(image.read_bytes(), original)
            modified = signed.read_bytes()
            for index, (before, after) in enumerate(zip(original, modified)):
                if before != after:
                    self.assertTrue(any(offset <= index < offset + size for offset, size in regions.values()))
            self.assertEqual(len(original), len(modified))
            self.assertEqual((output / (image.name + ".sha256")).read_text(),
                             f"{hashlib.sha256(modified).hexdigest()}  {image.name}\n")
            self.assertEqual(set(path.name for path in output.iterdir()),
                             {image.name, image.name + ".sha256", image.name + ".asc", "signing-key.asc", "SIGNING.txt"})
            with self.assertRaisesRegex(ValueError, "fingerprint"):
                signing.verify(signed, "A" * 40)
            for offset in (380, len(modified) - 1):
                damaged = bytearray(modified)
                damaged[offset] ^= 1
                signed.write_bytes(damaged)
                with self.assertRaises(RuntimeError):
                    signing.verify(signed, self.fingerprint)
            signed.write_bytes(modified)
            with self.assertRaisesRegex(ValueError, "existing AppImage signature"):
                signing.sign(signed, root / "resigned", self.fingerprint, self.private, PASSPHRASE)

    def test_failures_never_publish_signed_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / "test.AppImage"
            unsigned_fixture(image)
            for key, password, expected in ((b"", PASSPHRASE, self.fingerprint),
                                            (self.private, "", self.fingerprint),
                                            (self.private, "wrong-passphrase", self.fingerprint),
                                            (self.private, PASSPHRASE, "B" * 40)):
                with self.subTest(expected=expected, empty_key=not key):
                    with self.assertRaises((ValueError, RuntimeError)):
                        signing.sign(image, root / "signed", expected, key, password)
                    self.assertFalse((root / "signed").exists())
            image.with_name(image.name + ".sha256").write_text("incorrect")
            with self.assertRaisesRegex(ValueError, "checksum"):
                signing.sign(image, root / "signed", self.fingerprint, self.private, PASSPHRASE)
            self.assertFalse((root / "signed").exists())
            self.assertEqual(list(root.glob(".sign-stage-*")), [])

    def test_rejects_unsigned_and_malformed_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "test.AppImage"
            unsigned_fixture(image)
            with self.assertRaisesRegex(ValueError, "no embedded"):
                signing.verify(image, self.fingerprint)
            original = image.read_bytes()
            for position, value in ((4, 1), (8, 0)):
                damaged = bytearray(original)
                damaged[position] = value
                image.write_bytes(damaged)
                with self.assertRaises(ValueError):
                    signing.sections(image)
            damaged = bytearray(original)
            struct.pack_into("<Q", damaged, 64 + 2 * 64 + 24, 64)
            image.write_bytes(damaged)
            with self.assertRaisesRegex(ValueError, "overlap"):
                signing.sections(image)
            with self.assertRaises(ValueError):
                signing.fingerprint("12345678")

    def test_signing_only_subkey_export_and_unicode_identity(self):
        with signing.keyring() as home, tempfile.TemporaryDirectory() as directory:
            password = (PASSPHRASE + "\n").encode()
            signing.gpg(home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0",
                              "--quick-generate-key", "Synthetic \u00e9 signer <subkey@example.invalid>",
                              "ed25519", "cert", "1d"], password)
            def fingerprints():
                listing = signing.gpg(home, ["--with-colons", "--list-secret-keys"]).decode("utf-8")
                return [line.split(":")[9] for line in listing.splitlines() if line.startswith("fpr:")]
            primary = fingerprints()[0]
            signing.gpg(home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0",
                              "--quick-add-key", primary, "ed25519", "sign", "1d"], password)
            subkey = fingerprints()[1]
            private = signing.gpg(home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0",
                                        "--export-secret-subkeys", subkey], password)
            root = Path(directory)
            image = root / "test.AppImage"
            unsigned_fixture(image)
            signing.sign(image, root / "signed", subkey, private, PASSPHRASE)
            signing.verify(root / "signed/test.AppImage", subkey)
            with self.assertRaisesRegex(ValueError, "fingerprint"):
                signing.verify(root / "signed/test.AppImage", primary)

    def test_pinned_official_appimagetool_signature_interoperability(self):
        spec = importlib.util.spec_from_file_location("native_package", ROOT / "tools/linux/package-native.py")
        package = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(package)
        architecture = platform.machine()
        tool = package.download_tool("appimagetool", architecture)
        runtime = package.download_tool("runtime", architecture)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            appdir = root / "Synthetic.AppDir"
            appdir.mkdir()
            (appdir / "AppRun").write_text("#!/bin/sh\nexit 0\n")
            (appdir / "AppRun").chmod(0o755)
            (appdir / "synthetic.desktop").write_text(
                "[Desktop Entry]\nType=Application\nName=Synthetic\nExec=synthetic\nIcon=synthetic\nCategories=Utility;\n")
            (appdir / "synthetic.svg").write_text(
                '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><rect width="16" height="16"/></svg>')
            image = root / "official.AppImage"
            environment = {key: value for key, value in os.environ.items() if key not in signing.SECRET_NAMES}
            environment.update(GNUPGHOME=str(self.home), ARCH=architecture, APPIMAGETOOL_SIGN_PASSPHRASE=PASSPHRASE)
            result = subprocess.run([str(tool), "--appimage-extract-and-run", "--runtime-file", str(runtime),
                                     "--no-appstream", "--sign", "--sign-key", self.fingerprint,
                                     str(appdir), str(image)], env=environment, capture_output=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr.decode())
            signing.verify(image, self.fingerprint)

    @unittest.skipUnless(os.environ.get("GHCP_SIGNING_TEST_IMAGE"), "Run after packaging with GHCP_SIGNING_TEST_IMAGE.")
    def test_actual_appimage_signature(self):
        image = Path(os.environ["GHCP_SIGNING_TEST_IMAGE"]).resolve()
        with tempfile.TemporaryDirectory(prefix="ghcp-signed-smoke-") as directory:
            root = Path(directory)
            output = root / "signed"
            signing.sign(image, output, self.fingerprint, self.private, PASSPHRASE)
            signed = output / image.name
            signing.verify(signed, self.fingerprint)
            signature = subprocess.run([str(signed), "--appimage-signature"], capture_output=True,
                                       check=True, timeout=15).stdout
            self.assertTrue(signature.startswith(b"-----BEGIN PGP SIGNATURE-----"))
            runtime = root / "runtime"
            runtime.mkdir(mode=0o700)
            environment = dict(os.environ, HOME=str(root), XDG_CONFIG_HOME=str(root / "config"),
                               XDG_DATA_HOME=str(root / "data"), XDG_STATE_HOME=str(root / "state"),
                               XDG_CACHE_HOME=str(root / "cache"), XDG_RUNTIME_DIR=str(runtime),
                               DBUS_SYSTEM_BUS_ADDRESS="unix:path=" + str(root / "no-system-bus"),
                               QT_QPA_PLATFORM="offscreen", GIO_USE_VFS="local")
            environment.pop("DBUS_SESSION_BUS_ADDRESS", None)
            subprocess.run(["dbus-run-session", "--config-file=" + str(ROOT / "tools/linux/session-bus.conf"),
                            "--", str(signed), "--appimage-extract-and-run", "--smoke-ui"],
                           env=environment, check=True, timeout=60)
