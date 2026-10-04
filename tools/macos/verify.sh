#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
source tools/macos/sdk.sh
python3 tools/macos/architecture.py host
rid=osx-arm64
python3 -m unittest discover -s tools/ci -p 'test_*.py'
python3 -m unittest discover -s tools/release -p 'test_*.py'
python3 -m unittest discover -s tools/macos -p 'test_*.py'
for name in GHCPSpendTray.Tests GHCPSpendTray.SharedTests; do
    dotnet run --project "tests/$name" -c Release | tail -n 1
    dotnet publish "tests/$name" -c Release -r "$rid" -p:PublishAot=true \
        -p:IlcTreatWarningsAsErrors=true -o "artifacts/tests/$rid/$name" --nologo -v:q
    "artifacts/tests/$rid/$name/$name" | tail -n 1
done
bash tools/macos/build.sh
source tools/macos/sparkle.sh
xcrun swiftc -swift-version 6 -warnings-as-errors -O \
    src/GHCPSpendTray.Mac/Models.swift src/GHCPSpendTray.Mac/Platform.swift \
    src/GHCPSpendTray.Mac/AppModel.swift src/GHCPSpendTray.Mac/TrayIconRenderer.swift \
    src/GHCPSpendTray.Mac/Notifications.swift src/GHCPSpendTray.Mac/PopupSmokeReadiness.swift \
    src/GHCPSpendTray.Mac/Updates.swift src/GHCPSpendTray.Mac/UpdateViews.swift \
    -F "$SPARKLE_ROOT" -framework Sparkle \
    src/GHCPSpendTray.Mac/Views.swift \
    -import-objc-header src/GHCPSpendTray.Mac/Bridge.h \
    tests/GHCPSpendTray.MacTests/*.swift \
    artifacts/macos/GHCPSpendTray.app/Contents/Frameworks/GHCPSpendTray.MacBridge.dylib \
    -Xlinker -rpath -Xlinker @executable_path/GHCPSpendTray.app/Contents/Frameworks \
    -o artifacts/macos/platform-tests
mkdir -p artifacts/macos-test-diagnostics
artifacts/macos/platform-tests 2>&1 | tee artifacts/macos-test-diagnostics/platform-tests.log
python3 tools/macos/smoke-test.py artifacts/macos/GHCPSpendTray.app
