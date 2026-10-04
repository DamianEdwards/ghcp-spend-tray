#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
python3 tools/macos/architecture.py host

version="${1:-$(cat packaging/macos/version.txt)}"
channel="${2:-Development}"
python3 tools/macos/release.py version "$version" >/dev/null
case "$channel" in Development|Preview|Stable) ;; *) echo "Invalid release channel." >&2; exit 1 ;; esac
source tools/macos/sdk.sh
export MACOSX_DEPLOYMENT_TARGET=15.0
# Remove the obsolete generated Intel build output, never release assets.
rm -rf artifacts/macos/x64
app="$PWD/artifacts/macos/GHCPSpendTray.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"

output="$PWD/artifacts/macos/arm64"
dotnet publish src/GHCPSpendTray.MacBridge -c Release -r osx-arm64 \
    -p:IlcTreatWarningsAsErrors=true -o "$output" --nologo -v:q
install_name_tool -id @rpath/GHCPSpendTray.MacBridge.dylib "$output/GHCPSpendTray.MacBridge.dylib"
xcrun swiftc -swift-version 6 -warnings-as-errors -O -g \
    -target arm64-apple-macos15.0 \
    -import-objc-header src/GHCPSpendTray.Mac/Bridge.h \
    src/GHCPSpendTray.Mac/*.swift \
    "$output/GHCPSpendTray.MacBridge.dylib" \
    -Xlinker -rpath -Xlinker @executable_path/../Frameworks \
    -o "$output/GHCPSpendTray"
python3 tools/macos/architecture.py binary "$output/GHCPSpendTray" "$output/GHCPSpendTray.MacBridge.dylib"
# Replace old bundle binaries outright so a reused universal app cannot retain Intel slices.
cp "$output/GHCPSpendTray" "$app/Contents/MacOS/GHCPSpendTray"
cp "$output/GHCPSpendTray.MacBridge.dylib" "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
cp packaging/macos/Info.plist "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :GHCPReleaseChannel $channel" "$app/Contents/Info.plist"
cp LICENSE "$app/Contents/Resources/LICENSE.txt"
cp PRIVACY.md "$app/Contents/Resources/PRIVACY.md"

iconset="$PWD/artifacts/macos/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    artwork=src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png
    if [[ -f "src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-$size.png" ]]; then
        artwork="src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-$size.png"
    fi
    sips -z "$size" "$size" "$artwork" \
        --out "$iconset/icon_${size}x${size}.png" >/dev/null
    artwork=src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png
    if [[ -f "src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-$((size * 2)).png" ]]; then
        artwork="src/GHCPSpendTray.App/Assets/Square44x44Logo.targetsize-$((size * 2)).png"
    fi
    sips -z "$((size * 2))" "$((size * 2))" "$artwork" \
        --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
# Local output is ad-hoc signed only. Release automation replaces these signatures.
codesign --force --sign - "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
codesign --force --sign - "$app"
bash tools/macos/test-package.sh "$app" "$version"
echo "Built Apple-silicon-only development app: $app"
