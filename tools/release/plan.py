#!/usr/bin/env python3
"""Resolve independent release bumps and retain the exact plan across run attempts."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess

PREFIXES = {"windows": "v", "macos": "macos-v"}
BUMPS = ("no release", "Major", "Minor", "Patch")
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")


def parse_version(value, platform):
    if not isinstance(value, str) or not VERSION.fullmatch(value):
        raise ValueError(f"Invalid {platform} version: {value!r}")
    parts = tuple(map(int, value.split(".")))
    limits = (65535, 65535, 65535) if platform == "windows" else (9999, 99, 99)
    if parts == (0, 0, 0) or any(part > limit for part, limit in zip(parts, limits)):
        raise ValueError(f"{platform} version {value} exceeds its package bounds or is zero.")
    return parts


def tag_version(tag, platform):
    prefix = PREFIXES[platform]
    if not isinstance(tag, str) or not tag.startswith(prefix):
        return None
    value = tag[len(prefix):]
    return value if VERSION.fullmatch(value) else None


def next_version(base, bump, platform):
    if bump not in BUMPS:
        raise ValueError(f"Invalid {platform} bump choice: {bump!r}")
    if bump == "no release":
        return None
    parts = [0, 0, 0] if base is None else list(parse_version(base, platform))
    index = ("Major", "Minor", "Patch").index(bump)
    parts[index] += 1
    parts[index + 1:] = [0] * (2 - index)
    result = ".".join(map(str, parts))
    parse_version(result, platform)
    return result


def create_plan(repository, source, run_id, preview, bumps, releases):
    platforms = {}
    for platform, prefix in PREFIXES.items():
        base = None
        if bumps[platform] != "no release":
            stable = []
            for release in releases:
                value = tag_version(release.get("tag_name"), platform)
                if value is None:
                    continue
                if type(release.get("draft")) is not bool or type(release.get("prerelease")) is not bool:
                    raise ValueError("GitHub returned a release without valid draft/preview flags.")
                if not release["draft"] and not release["prerelease"]:
                    parse_version(value, platform)
                    stable.append(value)
            base = max(stable, key=lambda value: parse_version(value, platform), default=None)
        value = next_version(base, bumps[platform], platform)
        platforms[platform] = dict(bump=bumps[platform], baseVersion=base, version=value,
                                   tag=prefix + value if value else None)
    return dict(schemaVersion=1, repository=repository, sourceCommit=source,
                runId=run_id, prerelease=preview, platforms=platforms)


def validate_saved_plan(plan, repository, source, run_id, preview, bumps):
    expected = dict(schemaVersion=1, repository=repository, sourceCommit=source,
                    runId=run_id, prerelease=preview)
    if any(plan.get(key) != value for key, value in expected.items()) or set(plan.get("platforms", {})) != set(PREFIXES):
        raise ValueError("Saved release plan does not match this workflow run, source, or preview option.")
    for platform, prefix in PREFIXES.items():
        item = plan["platforms"][platform]
        value = next_version(item["baseVersion"], bumps[platform], platform)
        if item["bump"] != bumps[platform] or item["version"] != value or item["tag"] != (prefix + value if value else None):
            raise ValueError(f"Saved {platform} release selection does not match the requested bump.")


def check_selection(platform, item, releases, tags, source, retry):
    """Return an existing public release only when a retry can verify its provenance."""
    if item["version"] is None:
        return None
    target = parse_version(item["version"], platform)
    tag = item["tag"]
    existing = next((release for release in releases if release.get("tag_name") == tag), None)
    all_tags = set(tags) | {release["tag_name"] for release in releases}
    for other in sorted(all_tags):
        value = tag_version(other, platform)
        if value is None or parse_version(value, platform) < target:
            continue
        if other == tag and retry and tags.get(tag) == source:
            continue
        raise ValueError(
            f"{platform}: {item['bump']} from {item['baseVersion'] or 'no stable release'} selects {tag}, "
            f"but {other} already occupies that version or a higher one. "
            "Previews and drafts are excluded from the baseline, not from collision checks. "
            "Choose a larger bump or 'no release'; existing tags/releases will not be overwritten."
        )
    return existing if existing and existing["draft"] is False else None


def check_published(plan, platform, release, metadata):
    item = plan["platforms"][platform]
    expected = dict(platform=platform, version=item["version"], sourceCommit=plan["sourceCommit"],
                    releaseRunId=plan["runId"], signed=True)
    if platform == "macos":
        expected["notarized"] = True
    if release["prerelease"] != plan["prerelease"] or any(metadata.get(key) != value for key, value in expected.items()):
        raise ValueError(f"The public {platform} release was not published by this run with the planned source/version.")


def run(*arguments):
    return subprocess.check_output(arguments, text=True).strip()


def read_releases(repository):
    result = []
    page = 1
    while True:
        batch = json.loads(run("gh", "api", f"repos/{repository}/releases?per_page=100&page={page}"))
        if not isinstance(batch, list):
            raise ValueError("GitHub did not return a release list.")
        result.extend(batch)
        if len(batch) < 100:
            return result
        page += 1


def read_tags():
    lines = run("git", "for-each-ref", "--format=%(refname:strip=2) %(objectname) %(*objectname)", "refs/tags").splitlines()
    tags = {}
    for line in lines:
        fields = line.split()
        tags[fields[0]] = fields[2] if len(fields) == 3 else fields[1]
    return tags


def main(path):
    if os.environ["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("Release must be dispatched from main.")
    repository, source, run_id = (os.environ[name] for name in ("GITHUB_REPOSITORY", "GITHUB_SHA", "GITHUB_RUN_ID"))
    bumps = {platform: os.environ[platform.upper() + "_BUMP"] for platform in PREFIXES}
    if os.environ["PRERELEASE"] not in ("true", "false") or any(bump not in BUMPS for bump in bumps.values()):
        raise ValueError("Invalid release inputs.")
    preview = os.environ["PRERELEASE"] == "true"
    retry = int(os.environ["GITHUB_RUN_ATTEMPT"]) > 1
    releases = read_releases(repository) if any(bump != "no release" for bump in bumps.values()) else []
    tags = read_tags() if releases or any(bump != "no release" for bump in bumps.values()) else {}
    if retry:
        if not path.is_file():
            raise ValueError("The original release-plan artifact is missing. Start a new dispatch; retries never recalculate versions.")
        plan = json.loads(path.read_text())
        validate_saved_plan(plan, repository, source, run_id, preview, bumps)
    else:
        if path.exists():
            raise ValueError("Refusing to replace an existing release plan.")
        plan = create_plan(repository, source, run_id, preview, bumps, releases)
    outputs, summary = [], ["## Release plan", "", "| Platform | Choice | Stable baseline | Version | Action |",
                            "|---|---|---|---|---|"]
    for platform, item in plan["platforms"].items():
        release = check_selection(platform, item, releases, tags, source, retry)
        action = "Release" if item["version"] else "No release"
        if release:
            filename = "release.json" if platform == "windows" else "release-macos.json"
            assets = [asset for asset in release["assets"] if asset["name"] == filename]
            if len(assets) != 1 or type(assets[0].get("id")) is not int or assets[0]["id"] <= 0:
                raise ValueError("Published release is missing its unique provenance metadata asset.")
            metadata = json.loads(run("gh", "api", f"repos/{repository}/releases/assets/{assets[0]['id']}",
                                      "-H", "Accept: application/octet-stream"))
            check_published(plan, platform, release, metadata)
            action = "Already published by this run"
        outputs += [f"{platform}_release={str(action == 'Release').lower()}",
                    f"{platform}_version={item['version'] or ''}"]
        baseline = item["baseVersion"] or ("0.0.0 (no stable release)" if item["version"] else "-")
        summary.append(f"| {platform} | {item['bump']} | {baseline} | {item['version'] or '-'} | {action} |")
    if not retry:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(plan, indent=2) + "\n")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write("\n".join(outputs) + "\n")
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as output:
        output.write("\n".join(summary) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--plan", type=Path, default=Path("artifacts/release-plan/plan.json"))
    args = parser.parse_args()
    try:
        main(args.plan)
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release planning failed: {error}\n")
