# GHCPSpendTray

[![Install from the Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Install-0078D4?logo=microsoftstore&logoColor=white)](https://www.microsoft.com/store/productId/9PMX96TSF295)

GHCPSpendTray is an independent Windows 11 tray app for keeping an eye on
GitHub Copilot AI-credit consumption. It shows per-account usage and allocation
and offers optional spending alerts without a browser tab or a hosted service.

[Install from the Microsoft Store](https://www.microsoft.com/store/productId/9PMX96TSF295)
&middot; [All releases](https://github.com/DamianEdwards/ghcp-spend-tray/releases)
&middot; [Report an issue](https://github.com/DamianEdwards/ghcp-spend-tray/issues)

The app is built with C#, .NET 10 Native AOT, WinUI 3, and
[Microsoft UI Reactor](https://microsoft.github.io/microsoft-ui-reactor/main/).
It does not require a separate .NET runtime, `gh`, WebView, or backend service.

## Features

- A tray flyout with per-account consumption and allocation meters.
- Configurable pie-chart or percentage tray icons: one weighted roll-up by
  default, or one icon per selected account.
- Multiple accounts, including different identities on the same GitHub host.
- Account avatars in the flyout and account settings when available, with
  initials when an image cannot be shown.
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
2. A github.com sign-in code is generated immediately and copied to the clipboard.
   Select **Open browser**, paste the code on GitHub, and authorize GHCPSpendTray.
   Check the account and OAuth app shown on GitHub before approving. If copying
   failed, use **Copy code** to retry or select and copy the displayed code yourself.
3. Return to GHCPSpendTray. The account is verified and saved automatically after
   browser authorization; **Complete!** appears when setup finishes. There is no
   additional in-app confirmation.

For an enterprise account, select **Change host** to cancel the github.com attempt,
choose the host and its OAuth Client ID, check the displayed destinations, then
select **Start sign-in**. New Add account actions always start with github.com.
**Reconnect** starts immediately using the account's original host and registration;
a different browser identity is rejected rather than overwriting that account.
**Cancel sign-in** stops the current attempt, and **Try again** generates a new code.
Sign-in and clipboard failures are reported in the UI and diagnostic logs without
logging device codes, tokens, or server response bodies.

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
and a manual refresh action. With no accounts connected, Usage instead shows a
simple **Add account** prompt without empty totals, diagnostics, or refresh controls.
**Accounts** manages connections and
per-account preferences; **General** and **Notifications** configure refresh
and alerts. **About** shows the running app's version, including preview labels.
The default refresh interval
is 60 minutes (configurable from 5 to 1440), and the default allocation alerts
are 50%, 80%, and 100%.

In **Settings > General > System tray**, choose **Pie chart** or **Percentage
number**, one roll-up or per-account icons, and the connected accounts to include.
Choose **Save changes** to apply and persist the preferences. By default all
accounts, including newly connected accounts, contribute to a single pie.
The **Live preview** updates immediately as you change the draft style, icon mode,
or included accounts. It uses current usage and the same native-size pixels as
the real icons, with labels identifying each account. Previewing does not change
the notification area or save anything; a failed save leaves the existing tray
configuration unchanged. Freshness and availability updates also reach the preview.
Per-account icons open their account details on click; **Open GHCPSpendTray**
in any icon's context menu still opens the flyout. Double-click, keyboard access,
refresh, Settings, notifications and Exit remain available. If no accounts are
selected or connected, a neutral access icon remains.

The roll-up percentage is **eligible consumption divided by the same accounts'
eligible allocation**, not an average of account percentages. Only valid, fresh,
current-period observations with known, finite positive allocation qualify.
Stale, failed, unsupported, unknown/zero-allocation and unlimited accounts are
excluded from both sides. `!` marks a partial roll-up; `?` means no percentage is
available, not zero. Hover for the percentage and included/selected counts;
**Settings > Usage** lists every selected account and its inclusion or exclusion
reason. These preferences do not filter or change the existing dollar totals.

Numbers are rounded to whole percentages, with `<1` below 1% and `999+` above
999%. Numbers use native font glyphs rather than a pixel font; the percentage
unit is shown in the tooltip and preview label instead of a tiny extra glyph.
Both styles are antialiased at the taskbar's pixel size on a transparent canvas.
The settings preview places them on a taskbar-colored swatch so they remain
readable when the app and taskbar themes differ.
Pies fill to 100%; a `+` on either style marks over-allocation. Tooltips retain the
unsaturated percentage (up to two decimals, with `<0.01%` for smaller nonzero
values) and label over-allocation. Eligibility is reevaluated at least every
minute and on refresh/resume, including billing rollover. Windows controls
notification-area overflow and icon visibility; the app cannot force icons to
remain unhidden.

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
