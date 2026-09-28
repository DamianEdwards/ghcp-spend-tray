import unittest
from unittest.mock import patch
from release import check_tags, version
import release


class ReleaseTests(unittest.TestCase):
    def test_versions(self):
        for value in ("0.1.0", "1.0.0", "9999.99.99"):
            self.assertEqual(version(value), tuple(map(int, value.split("."))))
        for value in ("0.0.0", "1.2", "v1.2.3", "1.2.3.4", "01.2.3", "1.2.3\n",
                      "1.2.3-preview", "10000.0.0", "1.100.0", "1.0.100", "1.2.3;exit"):
            with self.assertRaises(ValueError, msg=value):
                version(value)

    def test_independent_versions(self):
        check_tags("0.1.0", {"v9.0.0": "windows", "macos-v0.1.0": "source"}, "source")
        check_tags("0.2.0", {"v9.0.0": "windows", "macos-v0.1.0": "old"}, "new")
        with self.assertRaises(ValueError):
            check_tags("0.1.0", {"macos-v0.1.0": "other"}, "source")
        with self.assertRaises(ValueError):
            check_tags("0.1.0", {"macos-v0.2.0": "newer"}, "source")

    def test_latest_source_run_must_succeed(self):
        source = "a" * 40
        old = dict(id=1, head_sha=source, head_branch="main", event="push",
                   status="completed", conclusion="success", run_attempt=1)
        latest = dict(old, id=2, conclusion="failure")
        with patch.object(release, "pages", return_value=iter([old, latest])):
            with self.assertRaises(ValueError):
                release.check_source("example/repo", source)
        with patch.object(release, "pages", side_effect=[
            iter([old]), iter([dict(name="Verification", head_sha=source, conclusion="success")])
        ]):
            release.check_source("example/repo", source)
        with patch.object(release, "pages", side_effect=[
            iter([old]), iter([dict(name="Verification", head_sha=source, conclusion="skipped")])
        ]):
            with self.assertRaises(ValueError):
                release.check_source("example/repo", source)

    def test_published_release_cannot_be_overwritten(self):
        with patch.object(release, "existing_release", return_value={"draft": False}):
            with self.assertRaises(ValueError):
                release.ensure_draft("example/repo", "macos-v0.1.0")
        with patch.object(release, "existing_release", return_value={"draft": True}):
            self.assertTrue(release.ensure_draft("example/repo", "macos-v0.1.0")["draft"])

    def test_release_pagination(self):
        first = [dict(tag_name=f"v{index}.0.0") for index in range(100)]
        with patch.object(release, "gh_json", side_effect=[first, [dict(tag_name="macos-v0.1.0")]]) as api:
            self.assertEqual(len(list(release.pages("repos/example/repo/releases"))), 101)
            self.assertTrue(api.call_args.args[0].endswith("page=2"))


if __name__ == "__main__":
    unittest.main()
