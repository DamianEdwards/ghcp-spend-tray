#!/usr/bin/env python3
"""Fail-closed Apple-silicon host and distribution architecture checks."""

import argparse
import platform
import subprocess


def require_host():
    system, machine = platform.system(), platform.machine()
    if system != "Darwin" or machine != "arm64":
        raise ValueError(f"macOS builds and runtime checks require native Apple silicon (arm64), not {system}/{machine}.")


def require_binary(path):
    architectures = subprocess.check_output(["lipo", "-archs", str(path)], text=True).split()
    if architectures != ["arm64"]:
        raise ValueError(f"{path} must contain exactly arm64; found {' '.join(architectures) or 'no slices'}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["host", "binary"])
    parser.add_argument("paths", nargs="*")
    args = parser.parse_args()
    if (args.operation == "host" and args.paths) or (args.operation == "binary" and not args.paths):
        parser.error("Use host without paths, or binary with at least one path.")
    try:
        if args.operation == "host":
            require_host()
        else:
            for path in args.paths:
                require_binary(path)
    except ValueError as error:
        parser.error(str(error))
