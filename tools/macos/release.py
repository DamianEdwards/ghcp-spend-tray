#!/usr/bin/env python3
"""Platform-scoped release validation; no external Python dependencies."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile


def version(value):
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise ValueError("Use a three-part numeric macOS version.")
    parts = tuple(map(int, value.split(".")))
    if parts == (0, 0, 0) or parts[0] > 9999 or any(part > 99 for part in parts[1:]):
        raise ValueError("macOS bundle versions require major <= 9999 and minor/patch <= 99, and must exceed zero.")
    return parts


def release_metadata(value, source, run_id):
    version(value)
    return dict(platform="macos", version=value, sourceCommit=source, releaseRunId=run_id,
                signed=True, notarized=True, architectures=["arm64"], minimumOS="15.0",
                bundleIdentifier="com.damianedwards.GHCPSpendTray")


def check_metadata(metadata, value, source):
    expected = release_metadata(value, source, os.environ["GITHUB_RUN_ID"])
    if metadata != expected:
        raise ValueError("Release metadata must describe the current signed/notarized Apple-silicon-only build.")


def run(*arguments):
    return subprocess.check_output(arguments, text=True).strip()


def gh_json(endpoint):
    return json.loads(run("gh", "api", endpoint))


def pages(endpoint, field=None):
    page = 1
    while True:
        result = gh_json(f"{endpoint}{'&' if '?' in endpoint else '?'}per_page=100&page={page}")
        batch = result[field] if field else result
        yield from batch
        if len(batch) < 100:
            break
        page += 1


def check_tags(value, tags, source):
    candidate = version(value)
    for tag, commit in tags.items():
        match = re.fullmatch(r"macos-v(\d+\.\d+\.\d+)", tag)
        if not match:
            continue
        if tag == f"macos-v{value}":
            if commit != source:
                raise ValueError("The existing macOS tag has a different source commit.")
        elif version(match[1]) >= candidate:
            raise ValueError(f"The macOS version must exceed {tag}.")


def check_source(repository, source):
    runs = pages(f"repos/{repository}/actions/workflows/verify.yml/runs?head_sha={source}&branch=main", "workflow_runs")
    eligible = [item for item in runs if item["head_sha"] == source and item["head_branch"] == "main"
                and item["event"] in ("push", "workflow_dispatch")]
    latest = max(eligible, key=lambda item: item["id"], default=None)
    if not latest or latest["status"] != "completed" or latest["conclusion"] != "success":
        raise ValueError("The latest Verify run for this exact main commit must succeed.")
    jobs = pages(f"repos/{repository}/actions/runs/{latest['id']}/attempts/{latest['run_attempt']}/jobs", "jobs")
    gates = [job for job in jobs if job["name"] == "Verification" and job["head_sha"] == source]
    if len(gates) != 1 or gates[0]["conclusion"] != "success":
        raise ValueError("The pinned source must pass the Verification gate.")


def existing_release(repository, tag):
    return next((item for item in pages(f"repos/{repository}/releases") if item["tag_name"] == tag), None)


def ensure_draft(repository, tag):
    release = existing_release(repository, tag)
    if release and not release["draft"]:
        raise ValueError("Published releases are immutable; refusing to overwrite.")
    return release


def validate(value):
    version(value)
    if os.environ["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("macOS releases must be dispatched from main.")
    repository, source = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"]
    check_source(repository, source)
    tags = {tag: run("git", "rev-list", "-n", "1", tag) for tag in run("git", "tag", "--list", "macos-v*").splitlines()}
    check_tags(value, tags, source)
    ensure_draft(repository, f"macos-v{value}")


def stage(value):
    validate(value)
    repository, source = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"]
    check_metadata(json.loads(Path("artifacts/macos-release/release-macos.json").read_text()), value, source)
    tag = f"macos-v{value}"
    remote = run("git", "ls-remote", "--tags", "origin", f"refs/tags/{tag}")
    if not remote:
        if not run("git", "tag", "--list", tag):
            run("git", "tag", tag, source)
        run("git", "push", "origin", f"refs/tags/{tag}")
    elif remote.split()[0] != source:
        raise ValueError("The release tag changed during the build.")
    if not ensure_draft(repository, tag):
        arguments = ["gh", "release", "create", tag, "--verify-tag", "--draft", "--generate-notes",
                     "--title", f"GHCPSpendTray for macOS {value}", "--latest=false"]
        previous = [t for t in run("git", "tag", "--list", "macos-v*").splitlines() if t != tag]
        if previous:
            arguments += ["--notes-start-tag", max(previous, key=lambda t: version(t.removeprefix("macos-v")))]
        if os.environ["PRERELEASE"] == "true":
            arguments.append("--prerelease")
        run(*arguments)
    assets = sorted(Path("artifacts/macos-release").iterdir())
    expected = {f"GHCPSpendTray-macOS-{value}.dmg", "SHA256SUMS", "release-macos.json"}
    if {asset.name for asset in assets} != expected:
        raise ValueError("Unexpected macOS release assets.")
    run("gh", "release", "upload", tag, *(str(asset) for asset in assets), "--clobber")
    with tempfile.TemporaryDirectory(prefix="ghcp-release-") as directory:
        run("gh", "release", "download", tag, "--dir", directory)
        if {path.name for path in Path(directory).iterdir()} != expected:
            raise ValueError("The draft has unexpected assets; inspect it before retrying.")
        for asset in assets:
            downloaded = Path(directory, asset.name)
            if hashlib.sha256(asset.read_bytes()).digest() != hashlib.sha256(downloaded.read_bytes()).digest():
                raise ValueError(f"Downloaded bytes differ: {asset.name}")
        dmg = str(Path(directory, f"GHCPSpendTray-macOS-{value}.dmg"))
        run("xcrun", "stapler", "validate", dmg)
        run("spctl", "--assess", "--type", "open", "--context", "context:primary-signature", dmg)
        run("gh", "attestation", "verify", dmg, "--repo", repository)


def publish(value):
    repository = os.environ["GITHUB_REPOSITORY"]
    tag = f"macos-v{value}"
    if not ensure_draft(repository, tag):
        raise ValueError("The verified draft does not exist.")
    run("gh", "release", "edit", tag, "--draft=false", "--latest=false",
        f"--prerelease={os.environ['PRERELEASE']}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["version", "validate", "stage", "publish"])
    parser.add_argument("value")
    args = parser.parse_args()
    try:
        version(args.value)
        if args.operation == "version":
            print(f"macos-v{args.value}")
        else:
            globals()[args.operation](args.value)
    except ValueError as error:
        parser.error(str(error))
