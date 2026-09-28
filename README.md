# GHCPSpendTray

[![Install from the Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Install-0078D4?logo=microsoftstore&logoColor=white)](https://www.microsoft.com/store/productId/9PMX96TSF295)

GHCPSpendTray is an independent Windows 11 tray and macOS menu-bar app for keeping an eye on
GitHub Copilot AI-credit consumption. It shows per-account usage and allocation
and offers optional spending alerts without a browser tab or a hosted service.

[Install from the Microsoft Store](https://www.microsoft.com/store/productId/9PMX96TSF295)
&middot; [All releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases)
&middot; [Report an issue](https://github.com/DamianEdwards/ghcp-spend-tray/issues)

Both apps share their C# domain and application logic, compiled with .NET 10
Native AOT. Windows uses WinUI 3 and
[Microsoft UI Reactor](https://microsoft.github.io/microsoft-ui-reactor/main/);
macOS uses native SwiftUI and AppKit.
It does not require a separate .NET runtime, `gh`, WebView, or backend service.

## Features

- A tray flyout with per-account consumption and allocation meters.
- Multiple accounts, including different identities on the same GitHub host.
- Account avatars in the flyout and account settings when available, with
  initials when an image cannot be shown.
- Configurable refresh intervals and percentage or per-account USD-increment
  notifications.
- Local history and settings, with OAuth tokens in Windows Credential Manager
  or the macOS login Keychain.
- Opt-in launch at login on either platform.

## Install

### Windows

Requires Windows 11 22H2 (build 22621) or newer on x64 or ARM64.

Install [GHCPSpendTray from the Microsoft Store](https://www.microsoft.com/store/productId/9PMX96TSF295),
then start it from the Start menu. It stays in the notification area.

Alternatively, download `GHCPSpendTray-<version>.msixbundle` from
[GitHub Releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases),
open the signed bundle in Windows App Installer, and select **Install**.
The bundle contains both architectures; the symbols archive is for debugging,
not installation. Do not bypass Windows trust or organization-policy warnings.

To update a GitHub-installed copy, exit the app from its tray menu and install a
newer signed bundle from Releases. GitHub-distributed builds do not check for
updates automatically. An update with the same package identity preserves
settings and history. The Microsoft Store package has a separate identity and
is **not** an in-place upgrade from the GitHub package.

### macOS

Requires macOS 14 Sonoma or newer, on Apple silicon or Intel. Download
`GHCPSpendTray-macOS-<version>.dmg` from a **macOS** release on
[GitHub Releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases).
Open the disk image, drag **GHCPSpendTray** to **Applications**, then launch it.
The app lives in the menu bar, without a persistent Dock icon.
Public macOS releases must be Developer ID signed and Apple notarized; do not
bypass Gatekeeper or your organization's security policy. CI development
artifacts are not public releases.

Click the menu-bar icon to see consumption; secondary-click for **Open**,
**Refresh Now**, **Settings**, and **Quit**. Settings has the same **Usage**,
**Accounts**, **General**, **Notifications**, and **About** sections as Windows.
Use **General > Launch at login** to opt in; macOS may require approval in
**System Settings > General > Login Items**. Notifications also require macOS
permission and can be suppressed by Focus.

Updates are manual: quit GHCPSpendTray, then replace the app in Applications
with a newer macOS release. Its stable bundle identifier preserves access to
local settings, history, and Keychain credentials. Windows (`v*`) and macOS
(`macos-v*`) releases have independent versions; neither platform upgrades or
migrates the other's local data. The Mac app does not use the App Store.

## Connect an account

1. Open the tray flyout and choose the connect button, or open
   **Settings > Accounts > Add account**.
2. Enter the GitHub host and check the authentication and API destinations
   displayed by the app. Start device sign-in, then enter the displayed code on
   the host's GitHub authorization page in your browser. Verify the OAuth app
   shown on the consent screen before approving it.
3. Confirm the GitHub login and user ID displayed by GHCPSpendTray, then save.

The app has built-in public OAuth registrations for `github.com` and
`msft.ghe.com`; it does not use `gh` credentials or ask for a client secret.
For another enterprise host, create an approved host-specific OAuth app with
Device Flow enabled and enter its Client ID during onboarding. The ID is
stored with that account and used for reconnect and token refresh; existing
accounts on built-in hosts retain their registrations. Enterprise SSO,
managed-user, IP, and application policies may require administrator approval. Successfully
signing in to a host does not establish that its Copilot consumption API
is available.

The app downloads and caches avatars in its local data directory at sign-in,
so signed image links can expire without making the picture disappear. The
**Refresh** button on an account's management page fetches a new avatar as
well as consumption. Existing accounts without a cached image can use that
button to populate it; initials appear when no image is available.
Removing an account also removes its cached image.

GHCPSpendTray currently requests `read:user` for identity and optionally
`offline_access` for refresh tokens where supported. Consumption comes from
an **undocumented GitHub endpoint** that may change or be unavailable on some
hosts; the minimum OAuth scope for it has not been established. The app reports
unavailable data rather than treating it as zero. See
[validation and known limitations](docs/VALIDATION.md).

## Use and notifications

Left-click the tray icon to toggle the flyout or double-click it to open
**Settings > Usage**; right-click it for **Open**,
**Refresh now**, **Settings**, and **Exit**. Settings opens on **Usage**, with the
current total, per-account consumption and diagnostics, availability status,
and a manual refresh action. **Accounts** manages connections and
per-account preferences; **General** and **Notifications** configure refresh
and alerts. **About** shows the running app's version, including preview labels.
The default refresh interval
is 60 minutes (configurable from 5 to 1440), and the default allocation alerts
are 50%, 80%, and 100%.

In **Settings > Notifications**, you can also set a USD increment (for example,
`50` for alerts at $50, $100, and so on). An account can inherit that value,
override it, or disable it. Unknown or unlimited allocations can still use
dollar alerts, but not percentage alerts. Windows may suppress a notification
even when the app submits it.

Displayed USD consumption is `credits_used / 100` from GitHub's token-billing
quota data. It is **not** an invoice, a finance budget, or total spend across all
GitHub products. Stale or partial observations are identified; previous billing
periods are not counted as current spend. GitHub's quota snapshot does not
provide a verified per-day consumption breakdown, so the app does not chart
daily usage from its locally sampled refreshes.

## Privacy and removal

GHCPSpendTray talks directly to your GitHub host over HTTPS. It does not send
account data or diagnostic logs to a developer-operated backend. Settings and
history are stored locally; access and refresh tokens are held in Windows
Credential Manager or the macOS Keychain. Use **Settings > General > Open data folder** to find your
local files. See the [privacy policy](PRIVACY.md) for retention and data handling.

Before uninstalling, remove accounts in the app to delete their locally stored
credentials. To revoke an OAuth grant, use **Manage OAuth grants** in account
details or the host's application settings; removing an account or uninstalling
does not necessarily revoke it. Then exit the app and uninstall through
**Windows Settings > Apps > Installed apps**. Windows normally removes
package-local data, but generic Credential Manager entries may remain.

On macOS, disable **Launch at login**, remove accounts, quit the app, and move
it from Applications to the Trash. Deleting the app does not delete
`~/Library/Application Support/GHCPSpendTray` or its Keychain items. Remove
that data directory separately if desired. If credentials remain, remove only
the app's `com.damianedwards.GHCPSpendTray` service items in Keychain Access.

If sign-in or consumption fails, check the displayed host destinations and
error, your organization's policy, and the app's bounded `logs` directory in
the data folder. Do not post tokens, device codes, unredacted configuration or
history, or private consumption details in public issues.

## Build and contribute

### Windows development

Install the .NET SDK pinned in [`global.json`](global.json), Visual Studio
C++ build tools, the Windows SDK, and ARM64 native tools. From PowerShell:

```powershell
git clone https://github.com/DamianEdwards/ghcp-spend-tray.git
Set-Location ghcp-spend-tray
.\tools\verify.ps1
.\tools\package.ps1
```

`verify.ps1 -NativeTests` also runs the test suites under executed x64 Native
AOT. Run build and publish commands sequentially because they share
intermediates. Local `package.ps1` output is an **unsigned development bundle**,
not the signed public release. For isolated synthetic UI checks, see
[`VALIDATION.md`](docs/VALIDATION.md); for release and signing details, see
[`RELEASING.md`](docs/RELEASING.md).

### macOS development

Install the SDK pinned in `global.json` and stable Xcode Command Line Tools
with Swift 6 (`xcode-select --install` if missing). A separate .NET macOS
workload, MAUI, or Xcode project is not required. Python 3 is used by build and
release checks. From the repository root:

```bash
bash tools/macos/verify.sh
open artifacts/macos/GHCPSpendTray.app
```

Verification runs managed and native shared tests, builds a universal
arm64/x86_64 app, exercises synthetic Keychain items, and launches populated
and empty SwiftUI smoke scenarios. The Keychain test deletes its own uniquely
named synthetic item; it never reads account credentials or enables login
startup. The resulting app is ad-hoc signed for local development only.
`bash tools/macos/build.sh` builds without running tests. If using a preview
toolchain, select a stable SDK with `SDKROOT`; see [release documentation](docs/RELEASING.md).

The Windows solution is [`GHCPSpendTray.slnx`](GHCPSpendTray.slnx).
`Core` contains domain, HTTP, and storage code; `Application` contains the
shared controller and view models; `App` contains Windows UI/platform
integration; `MacBridge` exports the Native AOT C ABI, and `Mac` contains
SwiftUI/AppKit UI and native services. Build platforms sequentially when using
the same checkout. Use synthetic data in tests and issue reports.

## License

[MIT](LICENSE). Copyright (c) 2026 Damian Edwards.

GHCPSpendTray is not affiliated with or endorsed by GitHub. Included .NET,
Windows App SDK, and Reactor components retain their own applicable licenses.
