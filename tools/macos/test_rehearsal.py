import json
from pathlib import Path
import plistlib
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import rehearsal
import updates


class RehearsalTests(unittest.TestCase):
    def test_fingerprint_checks_bytes_permissions_and_framework_symlinks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binary = root / "executable"
            binary.write_bytes(b"synthetic A")
            binary.chmod(0o755)
            link = root / "framework"
            link.symlink_to("executable")
            original = rehearsal.fingerprint(root)
            self.assertEqual(original["framework"], dict(link="executable"))
            binary.write_bytes(b"synthetic B")
            self.assertNotEqual(original, rehearsal.fingerprint(root))
            binary.write_bytes(b"synthetic A")
            binary.chmod(0o644)
            self.assertNotEqual(original, rehearsal.fingerprint(root))
            binary.chmod(0o755)
            link.unlink()
            link.symlink_to("other")
            self.assertNotEqual(original, rehearsal.fingerprint(root))

    def test_incomplete_event_writes_are_not_mistaken_for_missing_or_invalid_callbacks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual(rehearsal.events(root), [])
            path = root / "events.jsonl"
            path.write_bytes(b'{"event":"started"}\n{"event":"preser')
            self.assertEqual(rehearsal.events(root), [dict(event="started")])
            path.write_bytes(b"invalid\n")
            with self.assertRaises(json.JSONDecodeError):
                rehearsal.events(root)

    def test_bad_archives_require_signature_errors_not_transport_failure(self):
        signature = dict(event="updater-error", marker="rehearsal-A", domain="SUSparkleErrorDomain", code=4005,
                         errors=[dict(domain="SUSparkleErrorDomain", code=3002)])
        network = dict(signature, code=2001, errors=[])
        for name in ("corrupt-archive", "wrong-signature"):
            rehearsal.validate_rejection(name, [signature])
            for records in ([network], [], [signature, dict(event="ready-to-install", marker="rehearsal-A")],
                            [signature, dict(event="started", marker="rehearsal-B")]):
                with self.assertRaises(RuntimeError):
                    rehearsal.validate_rejection(name, records)
        rehearsal.validate_rejection("interrupted-download", [network])
        with self.assertRaises(RuntimeError):
            rehearsal.validate_rejection("interrupted-download", [signature])

    def test_cancellation_must_be_observed_without_a_replacement_process(self):
        for name, event in (("cancel-download", "cancelled-download"), ("cancel-install", "cancelled-install")):
            records = [dict(event=event, marker="rehearsal-A")]
            rehearsal.validate_rejection(name, records)
            with self.assertRaises(RuntimeError):
                rehearsal.validate_rejection(name, [dict(event="failure", marker="rehearsal-A")])
            with self.assertRaises(RuntimeError):
                rehearsal.validate_rejection(name, records + [dict(event="started", marker="rehearsal-B")])

    def test_feed_signs_the_exact_archive_and_contains_platform_requirements(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "synthetic.dmg"
            archive.write_bytes(b"synthetic archive")
            feed = root / "appcast.xml"
            with patch.object(rehearsal, "sign_file", side_effect=["synthetic-signature", None]) as sign:
                rehearsal.feed(feed, archive=archive, archive_url="http://127.0.0.1:12345/update.dmg",
                               value="9000.0.2", seed="synthetic-unused", sign_tool=Path("sign-tool"))
            item = ET.fromstring(feed.read_bytes()).find("./channel/item")
            self.assertEqual(item.findtext(f"{{{rehearsal.NAMESPACE}}}version"), "9000.0.2")
            self.assertEqual(item.findtext(f"{{{rehearsal.NAMESPACE}}}minimumSystemVersion"), "15.0")
            self.assertEqual(item.findtext(f"{{{rehearsal.NAMESPACE}}}hardwareRequirements"), "arm64")
            self.assertEqual(item.find("enclosure").get("length"), str(archive.stat().st_size))
            self.assertEqual(sign.call_args_list[0].args[1], archive)
            self.assertEqual(sign.call_args_list[1].args[1], feed)

    def test_rehearsal_hooks_are_explicit_and_forbidden_in_release_builds(self):
        build = (rehearsal.ROOT / "tools/macos/build.sh").read_text()
        self.assertIn('-D UPDATE_REHEARSAL tools/macos/rehearsal/AppRehearsal.swift', build)
        self.assertIn('[[ "$channel" == Development ]]', build)
        source = (rehearsal.ROOT / "tools/macos/rehearsal/AppRehearsal.swift").read_text()
        self.assertIn('feedURL.host == "127.0.0.1"', source)
        self.assertIn('Bundle.main.bundleIdentifier == configuration.identifier', source)
        self.assertIn('allowInteraction: false', source)
        self.assertIn('root.resolvingSymlinksInPath() == root', source)
        updates = (rehearsal.ROOT / "src/GHCPSpendTray.Mac/Updates.swift").read_text()
        feed_check = updates.split("private func validFeed(")[1].split("func allowedChannels(")[0]
        self.assertIn('#if UPDATE_REHEARSAL', feed_check)
        self.assertIn('return url.scheme == "https"', feed_check)

    def test_production_package_validation_rejects_the_rehearsal_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory, "Synthetic.app")
            contents = app / "Contents"
            contents.mkdir(parents=True)
            info = plistlib.loads((rehearsal.ROOT / "packaging/macos/Info.plist").read_bytes())
            info.update(GHCPReleaseChannel="Stable", GHCPUpdateRehearsal="/synthetic/rehearsal.json")
            (contents / "Info.plist").write_bytes(plistlib.dumps(info))
            with self.assertRaisesRegex(ValueError, "rehearsal bundle"):
                updates.inspect_bundle(app)


if __name__ == "__main__":
    unittest.main()
