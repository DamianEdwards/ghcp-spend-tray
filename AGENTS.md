# Working in this repository

GHCPSpendTray has Windows 11 (WinUI 3 / Microsoft UI Reactor) and macOS
(SwiftUI / AppKit) frontends sharing .NET 10 Native AOT logic.
Use the SDK pinned in `global.json`; Windows builds require
the Windows SDK and Visual Studio C++ build tools (including ARM64 tools for
packaging).

- `src\GHCPSpendTray.Core` contains testable domain, HTTP, and storage code.
- `src\GHCPSpendTray.Application` contains the shared application controller,
  platform interfaces, and view models; do not introduce OS dependencies there.
- `src\GHCPSpendTray.App` contains the UI and Windows integration. Keep native
  Shell interop in `Native` and Windows services in `Platform`.
- `src/GHCPSpendTray.MacBridge` exposes the shared controller through an
  in-process, source-generated JSON C ABI. `src/GHCPSpendTray.Mac` contains
  native SwiftUI/AppKit, Keychain, notifications, and login-item services.
- `src/GHCPSpendTray.Linux` is a headless .NET helper using the shared controller.
  Its libsecret adapter stores credentials in Secret Service, never plaintext.
  Keep device prompts out of broadcast snapshots and all credentials out of
  frontend IPC, command arguments, JSON files, and logs. Demo mode is opt-in.
  `src/GHCPSpendTray.Linux.Gnome` provides the native GNOME popup and preferences.
  `.Linux.Kde` and `.Linux.Hyprland` provide Plasma and Waybar/Quickshell surfaces;
  `.Linux.Qml` contains their shared usage view.
  `tools/linux/package-native.py` packages these with bundled Qt/Python graphical
  setup in one AppImage. Installation must remain per-user, consent-based, and
  independent of the original download. Keep updates/removal ownership-checked.
  Preserve users' Waybar JSONC and includes; keep integration reversible and
  do not start or restart their bar during installation.
  Do not reintroduce Avalonia or modify general tray extensions. See `docs/LINUX-PROTOTYPE.md`.
- `tests` contains executable test harnesses; `tools` contains build,
  packaging, and smoke-test scripts.

Run `.\tools\verify.ps1` from the repository root for release-tooling checks,
a Release build, and all four .NET test harnesses. For changes to interop,
serialization, or AOT-sensitive code, also run
`.\tools\verify.ps1 -NativeTests`. Run build and publish commands sequentially:
they share intermediate directories.

On macOS, run `bash tools/macos/verify.sh` with stable Swift 6 Command Line Tools
and the pinned .NET SDK on an Apple-silicon Mac, running natively (not under
Rosetta). macOS Intel builds and execution are unsupported. Verification
includes managed/Native AOT shared tests, arm64-only app packaging, synthetic
Keychain CRUD, and native UI smoke runs.
CI/release toolchains are pinned in `.github/actions/setup-macos` (Xcode 26.6,
macOS 26.5 SDK). Support the latest patches of macOS 26 and 15; deployment
minimum is 15.0. Advance this two-major-version window with each adopted stable
major release. Routine CI builds once on macOS 26/Apple silicon (`macos-26`)
and tests that exact arm64 artifact on macOS 15/Apple silicon (`macos-15`)
without rebuilding.
Executable and Native AOT bridge slices must be exactly arm64; universal
and Intel-only artifacts are rejected. Windows x64/ARM64 support is unchanged.
Sparkle is a checksum-pinned binary dependency in `packaging/macos/sparkle.json`;
the `swiftc` build embeds its framework and makes every helper arm64-only before
inside-out signing. Keep updater state/preferences native to Swift and disabled
in development/demo/smoke modes. Public builds require `SPARKLE_PUBLIC_ED_KEY`;
release/feed signing additionally requires the protected private seed. Never
commit or log production signing keys. `macos-updates.yml` rebuilds the signed,
stable-only Pages appcast from immutable release assets and can be retried
without rebuilding or republishing a release.
Run `python3 tools/macos/rehearsal.py --signing-identity '<Developer ID identity>'`
for opt-in local real-app replacement/relaunch coverage. Rehearsal hooks live
outside shipping Swift sources, compile only with `GHCP_UPDATE_REHEARSAL=A|B`,
and cannot be built as Preview/Stable. Keep local-feed exceptions compile-only,
synthetic state isolated, private seeds ephemeral, and reports explicit about
the lack of notarization and standard-dialog interaction coverage.
Production Mac signing also requires Xcode's
`notarytool` and configured Apple credentials.
Mac versions live in `packaging/macos/version.txt` for development and
`macos-v*` release tags; Windows keeps `v*` tags. Preserve independent versioning.
The unified Release workflow selects independent bumps from stable releases
and persists its version plan across retries. Test changes with
`python3 -m unittest discover -s tools/release`.
Keep `tools/ci/changes.py` and its tests current when adding platform paths.
Markdown and images under `docs/images` do not trigger app builds; unknown
paths still verify all platforms. Keep the aggregate Verification check
running for documentation-only changes.

Linux paths have a separate verification lane; shared and unknown paths verify
all three platforms. Run `bash tools/linux/verify.sh` for shared tests,
Native AOT, GJS interoperability, and isolated install/upgrade/activation/removal.
It must not modify the user's desktop session or enable extensions there.
Keep Linux out of the Windows solution and production release workflow while
it remains an unsigned development build.

Preserve Native AOT compatibility and the component-only Windows App SDK
dependency graph; avoid adding the umbrella `Microsoft.WindowsAppSDK` package.
Local `.\tools\package.ps1` output is an unsigned development bundle, not a
public release. See `docs/RELEASING.md` for production packaging and signing.

Use synthetic accounts and data in tests and issue reports. Do not commit
tokens, device codes, or real account/consumption data. Keep authentication
host-specific and treat unsupported or stale consumption as unavailable, not
zero. See `docs/VALIDATION.md` for verification procedures and limitations.
Update it only when those procedures, enduring limitations, or release acceptance
gates change; keep per-change test results and investigation narratives in PR
descriptions and CI artifacts, not a running journal in that document.
