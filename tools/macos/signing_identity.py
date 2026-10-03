#!/usr/bin/env python3
"""Check only the imported signing identity; never export or print private-key data."""

import argparse
import re
import subprocess


def identities(output):
    return re.findall(r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"([^"\r\n]+)"', output, re.MULTILINE)


def select_identity(selector, all_identities, valid_identities):
    if re.fullmatch(r"[0-9A-Fa-f]{40}", selector):
        matches = [(fingerprint, name) for fingerprint, name in all_identities
                   if fingerprint.casefold() == selector.casefold()]
    else:
        matches = [(fingerprint, name) for fingerprint, name in all_identities if name == selector]
    # find-identity without -v contains both the matching and valid sections.
    matches = list(dict.fromkeys(matches))
    if not all_identities:
        raise ValueError(
            "The imported P12 has no code-signing identity (certificate plus matching private key). "
            "In Keychain Access > login > My Certificates, expand Developer ID Application and "
            "export the certificate with its private key as .p12. Update MACOS_CERTIFICATE_P12 "
            "with its base64 contents and MACOS_CERTIFICATE_PASSWORD with its export password."
        )
    if not matches:
        raise ValueError(
            "MACOS_SIGNING_IDENTITY does not match an identity in MACOS_CERTIFICATE_P12. "
            "Use the fingerprint or exact Developer ID Application name from the same certificate "
            "and private key that were exported, not an Apple intermediate certificate."
        )
    if len(matches) != 1:
        raise ValueError("Multiple imported identities match MACOS_SIGNING_IDENTITY. Use the certificate's unique SHA-1 fingerprint.")
    fingerprint, name = matches[0]
    if not name.startswith("Developer ID Application: "):
        raise ValueError("The selected identity is not a Developer ID Application certificate for direct macOS distribution.")
    if fingerprint.casefold() not in {value.casefold() for value, _ in valid_identities}:
        raise ValueError(
            "The selected certificate and private key were imported, but macOS does not consider "
            "them a valid code-signing identity. Check expiration/revocation and the Apple Developer ID "
            "intermediate certificate chain on the signing runner. Do not override trust with Always Trust."
        )
    return fingerprint.upper()


def check(keychain, selector):
    def read(valid_only):
        command = ["/usr/bin/security", "find-identity", "-p", "codesigning" if valid_only else "basic"]
        if valid_only:
            command.append("-v")
        result = subprocess.run([*command, keychain], capture_output=True, text=True)
        if result.returncode:
            raise ValueError("macOS could not inspect the temporary signing Keychain.")
        return identities(result.stdout)

    return select_identity(selector, read(False), read(True))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("keychain")
    parser.add_argument("selector")
    args = parser.parse_args()
    try:
        print(check(args.keychain, args.selector))
    except (OSError, ValueError) as error:
        parser.exit(1, f"Signing identity check failed: {error}\n")
