# Build and contribute

GHCPSpendTray shares C# domain and application logic compiled with .NET 10
Native AOT. Windows uses WinUI 3 and
[Microsoft UI Reactor](https://microsoft.github.io/microsoft-ui-reactor/main/);
macOS uses native SwiftUI and AppKit.

## Windows

Install the SDK pinned in [`global.json`](../global.json), Visual Studio C++
build tools, the Windows SDK, and ARM64 native tools. From PowerShell:

```powershell
git clone https://github.com/DamianEdwards/ghcp-spend-tray.git
Set-Location ghcp-spend-tray
.\tools\verify.ps1
.\tools\package.ps1
```

Verification checks release tooling, builds Release, and runs all four .NET
test harnesses. Add `-NativeTests` to execute x64 Native AOT tests too.
Run build and publish commands sequentially: they share intermediates.
Local packaging produces an **unsigned development bundle**, not a signed
public installer. See [releasing](RELEASING.md) for production packaging.

CI splits verification with `-TestShard Application` and
`-TestShard CorePlatformShared`; together they retain the full solution build
and all four harnesses. Store and self-contained packaging independently
publish both architectures and exercise populated/empty packaged x64 startup.

## macOS

Use an Apple-silicon Mac on a supported macOS version with native arm64 tools,
not Rosetta. Install the pinned .NET SDK, Python 3, and stable Xcode Command
Line Tools with Swift 6. No .NET macOS workload, MAUI, or Xcode project is needed.

```bash
bash tools/macos/verify.sh
open artifacts/macos/GHCPSpendTray.app
```

Verification runs managed and native shared tests, builds an arm64-only app
and Native AOT bridge, rejects Intel/universal artifacts, exercises synthetic
Keychain items, and launches populated/empty UI smoke scenarios. The local
app is ad-hoc signed for development only.

`bash tools/macos/build.sh` builds without tests. Select a stable SDK with
`SDKROOT` if using a preview toolchain; see [releasing](RELEASING.md).
CI separates `verify.sh --shared-tests` and `verify.sh --app` on macOS 26 /
Apple silicon, then tests that same app artifact on macOS 15 without rebuilding.

## Isolated synthetic demos

Never use real accounts, tokens, or consumption data in tests or screenshots.
Demo modes use a separate data directory and disable real authentication,
notifications, and launch-at-login changes. Example accounts and preference
changes remain in memory.

After publishing Windows x64:

```powershell
.\artifacts\publish\win-x64\GHCPSpendTray.exe --demo --portable --data-dir C:\Temp\ghcp-demo
```

On Mac, alongside an installed release:

```bash
preview_dir="$(mktemp -d -t ghcp-preview)"
open -n artifacts/macos/GHCPSpendTray.app --args --demo --data-dir "$preview_dir"
```

Replace `--demo` with one of these modes:

| Mode | Scenario |
|---|---|
| `--demo-empty` | No accounts. On Mac, **Add Example Account** adds in-memory examples to review popup resizing. |
| `--demo-unlimited` | One unlimited account with an infinity indicator. |
| `--demo-usage-states` | Eight labeled examples: under/near/over forecasts, near/over actual usage, estimate off, estimate unavailable, and unlimited. Forecast amounts are fixed synthetic illustrations, not live projections. |

For smoke checks, packaged startup coverage, and limitations, see
[validation](VALIDATION.md). Per-change results belong in PR descriptions
and CI artifacts, not a running journal in that document.

## Source map

| Path | Responsibility |
|---|---|
| `src/GHCPSpendTray.Core` | Domain, HTTP, and storage code. |
| `src/GHCPSpendTray.Application` | Shared controller, platform interfaces, and view models; no OS dependencies. |
| `src/GHCPSpendTray.App` | Windows UI/platform services; Shell interop in `Native`. |
| `src/GHCPSpendTray.MacBridge` | Source-generated JSON C ABI for the Native AOT bridge. |
| `src/GHCPSpendTray.Mac` | SwiftUI/AppKit and native Mac services. |
| `tests` | Executable test harnesses. |
| `tools` | Build, packaging, smoke, and release checks. |

Build platforms sequentially in a shared checkout. Preserve Native AOT
compatibility and the component-only Windows App SDK package graph.
Windows and Mac have independent versions and release tags; see
[release setup and retry rules](RELEASING.md).

[Back to the user guide](../README.md)
