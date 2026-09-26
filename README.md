# GHCPSpendTray

[![Install from the Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Install-0078D4?logo=microsoftstore&logoColor=white)](https://www.microsoft.com/store/productId/9PMX96TSF295)

GHCPSpendTray is an independent Windows 11 tray app for keeping an eye on
GitHub Copilot AI-credit consumption. It shows per-account usage and allocation,
recent history, and optional spending alerts without a browser tab or a hosted
service.

[Install from the Microsoft Store](https://www.microsoft.com/store/productId/9PMX96TSF295)
&middot; [All releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases)
&middot; [Report an issue](https://github.com/DamianEdwards/ghcp-spend-tray/issues)

The app is built with C#, .NET 10 Native AOT, WinUI 3, and
[Microsoft UI Reactor](https://microsoft.github.io/microsoft-ui-reactor/main/).
It does not require a separate .NET runtime, `gh`, WebView, or backend service.

## Features

- A tray flyout with per-account consumption, allocation meters, and 24-hour
  history.
- Multiple accounts, including different identities on the same GitHub host.
- Configurable refresh intervals and percentage or per-account USD-increment
  notifications.
- Local history and settings, with OAuth tokens in Windows Credential Manager.
- Opt-in launch at Windows startup.

## Install

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

## Connect an account

1. Open the tray flyout and choose the connect button, or open
   **Settings > Accounts > Add account**.
2. Enter the GitHub host and check the authentication and API destinations
   displayed by the app. Start device sign-in, then enter the displayed code on
   the host's GitHub authorization page in your browser. Verify the OAuth app
   shown on the consent screen before approving it.
3. Confirm the GitHub login and user ID displayed by GHCPSpendTray, then save.

The app has a built-in public OAuth registration for `github.com`; it does not
use `gh` credentials or ask for a client secret.
Enterprise hosts without a built-in registration require an approved
host-specific OAuth registration added to the app before sign-in is available;
there is no user-configurable client ID. Enterprise SSO, managed-user, IP,
and application policies may require administrator approval. Successfully
signing in to a host does not establish that its Copilot consumption API
is available.

GHCPSpendTray currently requests `read:user` for identity and optionally
`offline_access` for refresh tokens where supported. Consumption comes from
an **undocumented GitHub endpoint** that may change or be unavailable on some
hosts; the minimum OAuth scope for it has not been established. The app reports
unavailable data rather than treating it as zero. See
[validation and known limitations](docs/VALIDATION.md).

## Use and notifications

Left-click the tray icon to open the flyout; right-click it for **Open**,
**Refresh now**, **Settings**, and **Exit**. Settings include account management,
refresh preferences, and notification thresholds. The default refresh interval
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
periods are not counted as current spend.

## Privacy and removal

GHCPSpendTray talks directly to your GitHub host over HTTPS. It does not send
account data or diagnostic logs to a developer-operated backend. Settings and
history are stored locally; access and refresh tokens are held in Windows
Credential Manager. Use **Settings > General > Open data folder** to find your
local files. See the [privacy policy](PRIVACY.md) for retention and data handling.

Before uninstalling, remove accounts in the app to delete their locally stored
credentials. To revoke an OAuth grant, use **Manage OAuth grants** in account
details or the host's application settings; removing an account or uninstalling
does not necessarily revoke it. Then exit the app and uninstall through
**Windows Settings > Apps > Installed apps**. Windows normally removes
package-local data, but generic Credential Manager entries may remain.

If sign-in or consumption fails, check the displayed host destinations and
error, your organization's policy, and the app's bounded `logs` directory in
the data folder. Do not post tokens, device codes, unredacted configuration or
history, or private consumption details in public issues.

## Build and contribute

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

The solution is [`GHCPSpendTray.slnx`](GHCPSpendTray.slnx). The `Core` project
contains domain, HTTP, and storage code; the `App` project contains the Windows
UI and platform integration; `tests` contains executable test harnesses.
Please run `.\tools\verify.ps1` before proposing changes, and use synthetic
data in tests and issue reports.

## License

[MIT](LICENSE). Copyright (c) 2026 Damian Edwards.

GHCPSpendTray is not affiliated with or endorsed by GitHub. Included .NET,
Windows App SDK, and Reactor components retain their own applicable licenses.
