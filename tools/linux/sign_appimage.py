#!/usr/bin/env python3
"""Sign existing type-2 AppImages without rebuilding their verified payload."""

import argparse
import base64
from contextlib import contextmanager
import hashlib
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile

SECTIONS = (b".sha256_sig", b".sig_key")
SECRET_NAMES = ("LINUX_SIGNING_KEY_BASE64", "LINUX_SIGNING_KEY_PASSPHRASE")


def fingerprint(value):
    if not re.fullmatch(r"(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})", value):
        raise ValueError("A full signing-key fingerprint is required, not a key ID.")
    return value.upper()


def sections(image):
    size = image.stat().st_size
    with image.open("rb") as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:7] != b"\x7fELF\x02\x01\x01" or header[8:11] != b"AI\x02":
            raise ValueError("Expected a little-endian ELF64 type-2 AppImage.")
        if struct.unpack_from("<H", header, 18)[0] not in (62, 183):
            raise ValueError("Only x86-64 and ARM64 AppImages are supported.")
        table = struct.unpack_from("<Q", header, 40)[0]
        entry_size, count, names_index = struct.unpack_from("<HHH", header, 58)
        if entry_size != 64 or not 0 < names_index < count or table < 64 or table + count * 64 > size:
            raise ValueError("Invalid or unsupported ELF section table.")
        stream.seek(table)
        entries = [struct.unpack("<IIQQQQIIQQ", stream.read(64)) for _ in range(count)]
        names = entries[names_index]
        if names[1] != 3 or not 0 < names[5] <= 1024 * 1024 or names[4] + names[5] > size:
            raise ValueError("Invalid ELF section-name table.")
        stream.seek(names[4])
        strings = stream.read(names[5])
        result = {}
        for entry in entries:
            if entry[0] >= len(strings):
                raise ValueError("Invalid ELF section name.")
            name = strings[entry[0]:].split(b"\0", 1)[0]
            if name in SECTIONS:
                offset, length = entry[4:6]
                if name in result or entry[1] != 1 or offset < 64 or not 0 < length <= 65536 or offset + length > size:
                    raise ValueError("Invalid AppImage signature section.")
                result[name] = (offset, length)
        if set(result) != set(SECTIONS):
            raise ValueError("AppImage signature sections are missing.")
        ranges = sorted([*result.values(), (table, count * 64), (names[4], names[5])])
        if any(start + length > following for (start, length), (following, _) in zip(ranges, ranges[1:])):
            raise ValueError("AppImage signature sections overlap ELF metadata.")
        return result


def canonical_digest(image, regions):
    # AppImage signs the lowercase SHA-256 hex string, with only these two
    # reserved sections zeroed. The MD5 section and the entire payload remain covered.
    digest = hashlib.sha256()
    with image.open("rb") as stream:
        position = 0
        while block := stream.read(1024 * 1024):
            block = bytearray(block)
            for offset, length in regions.values():
                start, end = max(position, offset), min(position + len(block), offset + length)
                if start < end:
                    block[start - position:end - position] = bytes(end - start)
            digest.update(block)
            position += len(block)
    return digest.hexdigest().encode("ascii")


def gpg(home, arguments, data=None):
    environment = {key: value for key, value in os.environ.items() if key not in SECRET_NAMES}
    environment["GNUPGHOME"] = str(home)
    result = subprocess.run(["gpg", "--homedir", str(home), "--batch", "--no-tty", "--no-auto-key-retrieve",
                             *arguments], input=data, capture_output=True, env=environment, timeout=120)
    if result.returncode:
        raise RuntimeError("GPG operation failed; check the configured key, passphrase, expiry and signature.")
    return result.stdout


@contextmanager
def keyring():
    with tempfile.TemporaryDirectory(prefix="ghcp-sign-") as directory:
        home = Path(directory)
        home.chmod(0o700)
        try:
            yield home
        finally:
            environment = {key: value for key, value in os.environ.items() if key not in SECRET_NAMES}
            subprocess.run(["gpgconf", "--homedir", str(home), "--kill", "gpg-agent"],
                           check=True, capture_output=True, env=environment, timeout=15)


def read_section(image, region):
    with image.open("rb") as stream:
        stream.seek(region[0])
        return stream.read(region[1])


def verify(image, expected):
    expected = fingerprint(expected)
    regions = sections(image)
    signature = read_section(image, regions[SECTIONS[0]]).rstrip(b"\0")
    public_key = read_section(image, regions[SECTIONS[1]]).rstrip(b"\0")
    if not signature.startswith(b"-----BEGIN PGP SIGNATURE-----") or not public_key.startswith(b"-----BEGIN PGP PUBLIC KEY BLOCK-----"):
        raise ValueError("The AppImage has no embedded armored signature and public key.")
    with keyring() as home:
        gpg(home, ["--import"], public_key)
        digest = home / "digest"
        signature_file = home / "signature.asc"
        digest.write_bytes(canonical_digest(image, regions))
        signature_file.write_bytes(signature)
        status = gpg(home, ["--status-fd", "1", "--verify", str(signature_file), str(digest)]).decode("utf-8")
        valid = [line.split()[2] for line in status.splitlines() if line.startswith("[GNUPG:] VALIDSIG ")]
        if valid != [expected] or any(f"[GNUPG:] {failure}" in status for failure in
                                     ("EXPKEYSIG", "EXPSIG", "REVKEYSIG", "BADSIG", "ERRSIG")):
            raise ValueError("Signature does not match the required, valid signing-key fingerprint.")
    return regions


def sign(image, output, expected, private_key, passphrase):
    expected = fingerprint(expected)
    if not private_key or not passphrase or "\n" in passphrase or "\r" in passphrase:
        raise ValueError("A signing key and nonempty, single-line passphrase are required.")
    if output.exists():
        raise ValueError("The signing output directory must not already exist.")
    regions = sections(image)
    if any(any(read_section(image, region)) for region in regions.values()):
        raise ValueError("Refusing to replace an existing AppImage signature.")
    with image.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    if image.with_name(image.name + ".sha256").read_text() != f"{checksum}  {image.name}\n":
        raise ValueError("Input AppImage does not match its build checksum.")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".sign-stage-", dir=output.parent) as directory, keyring() as home:
        stage = Path(directory)
        signed = stage / image.name
        shutil.copyfile(image, signed)
        signed.chmod(0o755)
        with signed.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != checksum:
                raise ValueError("The unsigned AppImage changed while preparing signing.")
        unsigned_digest = canonical_digest(signed, regions)
        gpg(home, ["--import"], private_key)
        public_key = gpg(home, ["--armor", "--export", expected])
        digest = home / "digest"
        digest.write_bytes(unsigned_digest)
        signature = gpg(home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0", "--local-user", expected + "!",
                               "--armor", "--textmode", "--detach-sign", "--output", "-", str(digest)],
                        (passphrase + "\n").encode())
        with signed.open("r+b") as stream:
            for name, value in zip(SECTIONS, (signature, public_key)):
                offset, length = regions[name]
                if not value or len(value) > length:
                    raise ValueError("Signing key or signature does not fit the AppImage's reserved section.")
                stream.seek(offset)
                stream.write(value.ljust(length, b"\0"))
        verify(signed, expected)
        if canonical_digest(signed, regions) != unsigned_digest:
            raise ValueError("Signing unexpectedly changed the verified runtime or application payload.")
        detached = gpg(home, ["--pinentry-mode", "loopback", "--passphrase-fd", "0", "--local-user", expected + "!",
                              "--armor", "--detach-sign", "--output", "-", str(signed)],
                       (passphrase + "\n").encode())
        detached_file = stage / (signed.name + ".asc")
        detached_file.write_bytes(detached)
        gpg(home, ["--verify", str(detached_file), str(signed)])
        with signed.open("rb") as stream:
            checksum = hashlib.file_digest(stream, "sha256").hexdigest()
        (stage / (signed.name + ".sha256")).write_text(f"{checksum}  {signed.name}\n")
        (stage / "signing-key.asc").write_bytes(public_key)
        (stage / "SIGNING.txt").write_text(
            f"Embedded and detached OpenPGP signer: {expected}\n"
            f"Source commit: {os.environ.get('GITHUB_SHA', 'local')}\n"
            "Development build. Confirm the fingerprint through a trusted channel; do not trust a bundled key alone.\n")
        stage.rename(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("sign", "verify"))
    parser.add_argument("image", type=Path)
    parser.add_argument("--fingerprint", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.operation == "verify":
        verify(args.image, args.fingerprint)
    else:
        if args.output is None:
            parser.error("sign requires --output")
        encoded = os.environ.pop(SECRET_NAMES[0], "")
        passphrase = os.environ.pop(SECRET_NAMES[1], "")
        if not encoded:
            raise ValueError("Protected Linux signing credentials are not configured.")
        sign(args.image, args.output, args.fingerprint, base64.b64decode(encoded, validate=True), passphrase)
    print("Verified AppImage signature for " + fingerprint(args.fingerprint))


if __name__ == "__main__":
    main()
