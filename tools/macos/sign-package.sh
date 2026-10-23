#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
version="${1:?Supply the macOS version}"
python3 tools/macos/release.py version "$version" >/dev/null
current_stage="configuration"
stage() {
    current_stage="$1"
    printf 'macOS release: %s\n' "$current_stage"
}
trap 'printf "macOS release failed during: %s\n" "$current_stage" >&2' ERR
python3 - <<'PY'
import os
required = ("MACOS_CERTIFICATE_P12", "MACOS_CERTIFICATE_PASSWORD", "MACOS_SIGNING_IDENTITY",
            "MACOS_NOTARY_KEY", "MACOS_NOTARY_KEY_ID", "MACOS_NOTARY_ISSUER")
missing = [name for name in required if not os.environ.get(name, "").strip()]
if missing:
    raise SystemExit("Configure " + ", ".join(missing) + " in production. Unsigned releases are prohibited.")
PY
source tools/macos/sparkle.sh
python3 tools/macos/updates.py keys
app="$PWD/artifacts/macos/GHCPSpendTray.app"
bash tools/macos/test-package.sh "$app" "$version"
temporary="$(mktemp -d -t ghcp-signing)"
keychain="$temporary/signing.keychain-db"
cleanup() {
    local status=0
    if ! python3 tools/macos/keychain_search.py remove "$keychain"; then
        echo "Could not remove the temporary signing Keychain from the search list." >&2
        status=1
    fi
    if [[ -f "$keychain" ]] && ! security delete-keychain "$keychain"; then
        echo "Could not remove the temporary signing Keychain." >&2
        status=1
    fi
    rm -f "$temporary/certificate.p12" "$temporary/notary.p8" "$temporary/app.zip" "$temporary/import-error.log"
    if ! rmdir "$temporary"; then
        echo "Could not remove the temporary signing directory." >&2
        status=1
    fi
    return "$status"
}
trap cleanup EXIT
export GHCP_SIGNING_TEMP="$temporary"
stage "decode signing credentials"
python3 - <<'PY'
import base64, os
from pathlib import Path
directory = Path(os.environ["GHCP_SIGNING_TEMP"])
os.umask(0o077)
directory.joinpath("certificate.p12").write_bytes(base64.b64decode(os.environ["MACOS_CERTIFICATE_P12"], validate=True))
directory.joinpath("notary.p8").write_text(os.environ["MACOS_NOTARY_KEY"])
PY
password="$(openssl rand -hex 32)"
stage "create temporary signing Keychain"
security create-keychain -p "$password" "$keychain"
stage "add temporary signing Keychain to codesign search list"
python3 tools/macos/keychain_search.py add "$keychain"
stage "configure temporary signing Keychain"
security set-keychain-settings -lut 21600 "$keychain"
stage "unlock temporary signing Keychain"
security unlock-keychain -p "$password" "$keychain"
stage "import P12 certificate and private key"
if ! security import "$temporary/certificate.p12" -k "$keychain" -f pkcs12 \
    -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign >/dev/null 2>"$temporary/import-error.log"; then
    echo "Could not import MACOS_CERTIFICATE_P12. Re-export the Developer ID Application certificate and its private key as .p12; update the base64 P12 secret and matching export password. Do not upload a .cer file." >&2
    exit 1
fi
stage "validate selected Developer ID identity and certificate trust"
MACOS_SIGNING_IDENTITY="$(python3 tools/macos/signing_identity.py "$keychain" "$MACOS_SIGNING_IDENTITY")"
stage "authorize codesign access to the imported private key"
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
stage "sign Native AOT library"
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --options runtime --timestamp \
    "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
stage "sign Sparkle framework and helpers"
bash tools/macos/sign-sparkle.sh "$app" "$MACOS_SIGNING_IDENTITY" "$keychain"
stage "sign application"
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --options runtime --timestamp "$app"
stage "verify application signature"
codesign --verify --deep --strict "$app"
ditto -c -k --keepParent "$app" "$temporary/app.zip"
stage "submit application for notarization"
bash tools/macos/notarize.sh "$temporary/app.zip" app
stage "staple and assess notarized application"
xcrun stapler staple "$app"
bash tools/macos/test-package.sh "$app" "$version" true
# Exercise the hardened-runtime build too, before anything is published.
python3 tools/macos/smoke-test.py "$app"
stage "package and sign disk image"
mkdir -p artifacts/macos-dmg artifacts/macos-release
ditto "$app" artifacts/macos-dmg/GHCPSpendTray.app
ln -sfn /Applications artifacts/macos-dmg/Applications
dmg="$PWD/artifacts/macos-release/GHCPSpendTray-macOS-$version.dmg"
hdiutil create -ov -volname GHCPSpendTray -srcfolder artifacts/macos-dmg -format UDZO "$dmg" >/dev/null
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --timestamp "$dmg"
stage "submit disk image for notarization"
bash tools/macos/notarize.sh "$dmg" dmg
stage "staple and assess notarized disk image"
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl --assess --type open --context context:primary-signature "$dmg"
stage "sign update archive and release appcast"
python3 tools/macos/updates.py generate "$dmg" "$version"
export GHCP_MAC_VERSION="$version"
stage "write release metadata and checksums"
python3 - <<'PY'
import hashlib, json, os, sys
from pathlib import Path
sys.path.insert(0, "tools/macos")
from release import release_metadata
root = Path("artifacts/macos-release")
metadata = release_metadata(os.environ["GHCP_MAC_VERSION"], os.environ["GITHUB_SHA"], os.environ["GITHUB_RUN_ID"])
root.joinpath("release-macos.json").write_text(json.dumps(metadata, indent=2) + "\n")
assets = sorted(path for path in root.iterdir() if path.name != "SHA256SUMS")
root.joinpath("SHA256SUMS").write_text("".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in assets))
PY
