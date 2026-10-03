#!/bin/bash
set -euo pipefail
app="${1:?Supply the app bundle}"
version="${2:?Supply the version}"
signed="${3:-false}"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")" == com.damianedwards.GHCPSpendTray ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")" == "$version" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist")" == "$version" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$app/Contents/Info.plist")" == 15.0 ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$app/Contents/Info.plist")" == true ]]
for binary in "$app/Contents/MacOS/GHCPSpendTray" "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"; do
    [[ "$(lipo -archs "$binary")" == "x86_64 arm64" ]]
    for arch in arm64 x86_64; do
        # No build-machine absolute paths or separately installed runtime may leak into the bundle.
        if otool -arch "$arch" -L "$binary" | tail -n +2 | grep -Ev '^[[:space:]]+(@rpath/|/usr/lib/|/System/Library/)' | grep -q .; then
            echo "Unexpected $arch dynamic dependency in $binary" >&2
            exit 1
        fi
        minimum="$(xcrun vtool -arch "$arch" -show-build "$binary" | awk '/minos/ { print $2 }')"
        case "$minimum" in 12.0|13.0|14.0|15.0) ;; *) echo "Unexpected $arch minimum OS: $minimum" >&2; exit 1 ;; esac
        if [[ "$binary" == "$app/Contents/MacOS/GHCPSpendTray" && "$minimum" != 15.0 ]]; then
            echo "The $arch application must target macOS 15.0, not $minimum." >&2
            exit 1
        fi
    done
done
codesign --verify --deep --strict "$app"
if [[ "$signed" == true ]]; then
    codesign --display --verbose=4 "$app" 2>&1 | grep -q 'Authority=Developer ID Application:'
    xcrun stapler validate "$app"
    spctl --assess --type execute --verbose=2 "$app"
fi
echo "PASS: universal macOS bundle, version, dependencies, and signatures."
