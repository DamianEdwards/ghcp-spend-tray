#!/bin/bash
set -euo pipefail
app="${1:?Supply the app bundle}"
version="${2:?Supply the version}"
signed="${3:-false}"
python3 "$(dirname "$0")/architecture.py" host
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")" == com.damianedwards.GHCPSpendTray ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")" == "$version" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist")" == "$version" ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$app/Contents/Info.plist")" == 15.0 ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$app/Contents/Info.plist")" == true ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSArchitecturePriority:0' "$app/Contents/Info.plist")" == arm64 ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSRequiresNativeExecution' "$app/Contents/Info.plist")" == true ]]
python3 "$(dirname "$0")/updates.py" inspect "$app"
for binary in "$app/Contents/MacOS/GHCPSpendTray" "$app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib"; do
    python3 "$(dirname "$0")/architecture.py" binary "$binary"
    # No build-machine absolute paths or separately installed runtime may leak into the bundle.
    dependencies="$(otool -arch arm64 -L "$binary")"
    if printf '%s\n' "$dependencies" | tail -n +2 | grep -Ev '^[[:space:]]+(@rpath/|/usr/lib/|/System/Library/)' | grep -q .; then
        echo "Unexpected arm64 dynamic dependency in $binary" >&2
        exit 1
    fi
    minimum="$(xcrun vtool -arch arm64 -show-build "$binary" | awk '/minos/ { print $2 }')"
    case "$minimum" in 12.0|13.0|14.0|15.0) ;; *) echo "Unexpected arm64 minimum OS: $minimum" >&2; exit 1 ;; esac
    if [[ "$binary" == "$app/Contents/MacOS/GHCPSpendTray" && "$minimum" != 15.0 ]]; then
        echo "The arm64 application must target macOS 15.0, not $minimum." >&2
        exit 1
    fi
done
if otool -L "$app/Contents/MacOS/GHCPSpendTray" | grep -q '@rpath/Sparkle.framework' &&
    [[ ! -d "$app/Contents/Frameworks/Sparkle.framework" ]]; then
    echo "The linked Sparkle framework is missing." >&2
    exit 1
fi
if [[ -d "$app/Contents/Frameworks/Sparkle.framework" ]]; then
    while IFS= read -r binary; do
        if file "$binary" | grep -q 'Mach-O'; then
            python3 "$(dirname "$0")/architecture.py" binary "$binary"
            if otool -arch arm64 -L "$binary" | tail -n +2 |
                grep -Ev '^[[:space:]]+(@rpath/|@loader_path/|@executable_path/|/usr/lib/|/System/Library/)' | grep -q .; then
                echo "Unexpected Sparkle dynamic dependency in $binary" >&2
                exit 1
            fi
        fi
    done < <(find "$app/Contents/Frameworks/Sparkle.framework" -type f)
fi
codesign --verify --deep --strict "$app"
if [[ "$signed" == true ]]; then
    codesign --display --verbose=4 "$app" 2>&1 | grep -q 'Authority=Developer ID Application:'
    xcrun stapler validate "$app"
    spctl --assess --type execute --verbose=2 "$app"
fi
echo "PASS: Apple-silicon-only macOS bundle, version, dependencies, and signatures."
