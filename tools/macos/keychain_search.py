#!/usr/bin/env python3
"""Add/remove one temporary signing Keychain while preserving other search entries."""

import argparse
import shlex
import subprocess


def update_search_list(operation, keychain):
    result = subprocess.run(["/usr/bin/security", "list-keychains", "-d", "user"],
                            capture_output=True, text=True)
    if result.returncode:
        raise ValueError("Could not read the user Keychain search list.")
    current = shlex.split(result.stdout)
    if operation == "add":
        if keychain in current:
            return
        updated = [keychain, *current]
    elif operation == "remove":
        updated = [entry for entry in current if entry != keychain]
    else:
        raise ValueError("Unknown Keychain search-list operation.")
    if updated == current:
        return
    result = subprocess.run(["/usr/bin/security", "list-keychains", "-d", "user", "-s", *updated],
                            capture_output=True, text=True)
    if result.returncode:
        raise ValueError(f"Could not {operation} the temporary signing Keychain in the user search list.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["add", "remove"])
    parser.add_argument("keychain")
    args = parser.parse_args()
    try:
        update_search_list(args.operation, args.keychain)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Signing Keychain search-list update failed: {error}\n")
