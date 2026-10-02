#!/usr/bin/env python3
"""Run the real app with synthetic data and a bounded process lifetime."""

import argparse
from pathlib import Path
import subprocess
import tempfile


def smoke(app, architecture=None):
    executable = Path(app).resolve() / "Contents/MacOS/GHCPSpendTray"
    for empty in (False, True):
        with tempfile.TemporaryDirectory(prefix="ghcp-mac-smoke-") as directory:
            command = [str(executable), "--smoke-test", "--data-dir", directory]
            if empty:
                command.append("--demo-empty")
            if architecture:
                command = ["arch", f"-{architecture}", *command]
            subprocess.run(command, check=True, timeout=90)
            result = Path(directory, "smoke-result.txt").read_text()
            if not result.startswith("PASS:"):
                raise RuntimeError("The application did not complete its smoke assertions.")
            for page in ("Usage", "Accounts", "General", "Notifications", "About"):
                if Path(directory, f"{page}.png").stat().st_size < 1000:
                    raise RuntimeError(f"{page} did not render.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("app")
    parser.add_argument("--arch", choices=["arm64", "x86_64"])
    args = parser.parse_args()
    smoke(args.app, args.arch)
