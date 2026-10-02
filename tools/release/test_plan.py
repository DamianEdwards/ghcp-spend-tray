import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import plan

REPOSITORY = "example/ghcp-spend-tray"
SOURCE = "a" * 40
RUN_ID = "1234"
BUMPS = {"windows": "Minor", "macos": "Minor"}


def release(tag, *, preview=False, draft=False, **extra):
    return dict(tag_name=tag, prerelease=preview, draft=draft, **extra)


def make_plan(releases=(), bumps=None, preview=False):
    return plan.create_plan(REPOSITORY, SOURCE, RUN_ID, preview, bumps or BUMPS, releases)


class VersionTests(unittest.TestCase):
    def test_bump_resets_lower_components(self):
        for platform in plan.PREFIXES:
            for bump, expected in (("Major", "3.0.0"), ("Minor", "2.4.0"), ("Patch", "2.3.5")):
                with self.subTest(platform=platform, bump=bump):
                    self.assertEqual(plan.next_version("2.3.4", bump, platform), expected)

    def test_first_release_default_matches_issue_39_with_other_bumps_available(self):
        for platform in plan.PREFIXES:
            for bump, value in (("Major", "1.0.0"), ("Minor", "0.1.0"), ("Patch", "0.0.1")):
                self.assertEqual(plan.next_version(None, bump, platform), value)
            self.assertIsNone(plan.next_version(None, "no release", platform))

    def test_independent_latest_stable_versions(self):
        data = [
            release("macos-v9.0.0", preview=True), release("v2.0.0", draft=True),
            release("v0.3.1"), release("v0.2.0"), release("macos-v1.2.3"),
            release("v0.4.0", preview=True), release("macos-v1.2.2"),
            release("unrelated-v99.0.0"), release("v0.5.0-preview.1"),
        ]
        result = make_plan(data)
        self.assertEqual(result["platforms"]["windows"],
                         dict(bump="Minor", baseVersion="0.3.1", version="0.4.0", tag="v0.4.0"))
        self.assertEqual(result["platforms"]["macos"],
                         dict(bump="Minor", baseVersion="1.2.3", version="1.3.0", tag="macos-v1.3.0"))

    def test_first_platform_ignores_other_platform_history(self):
        result = make_plan([release("v10.0.0")])
        self.assertEqual(result["platforms"]["macos"]["version"], "0.1.0")
        self.assertEqual(result["platforms"]["windows"]["version"], "10.1.0")

    def test_platforms_can_be_skipped_independently(self):
        for platform in plan.PREFIXES:
            bumps = dict(BUMPS, **{platform: "no release"})
            item = make_plan([release("v0.3.1")], bumps)["platforms"][platform]
            self.assertIsNone(item["version"])
            self.assertIsNone(item["tag"])

    def test_numeric_order_not_api_order_or_repository_latest(self):
        data = [release("v1.9.99"), release("v1.10.2"), release("v1.10.1"), release("macos-v90.0.0")]
        self.assertEqual(make_plan(data)["platforms"]["windows"]["baseVersion"], "1.10.2")

    def test_preview_dispatch_still_uses_stable_baseline(self):
        result = make_plan([release("v0.3.1"), release("v1.0.0", preview=True)], preview=True)
        self.assertTrue(result["prerelease"])
        self.assertEqual(result["platforms"]["windows"]["version"], "0.4.0")

    def test_missing_flags_fail_closed(self):
        for value in (dict(tag_name="v0.3.1"), dict(tag_name="v0.3.1", draft=False, prerelease="false")):
            with self.assertRaises(ValueError):
                make_plan([value])

    def test_input_and_package_boundaries(self):
        for platform in plan.PREFIXES:
            for value in ("0.0.0", "01.2.3", "1.2", "1.2.3.4", "v1.2.3", "1.2.3\n", "1.2.3;exit", "-1.0.0"):
                with self.subTest(value=value, platform=platform), self.assertRaises(ValueError):
                    plan.next_version(value, "Patch", platform)
            with self.assertRaises(ValueError):
                plan.next_version("1.0.0", "minor", platform)
        for platform, base, bump in (("windows", "65535.0.0", "Major"), ("windows", "1.65535.0", "Minor"),
                                     ("windows", "1.0.65535", "Patch"), ("macos", "9999.0.0", "Major"),
                                     ("macos", "1.99.0", "Minor"), ("macos", "1.0.99", "Patch")):
            with self.subTest(platform=platform, base=base), self.assertRaises(ValueError):
                plan.next_version(base, bump, platform)
        self.assertEqual(plan.next_version("1.99.99", "Major", "macos"), "2.0.0")


class CollisionTests(unittest.TestCase):
    def setUp(self):
        self.item = make_plan([release("v0.3.1")])["platforms"]["windows"]

    def test_first_dispatch_rejects_existing_preview_draft_or_tag(self):
        for existing in (release("v0.4.0", preview=True), release("v0.4.0", draft=True), release("v0.4.0")):
            with self.subTest(existing=existing), self.assertRaisesRegex(ValueError, "Choose a larger bump"):
                plan.check_selection("windows", self.item, [existing], {}, SOURCE, False)
        with self.assertRaises(ValueError):
            plan.check_selection("windows", self.item, [], {"v0.4.0": SOURCE}, SOURCE, False)

    def test_higher_preview_also_blocks_downgrade(self):
        with self.assertRaises(ValueError):
            plan.check_selection("windows", self.item, [release("v1.0.0", preview=True)], {}, SOURCE, False)

    def test_other_platform_and_older_tags_do_not_collide(self):
        self.assertIsNone(plan.check_selection("windows", self.item, [], {"macos-v9.0.0": SOURCE, "v0.3.1": SOURCE}, SOURCE, False))

    def test_no_release_ignores_collisions(self):
        item = make_plan(bumps=dict(BUMPS, windows="no release"))["platforms"]["windows"]
        self.assertIsNone(plan.check_selection("windows", item, [release("v99.0.0")], {}, SOURCE, False))

    def test_retry_allows_only_same_source_tag(self):
        existing = release("v0.4.0", draft=True)
        self.assertIsNone(plan.check_selection("windows", self.item, [existing], {"v0.4.0": SOURCE}, SOURCE, True))
        for tags in ({}, {"v0.4.0": "b" * 40}):
            with self.assertRaises(ValueError):
                plan.check_selection("windows", self.item, [existing], tags, SOURCE, True)

    def test_mac_collisions_are_independent(self):
        item = make_plan()["platforms"]["macos"]
        with self.assertRaises(ValueError):
            plan.check_selection("macos", item, [release("macos-v0.1.0", preview=True)], {}, SOURCE, False)
        self.assertIsNone(plan.check_selection("macos", item, [release("v10.0.0")], {}, SOURCE, False))

    def test_major_can_follow_first_preview_without_stable_baseline(self):
        releases = [release("macos-v0.1.0", preview=True)]
        item = make_plan(releases, dict(BUMPS, macos="Major"))["platforms"]["macos"]
        self.assertEqual(item["version"], "1.0.0")
        self.assertIsNone(plan.check_selection("macos", item, releases, {"macos-v0.1.0": "b" * 40}, SOURCE, False))


class RetryTests(unittest.TestCase):
    def test_saved_plan_is_bound_to_inputs_source_and_run(self):
        original = make_plan()
        plan.validate_saved_plan(original, REPOSITORY, SOURCE, RUN_ID, False, BUMPS)
        for field, value in (("repository", "other/repo"), ("sourceCommit", "b" * 40), ("runId", "5678"),
                             ("prerelease", True), ("schemaVersion", 2)):
            changed = copy.deepcopy(original)
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                plan.validate_saved_plan(changed, REPOSITORY, SOURCE, RUN_ID, False, BUMPS)
        changed = copy.deepcopy(original)
        changed["platforms"]["windows"]["version"] = "9.0.0"
        with self.assertRaises(ValueError):
            plan.validate_saved_plan(changed, REPOSITORY, SOURCE, RUN_ID, False, BUMPS)
        with self.assertRaises(ValueError):
            plan.validate_saved_plan(original, REPOSITORY, SOURCE, RUN_ID, False, dict(BUMPS, windows="Patch"))

    def test_public_release_must_have_same_run_provenance(self):
        original = make_plan()
        for platform in plan.PREFIXES:
            existing = release(original["platforms"][platform]["tag"])
            metadata = dict(platform=platform, version="0.1.0", sourceCommit=SOURCE, releaseRunId=RUN_ID,
                            signed=True, notarized=True)
            plan.check_published(original, platform, existing, metadata)
            for field, value in (("releaseRunId", "5678"), ("sourceCommit", "b" * 40), ("version", "0.2.0"), ("signed", False)):
                with self.subTest(platform=platform, field=field), self.assertRaises(ValueError):
                    plan.check_published(original, platform, existing, dict(metadata, **{field: value}))
            with self.assertRaises(ValueError):
                plan.check_published(original, platform, dict(existing, prerelease=True), metadata)
        with self.assertRaises(ValueError):
            plan.check_published(original, "macos", release("macos-v0.1.0"), dict(metadata, notarized=False))

    def test_main_retains_versions_after_one_platform_published(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "plan.json"
            env = dict(GITHUB_REF="refs/heads/main", GITHUB_REPOSITORY=REPOSITORY, GITHUB_SHA=SOURCE,
                       GITHUB_RUN_ID=RUN_ID, GITHUB_RUN_ATTEMPT="1", WINDOWS_BUMP="Minor", MACOS_BUMP="Minor",
                       PRERELEASE="false", GITHUB_OUTPUT=str(root / "output"), GITHUB_STEP_SUMMARY=str(root / "summary"))
            with patch.dict(os.environ, env), patch.object(plan, "read_releases", return_value=[release("v0.3.1")]), \
                 patch.object(plan, "read_tags", return_value={"v0.3.1": "b" * 40}):
                plan.main(path)
            original_bytes = path.read_bytes()
            current = [release("v0.4.0", assets=[dict(id=42, name="release.json")]), release("v0.3.1")]
            metadata = dict(platform="windows", version="0.4.0", sourceCommit=SOURCE, releaseRunId=RUN_ID, signed=True)
            (root / "output").write_text("")
            with patch.dict(os.environ, dict(env, GITHUB_RUN_ATTEMPT="2")), \
                 patch.object(plan, "read_releases", return_value=current), \
                 patch.object(plan, "read_tags", return_value={"v0.4.0": SOURCE}), \
                 patch.object(plan, "run", return_value=json.dumps(metadata)):
                plan.main(path)
            self.assertEqual(path.read_bytes(), original_bytes)
            output = (root / "output").read_text()
            self.assertIn("windows_release=false\nwindows_version=0.4.0", output)
            self.assertIn("macos_release=true\nmacos_version=0.1.0", output)

    def test_missing_saved_plan_never_recalculates(self):
        with tempfile.TemporaryDirectory() as directory, \
             patch.dict(os.environ, dict(GITHUB_REF="refs/heads/main", GITHUB_REPOSITORY=REPOSITORY, GITHUB_SHA=SOURCE,
                        GITHUB_RUN_ID=RUN_ID, GITHUB_RUN_ATTEMPT="2", WINDOWS_BUMP="Minor", MACOS_BUMP="Minor", PRERELEASE="true")), \
             patch.object(plan, "read_releases", return_value=[]), patch.object(plan, "read_tags", return_value={}):
            with self.assertRaisesRegex(ValueError, "missing"):
                plan.main(Path(directory, "plan.json"))

    def test_both_no_release_is_a_noop_without_github_access(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env = dict(GITHUB_REF="refs/heads/main", GITHUB_REPOSITORY=REPOSITORY, GITHUB_SHA=SOURCE,
                       GITHUB_RUN_ID=RUN_ID, GITHUB_RUN_ATTEMPT="1", WINDOWS_BUMP="no release", MACOS_BUMP="no release",
                       PRERELEASE="false", GITHUB_OUTPUT=str(root / "output"), GITHUB_STEP_SUMMARY=str(root / "summary"))
            with patch.dict(os.environ, env), patch.object(plan, "run", side_effect=AssertionError("No remote or git access expected")):
                plan.main(root / "plan.json")
            self.assertEqual((root / "output").read_text(), "windows_release=false\nwindows_version=\nmacos_release=false\nmacos_version=\n")


class GitHubTests(unittest.TestCase):
    def test_paginated_releases(self):
        first = [release(f"v1.0.{index}") for index in range(100)]
        with patch.object(plan, "run", side_effect=[json.dumps(first), json.dumps([release("macos-v0.1.0")])]) as api:
            self.assertEqual(len(plan.read_releases(REPOSITORY)), 101)
            self.assertTrue(api.call_args.args[-1].endswith("page=2"))

    def test_api_failure_never_looks_like_first_release(self):
        with patch.object(plan, "run", side_effect=OSError("Synthetic API failure")):
            with self.assertRaises(OSError):
                plan.read_releases(REPOSITORY)
        with patch.object(plan, "run", return_value='{"message":"Forbidden"}'):
            with self.assertRaises(ValueError):
                plan.read_releases(REPOSITORY)

    def test_annotated_tags_are_peeled(self):
        with patch.object(plan, "run", return_value=f"v0.3.1 {SOURCE} \nmacos-v0.1.0 {'b' * 40} {SOURCE}"):
            self.assertEqual(plan.read_tags(), {"v0.3.1": SOURCE, "macos-v0.1.0": SOURCE})


if __name__ == "__main__":
    unittest.main()
