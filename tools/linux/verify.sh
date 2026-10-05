#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
export GIO_USE_VFS=local
for tool in dotnet dbus-run-session gdbus python3 gjs glib-compile-schemas gsettings node; do
    command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) echo "Unsupported Linux architecture." >&2; exit 1 ;;
esac
python3 -m unittest discover -s tools/ci
python3 -m unittest discover -s tools/linux
python3 tools/linux/test-qml.py
for source in src/GHCPSpendTray.Linux.Gnome/*.js; do
    node --input-type=module --check < "$source"
done
glib-compile-schemas --strict --dry-run src/GHCPSpendTray.Linux.Gnome/schemas
dotnet run --project tests/GHCPSpendTray.Tests -c Release
dotnet run --project tests/GHCPSpendTray.SharedTests -c Release
dbus-run-session --config-file=tools/linux/session-bus.conf -- dotnet run --project tests/GHCPSpendTray.LinuxTests -c Release
dotnet publish tests/GHCPSpendTray.LinuxTests -c Release -r "$rid" \
    -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true -o artifacts/linux/native-tests --nologo
dbus-run-session --config-file=tools/linux/session-bus.conf -- artifacts/linux/native-tests/GHCPSpendTray.LinuxTests
python3 tools/linux/package-native.py
python3 tools/linux/smoke-test.py "artifacts/linux/GHCPSpendTray-linux-demo-$(uname -m).AppImage"
