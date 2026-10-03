#!/usr/bin/env python3
"""Fail-closed platform routing, shared by PR and main verification."""

import os
from pathlib import Path
import subprocess


def classify(paths):
    result = dict(windows=False, macos=False, markdown=False)
    for path in paths:
        if path.endswith((".md", ".markdown")):
            result["markdown"] = True
        elif path == ".markdownlint.jsonc":
            result["markdown"] = True
        elif path.startswith("docs/images/") and Path(path).suffix.lower() in {
            ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif",
        }:
            continue
        elif path in {
            "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png",
            "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.svg",
            "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo-small.svg",
            *(f"src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-{size}.png" for size in (16, 32, 64, 256)),
            "tools/assert-verification.ps1",
            ".github/workflows/release.yml",
        }:
            result.update(windows=True, macos=True)
        elif path.startswith(("src/GHCPSpendTray.Mac/", "src/GHCPSpendTray.MacBridge/", "tests/GHCPSpendTray.MacTests/", "tools/macos/", "packaging/macos/")) or path == ".github/workflows/notarization-status.yml":
            result["macos"] = True
        elif path.startswith(("src/GHCPSpendTray.App/", "tests/GHCPSpendTray.AppTests/", "tests/GHCPSpendTray.PlatformTests/")) or (
            path.startswith("tools/") and path.endswith(".ps1")
        ) or path in ("packaging/AppxManifest.xml", "packaging/priconfig.xml", ".github/workflows/store-package.yml", "GHCPSpendTray.slnx"):
            result["windows"] = True
        else:
            # Shared sources, tests, SDK, CI routing, or an unfamiliar path run both.
            result.update(windows=True, macos=True)
    return result


def git(*arguments):
    return subprocess.check_output(["git", *arguments])


if __name__ == "__main__":
    event = os.environ["EVENT_NAME"]
    if event == "workflow_dispatch":
        result = dict(windows=True, macos=True, markdown=True)
    else:
        base, head = os.environ["BASE_SHA"], os.environ["HEAD_SHA"]
        if base == "0" * 40:
            raw = git("ls-files", "-z")
        elif event == "pull_request":
            raw = git("diff", "--no-renames", "--name-only", "-z", f"{base}...{head}")
        else:
            raw = git("diff", "--no-renames", "--name-only", "-z", base, head)
        result = classify(raw.decode().strip("\0").split("\0") if raw else [])
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        for name, enabled in result.items():
            output.write(f"{name}={str(enabled).lower()}\n")
