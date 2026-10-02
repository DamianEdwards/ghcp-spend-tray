#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
case "$(uname -m)" in arm64) rid=osx-arm64 ;; x86_64) rid=osx-x64 ;; *) echo "Unsupported Mac architecture" >&2; exit 1 ;; esac
python3 -m unittest discover -s tools/ci -p 'test_*.py'
python3 -m unittest discover -s tools/macos -p 'test_*.py'
for name in GHCPSpendTray.Tests GHCPSpendTray.SharedTests; do
    dotnet run --project "tests/$name" -c Release | tail -n 1
    dotnet publish "tests/$name" -c Release -r "$rid" -p:PublishAot=true \
        -p:IlcTreatWarningsAsErrors=true -o "artifacts/tests/$rid/$name" --nologo -v:q
    "artifacts/tests/$rid/$name/$name" | tail -n 1
done
bash tools/macos/build.sh
xcrun swiftc -swift-version 6 -warnings-as-errors -O \
    src/GHCPSpendTray.Mac/Models.swift src/GHCPSpendTray.Mac/Platform.swift \
    src/GHCPSpendTray.Mac/AppModel.swift src/GHCPSpendTray.Mac/TrayIconRenderer.swift \
    src/GHCPSpendTray.Mac/Notifications.swift \
    -import-objc-header src/GHCPSpendTray.Mac/Bridge.h \
    tests/GHCPSpendTray.MacTests/*.swift \
    artifacts/macos/GHCPSpendTray.app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib \
    -Xlinker -rpath -Xlinker @executable_path/GHCPSpendTray.app/Contents/Frameworks \
    -o artifacts/macos/platform-tests
artifacts/macos/platform-tests
python3 tools/macos/smoke-test.py artifacts/macos/GHCPSpendTray.app
