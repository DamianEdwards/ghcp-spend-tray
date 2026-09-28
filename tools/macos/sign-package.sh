#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
version="${1:?Supply the macOS version}"
python3 tools/macos/release.py version "$version" >/dev/null
python3 - <<'PY'
import os
required = ("MACOS_CERTIFICATE_P12", "MACOS_CERTIFICATE_PASSWORD", "MACOS_SIGNING_IDENTITY",
            "MACOS_NOTARY_KEY", "MACOS_NOTARY_KEY_ID", "MACOS_NOTARY_ISSUER")
missing = [name for name in required if not os.environ.get(name, "").strip()]
if missing:
    raise SystemExit("Configure " + ", ".join(missing) + " in production-macos. Unsigned releases are prohibited.")
PY
app="$PWD/artifacts/macos/GHCPSpendTray.app"
bash tools/macos/test-package.sh "$app" "$version"
temporary="$(mktemp -d -t ghcp-signing)"
keychain="$temporary/signing.keychain-db"
cleanup() {
    local status=0
    if [[ -f "$keychain" ]] && ! security delete-keychain "$keychain"; then
        echo "Could not remove the temporary signing Keychain." >&2
        status=1
    fi
    rm -f "$temporary/certificate.p12" "$temporary/notary.p8" "$temporary/app.zip"
    if ! rmdir "$temporary"; then
        echo "Could not remove the temporary signing directory." >&2
        status=1
    fi
    return "$status"
}
trap cleanup EXIT
export GHCP_SIGNING_TEMP="$temporary"
python3 - <<'PY'
import base64, os
from pathlib import Path
directory = Path(os.environ["GHCP_SIGNING_TEMP"])
os.umask(0o077)
directory.joinpath("certificate.p12").write_bytes(base64.b64decode(os.environ["MACOS_CERTIFICATE_P12"], validate=True))
directory.joinpath("notary.p8").write_text(os.environ["MACOS_NOTARY_KEY"])
PY
password="$(openssl rand -hex 32)"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$temporary/certificate.p12" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --options runtime --timestamp \
    "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --options runtime --timestamp "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --keepParent "$app" "$temporary/app.zip"
xcrun notarytool submit "$temporary/app.zip" --key "$temporary/notary.p8" \
    --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER" --wait --timeout 20m
xcrun stapler staple "$app"
bash tools/macos/test-package.sh "$app" "$version" true
# Exercise the hardened-runtime build too, before anything is published.
python3 tools/macos/smoke-test.py "$app"
mkdir -p artifacts/macos-dmg artifacts/macos-release
ditto "$app" artifacts/macos-dmg/GHCPSpendTray.app
ln -sfn /Applications artifacts/macos-dmg/Applications
dmg="$PWD/artifacts/macos-release/GHCPSpendTray-macOS-$version.dmg"
hdiutil create -ov -volname GHCPSpendTray -srcfolder artifacts/macos-dmg -format UDZO "$dmg" >/dev/null
codesign --force --sign "$MACOS_SIGNING_IDENTITY" --keychain "$keychain" --timestamp "$dmg"
xcrun notarytool submit "$dmg" --key "$temporary/notary.p8" \
    --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER" --wait --timeout 20m
xcrun stapler staple "$dmg"
xcrun stapler validate "$dmg"
spctl --assess --type open --context context:primary-signature "$dmg"
export GHCP_MAC_VERSION="$version"
python3 - <<'PY'
import hashlib, json, os
from pathlib import Path
root = Path("artifacts/macos-release")
metadata = dict(platform="macos", version=os.environ["GHCP_MAC_VERSION"],
                sourceCommit=os.environ["GITHUB_SHA"], signed=True, notarized=True,
                architectures=["arm64", "x86_64"], minimumOS="14.0",
                bundleIdentifier="com.damianedwards.GHCPSpendTray")
root.joinpath("release-macos.json").write_text(json.dumps(metadata, indent=2) + "\n")
assets = sorted(path for path in root.iterdir() if path.name != "SHA256SUMS")
root.joinpath("SHA256SUMS").write_text("".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in assets))
PY
