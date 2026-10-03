#!/usr/bin/env python3
"""Run the real app with synthetic data and a bounded process lifetime."""

import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
from architecture import require_binary, require_host


def smoke(app):
    require_host()
    bundle = Path(app).resolve()
    executable = bundle / "Contents/MacOS/GHCPSpendTray"
    require_binary(executable)
    require_binary(bundle / "Contents/Frameworks/GHCPSpendTray.MacBridge.dylib")
    for empty in (False, True):
        with tempfile.TemporaryDirectory(prefix="ghcp-mac-smoke-") as directory:
            command = ["arch", "-arm64", str(executable), "--smoke-test", "--data-dir", directory]
            if empty:
                command.append("--demo-empty")
            with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as process:
                try:
                    stdout, stderr = process.communicate(timeout=90)
                except subprocess.TimeoutExpired:
                    try:
                        sample = subprocess.run(["/usr/bin/sample", str(process.pid), "1", "1"],
                                                capture_output=True, text=True, timeout=15)
                        print("\n".join(sample.stdout.splitlines()[:160]), file=sys.stderr)
                        print(sample.stderr, file=sys.stderr)
                    except (OSError, subprocess.TimeoutExpired) as error:
                        print(f"Could not sample the timed-out smoke process: {error}", file=sys.stderr)
                    finally:
                        process.kill()
                        stdout, stderr = process.communicate()
                        print(stdout, end="")
                        print(stderr, end="", file=sys.stderr)
                    raise
            print(stdout, end="")
            print(stderr, end="", file=sys.stderr)
            subprocess.CompletedProcess(command, process.returncode, stdout, stderr).check_returncode()
            if "PASS: native notification settings callback." not in stdout:
                raise RuntimeError("The app did not exercise its native notification settings callback.")
            result = Path(directory, "smoke-result.txt").read_text()
            if not result.startswith("PASS:"):
                raise RuntimeError("The application did not complete its smoke assertions.")
            for page in ("Flyout", "FlyoutWithExample", "Usage", "Accounts", "General", "Notifications", "About"):
                if Path(directory, f"{page}.png").stat().st_size < 1000:
                    raise RuntimeError(f"{page} did not render.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("app")
    args = parser.parse_args()
    smoke(args.app)
