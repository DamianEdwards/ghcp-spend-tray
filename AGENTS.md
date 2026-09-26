# Working in this repository

GHCPSpendTray is a Windows 11 tray app built with .NET 10 Native AOT, WinUI 3,
and Microsoft UI Reactor. Use the SDK pinned in `global.json`; builds require
the Windows SDK and Visual Studio C++ build tools (including ARM64 tools for
packaging).

- `src\GHCPSpendTray.Core` contains testable domain, HTTP, and storage code.
- `src\GHCPSpendTray.App` contains the UI and Windows integration. Keep native
  Shell interop in `Native` and Windows services in `Platform`.
- `tests` contains three executable test harnesses; `tools` contains build,
  packaging, and smoke-test scripts.

Run `.\tools\verify.ps1` from the repository root for release-tooling checks,
a Release build, and all three test harnesses. For changes to interop,
serialization, or AOT-sensitive code, also run
`.\tools\verify.ps1 -NativeTests`. Run build and publish commands sequentially:
they share intermediate directories.

Preserve Native AOT compatibility and the component-only Windows App SDK
dependency graph; avoid adding the umbrella `Microsoft.WindowsAppSDK` package.
Local `.\tools\package.ps1` output is an unsigned development bundle, not a
public release. See `docs/RELEASING.md` for production packaging and signing.

Use synthetic accounts and data in tests and issue reports. Do not commit
tokens, device codes, or real account/consumption data. Keep authentication
host-specific and treat unsupported or stale consumption as unavailable, not
zero. See `docs/VALIDATION.md` for current evidence and limitations.
