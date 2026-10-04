#!/bin/bash
set -euo pipefail
framework="${1:?Supply the app bundle}/Contents/Frameworks/Sparkle.framework"
version="$framework/Versions/B"
identity="${2:?Supply the signing identity}"
options=(--force --sign "$identity")
if [[ "$identity" != - ]]; then
    options+=(--keychain "${3:?Supply the signing Keychain}" --options runtime --timestamp)
fi
for component in XPCServices/Downloader.xpc XPCServices/Installer.xpc Autoupdate Updater.app; do
    codesign "${options[@]}" "$version/$component"
done
codesign "${options[@]}" "$framework"
