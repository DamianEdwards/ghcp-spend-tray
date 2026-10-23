#!/bin/bash
set -euo pipefail
framework="${1:?Supply the app bundle}/Contents/Frameworks/Sparkle.framework"
version="$framework/Versions/B"
identity="${2:?Supply the signing identity}"
options=(--force --sign "$identity")
if [[ "$identity" != - ]]; then
    options+=(--options runtime --timestamp)
    if [[ -n "${3:-}" ]]; then options+=(--keychain "$3"); fi
fi
for component in XPCServices/Downloader.xpc XPCServices/Installer.xpc Autoupdate Updater.app; do
    codesign "${options[@]}" "$version/$component"
done
codesign "${options[@]}" "$framework"
