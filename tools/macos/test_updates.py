import base64
import copy
import json
import os
from pathlib import Path
import platform
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import updates


# Public RFC 8032 test vector, never a production signing key.
SEED = base64.b64encode(bytes.fromhex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60")).decode()
PUBLIC = base64.b64encode(bytes.fromhex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")).decode()
SIGNATURE = base64.b64encode(bytes(64)).decode()
SLUG = "example/ghcp-spend-tray"
ROOT = Path(__file__).resolve().parents[2]


def release(value="0.3.0", preview=False, draft=False, with_feed=True):
    return dict(tag_name=f"macos-v{value}", draft=draft, prerelease=preview, published_at="2026-10-04T00:00:00Z",
                assets=[dict(name=name, id=index + 1, size=123) for index, name in enumerate(
                    ["release-macos.json", f"GHCPSpendTray-macOS-{value}.dmg"] + (["appcast.xml"] if with_feed else []))])


def appcast(value="0.3.0", preview=False):
    root = ET.Element("rss", version="2.0")
    item = ET.SubElement(ET.SubElement(root, "channel"), "item")
    for key, text in (("version", value), ("minimumSystemVersion", "15.0"), ("hardwareRequirements", "arm64")):
        ET.SubElement(item, f"{{{updates.SPARKLE}}}{key}").text = text
    if preview:
        ET.SubElement(item, f"{{{updates.SPARKLE}}}channel").text = "preview"
    ET.SubElement(item, "enclosure", url=updates.download_url(SLUG, value), length="123",
                  **{f"{{{updates.SPARKLE}}}edSignature": SIGNATURE})
    return ET.tostring(root)


def metadata(value="0.3.0"):
    return dict(platform="macos", version=value, signed=True, notarized=True, architectures=["arm64"],
                minimumOS="15.0", bundleIdentifier="com.damianedwards.GHCPSpendTray")


class UpdateToolTests(unittest.TestCase):
    def test_configuration_preserves_permission_flow_and_requires_production_keys(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            path = Path(directory, "Info.plist")
            path.write_bytes((ROOT / "packaging/macos/Info.plist").read_bytes())
            updates.configure(path, "Development")
            self.assertNotIn("SUPublicEDKey", plistlib.loads(path.read_bytes()))
            with self.assertRaisesRegex(ValueError, "32-byte"):
                updates.configure(path, "Stable")
            with patch.dict(os.environ, SPARKLE_PUBLIC_ED_KEY=PUBLIC):
                for channel in ("Stable", "Preview"):
                    updates.configure(path, channel)
                    data = plistlib.loads(path.read_bytes())
                    self.assertEqual(data["SUPublicEDKey"], PUBLIC)
                    self.assertNotIn("SUEnableAutomaticChecks", data)
                    self.assertFalse(data["SUAutomaticallyUpdate"])
                    self.assertFalse(data["SUEnableSystemProfiling"])
                    self.assertTrue(data["SURequireSignedFeed"])
                    self.assertTrue(data["SUVerifyUpdateBeforeExtraction"])
            with self.assertRaises(ValueError):
                updates.configure(path, "Unknown")

    def test_public_keys_and_https_feed_urls_fail_closed(self):
        self.assertEqual(updates.public_key(PUBLIC), PUBLIC)
        for value in ("", "not a key", base64.b64encode(bytes(31)).decode()):
            with self.assertRaises(ValueError):
                updates.public_key(value)
        self.assertEqual(updates.feed_url(updates.DEFAULT_FEED), updates.DEFAULT_FEED)
        for value in ("http://example.com/appcast.xml", "https://user:pass@example.com/appcast.xml",
                      "https://example.com/appcast.xml?token=secret", "file:///appcast.xml",
                      "https://example.com/other.xml", "https://example.com/appcast.xml#fragment", ""):
            with self.assertRaises(ValueError, msg=value):
                updates.feed_url(value)

    def test_appcast_metadata_is_bound_to_exact_archive_and_platform(self):
        updates.validate_item(appcast(), SLUG, "0.3.0", 123)
        for key, value in (("version", "0.2.0"), ("minimumSystemVersion", "26.0"),
                           ("hardwareRequirements", "x86_64"), ("channel", "preview")):
            root = ET.fromstring(appcast())
            item = root.find("./channel/item")
            element = item.find(f"{{{updates.SPARKLE}}}{key}")
            if element is None:
                element = ET.SubElement(item, f"{{{updates.SPARKLE}}}{key}")
            element.text = value
            with self.assertRaises(ValueError):
                updates.validate_item(ET.tostring(root), SLUG, "0.3.0", 123)
        for attribute, value in (("url", "https://example.com/wrong.dmg"), ("length", "124"),
                                 (f"{{{updates.SPARKLE}}}edSignature", "")):
            root = ET.fromstring(appcast())
            root.find("./channel/item/enclosure").set(attribute, value)
            with self.assertRaises(ValueError):
                updates.validate_item(ET.tostring(root), SLUG, "0.3.0", 123)
        root = ET.fromstring(appcast())
        root.find("channel").append(copy.deepcopy(root.find("./channel/item")))
        with self.assertRaises(ValueError):
            updates.validate_item(ET.tostring(root), SLUG, "0.3.0", 123)

    def test_feed_is_numeric_mac_only_stable_and_keeps_older_compatible_updates(self):
        releases = [release("0.9.0"), release("0.10.0"), release("1.0.0", preview=True),
                    release("2.0.0", draft=True), release("0.2.0", with_feed=False),
                    dict(tag_name="v99.0.0", draft=False, prerelease=False)]
        data = {}
        for item in releases[:2]:
            value = item["tag_name"].removeprefix("macos-v")
            for asset in item["assets"]:
                data[id(asset)] = json.dumps(metadata(value)).encode() if asset["name"] == "release-macos.json" else appcast(value)
        verified = []
        feed = updates.assemble(releases, SLUG, lambda asset: data[id(asset)], verified.append)
        items = ET.fromstring(feed).findall("./channel/item")
        self.assertEqual([item.findtext(f"{{{updates.SPARKLE}}}version") for item in items], ["0.10.0", "0.9.0"])
        self.assertEqual(len(verified), 2)
        self.assertTrue(all(item.findtext("pubDate") for item in items))

    def test_missing_latest_feed_and_unsigned_or_universal_metadata_block_publication(self):
        with self.assertRaisesRegex(ValueError, "latest stable"):
            updates.assemble([release(with_feed=False), release("0.2.0")], SLUG, None, None)
        with self.assertRaisesRegex(ValueError, "No published"):
            updates.assemble([release(preview=True)], SLUG, None, None)
        for mutation in (dict(signed=False), dict(notarized=False), dict(architectures=["arm64", "x86_64"]),
                         dict(version="0.2.0"), dict(bundleIdentifier="other.app"), dict(minimumOS="26.0")):
            with self.assertRaises(ValueError):
                updates.assemble([release()], SLUG, lambda asset: json.dumps(dict(metadata(), **mutation)).encode(), None)
        def rejected(_):
            raise ValueError("Synthetic invalid feed signature")
        with self.assertRaisesRegex(ValueError, "Synthetic invalid"):
            updates.assemble([release()], SLUG,
                             lambda asset: json.dumps(metadata()).encode() if asset["name"] == "release-macos.json" else appcast(),
                             rejected)

    def test_api_asset_ids_are_validated_before_download(self):
        for value in (None, "1", 0, -1, True):
            with patch.object(updates.subprocess, "check_output") as download:
                with self.assertRaises(ValueError):
                    updates.asset_bytes(SLUG, dict(id=value))
                download.assert_not_called()

    def test_pages_configuration_is_required_before_creating_or_signing_a_site(self):
        for site in (dict(build_type="legacy", html_url=updates.DEFAULT_FEED.removesuffix("appcast.xml")),
                     dict(build_type="workflow", html_url="https://example.com/other/"),
                     dict(build_type="workflow")):
            with tempfile.TemporaryDirectory() as directory, \
                 patch.dict(os.environ, GITHUB_REPOSITORY=SLUG, MACOS_UPDATE_FEED_URL=updates.DEFAULT_FEED), \
                 patch.object(updates, "validate_keys"), \
                 patch.object(updates.subprocess, "check_output", return_value=json.dumps(site)), \
                 patch.object(updates, "signing_command") as sign:
                destination = Path(directory, "site")
                with self.assertRaisesRegex(ValueError, "Enable GitHub Pages"):
                    updates.build_feed(destination)
                self.assertFalse(destination.exists())
                sign.assert_not_called()


@unittest.skipUnless(platform.system() == "Darwin" and platform.machine() == "arm64",
                     "Sparkle signing fixtures require native Apple silicon.")
class UpdateSigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["bash", "-c", "source tools/macos/sparkle.sh"], cwd=ROOT, check=True)
        lock = json.loads((ROOT / "packaging/macos/sparkle.json").read_text())
        cls.environment = dict(SPARKLE_ROOT=str(ROOT / "artifacts/sparkle" / lock["version"]),
                               SPARKLE_PUBLIC_ED_KEY=PUBLIC, SPARKLE_PRIVATE_ED_KEY=SEED)

    def test_actual_signing_and_tampered_feed_rejection(self):
        with patch.dict(os.environ, self.environment), tempfile.TemporaryDirectory() as directory:
            updates.validate_keys()
            path = Path(directory, "appcast.xml")
            path.write_bytes(appcast())
            updates.signing_command("sign_update", path)
            updates.signing_command("sign_update", "--verify", path)
            path.write_bytes(path.read_bytes().replace(b"0.3.0", b"0.4.0"))
            with self.assertRaises(subprocess.CalledProcessError):
                updates.signing_command("sign_update", "--verify", path)

    def test_mismatched_public_private_keys_are_rejected(self):
        with patch.dict(os.environ, dict(self.environment, SPARKLE_PUBLIC_ED_KEY=base64.b64encode(bytes(32)).decode())):
            with self.assertRaises(subprocess.CalledProcessError):
                updates.validate_keys()

    def test_real_dmg_appcast_generation_signs_exact_archive_bytes(self):
        with tempfile.TemporaryDirectory(prefix="ghcp-synthetic-update-") as directory:
            root = Path(directory)
            app = root / "layout/GHCPSpendTray.app"
            executable = app / "Contents/MacOS/GHCPSpendTray"
            executable.parent.mkdir(parents=True)
            plist = plistlib.loads((ROOT / "packaging/macos/Info.plist").read_bytes())
            plist.update(CFBundleVersion="0.3.0", CFBundleShortVersionString="0.3.0", SUPublicEDKey=PUBLIC,
                         GHCPReleaseChannel="Stable")
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps(plist))
            subprocess.run(["xcrun", "clang", "-target", "arm64-apple-macos15.0", "-x", "c", "-",
                            "-o", str(executable)], input="int main(void) { return 0; }", text=True, check=True)
            subprocess.run(["codesign", "--force", "--sign", "-", str(app)], check=True)
            dmg = root / "GHCPSpendTray-macOS-0.3.0.dmg"
            subprocess.run(["hdiutil", "create", "-quiet", "-srcfolder", str(app.parent), "-format", "UDZO", str(dmg)], check=True)
            environment = dict(self.environment, GITHUB_REPOSITORY=SLUG, PRERELEASE="false")
            with patch.dict(os.environ, environment):
                updates.generate(dmg, "0.3.0")
                item = updates.validate_item((root / "appcast.xml").read_bytes(), SLUG, "0.3.0", dmg.stat().st_size)
                signature = item.find("enclosure").get(f"{{{updates.SPARKLE}}}edSignature")
                # The appcast must sign the distribution archive, not just the app.
                dmg.write_bytes(dmg.read_bytes() + b"synthetic tampering")
                result = subprocess.run(["artifacts/sparkle/update-crypto", str(dmg), signature], capture_output=True)
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
