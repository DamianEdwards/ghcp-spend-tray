#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."

version="${1:-$(cat packaging/macos/version.txt)}"
channel="${2:-Development}"
python3 tools/macos/release.py version "$version" >/dev/null
case "$channel" in Development|Preview|Stable) ;; *) echo "Invalid release channel." >&2; exit 1 ;; esac
if [[ -z "${SDKROOT:-}" ]]; then
    sdk_version="$(xcrun --show-sdk-version)"
    # Some preview Command Line Tools omit the SwiftUI macro plugin. Prefer the
    # installed stable SDK in that case, without changing the machine's selection.
    if [[ "${sdk_version%%.*}" -ge 27 && "$(xcode-select -p)" == */CommandLineTools ]]; then
        export SDKROOT
        SDKROOT="$(xcode-select -p)/SDKs/MacOSX26.sdk"
        if [[ ! -d "$SDKROOT" ]]; then
            echo "Select a stable Xcode toolchain or set SDKROOT to a stable macOS SDK." >&2
            exit 1
        fi
        echo "Using stable macOS SDK: $SDKROOT"
    fi
fi
export MACOSX_DEPLOYMENT_TARGET=14.0
app="$PWD/artifacts/macos/GHCPSpendTray.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"

for arch in arm64 x64; do
    native_arch="$arch"
    if [[ "$arch" == x64 ]]; then native_arch=x86_64; fi
    output="$PWD/artifacts/macos/$arch"
    dotnet publish src/GHCPSpendTray.MacBridge -c Release -r "osx-$arch" \
        -p:IlcTreatWarningsAsErrors=true -o "$output" --nologo -v:q
    install_name_tool -id @rpath/GHCPSpendTray.MacBridge.dylib "$output/GHCPSpendTray.MacBridge.dylib"
    xcrun swiftc -swift-version 6 -warnings-as-errors -O -g \
        -target "$native_arch-apple-macos14.0" \
        -import-objc-header src/GHCPSpendTray.Mac/Bridge.h \
        src/GHCPSpendTray.Mac/*.swift \
        "$output/GHCPSpendTray.MacBridge.dylib" \
        -Xlinker -rpath -Xlinker @executable_path/../Frameworks \
        -o "$output/GHCPSpendTray"
done

lipo -create artifacts/macos/arm64/GHCPSpendTray artifacts/macos/x64/GHCPSpendTray -output "$app/Contents/MacOS/GHCPSpendTray"
lipo -create artifacts/macos/arm64/GHCPSpendTray.MacBridge.dylib artifacts/macos/x64/GHCPSpendTray.MacBridge.dylib \
    -output "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
cp packaging/macos/Info.plist "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :GHCPReleaseChannel $channel" "$app/Contents/Info.plist"
cp LICENSE "$app/Contents/Resources/LICENSE.txt"
cp PRIVACY.md "$app/Contents/Resources/PRIVACY.md"

iconset="$PWD/artifacts/macos/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png \
        --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.png \
        --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
# Local output is ad-hoc signed only. Release automation replaces these signatures.
codesign --force --sign - "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"
codesign --force --sign - "$app"
bash tools/macos/test-package.sh "$app" "$version"
echo "Built universal development app: $app"
