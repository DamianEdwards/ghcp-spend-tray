# GHSpend implementation plan

> September 23 UI migration: the user superseded direct Win32 controls with
> Microsoft UI Reactor/WinUI 3, approved .NET 10 to retain working Native AOT,
> and chose self-contained ZIPs run from the extracted folder (no self-copy;
> startup is opt-in). The cost-first tray flyout opens above its icon; the gear
> opens settings. Dollar-increment alerts apply per account, with configurable
> amounts. README.md and VALIDATION.md describe the current implementation.

> Historical approved plan. On September 17, 2026, the user superseded the
> app-owned/per-host OAuth registration design: use GitHub CLI's fixed public
> client ID and remove user-specified client IDs. Current behavior and validation
> status are documented in README.md and VALIDATION.md. The original plan follows,
> with tenant and consumption examples generalized for public distribution.
>
> September 23, 2026 update: the user registered the project-owned GHCPSpend
> OAuth app with Device Flow, expiring tokens, and no redirect URI. Its public
> client ID now replaces the CLI ID; the UI still does not accept client IDs.
> A separate registration for msft.ghe.com was subsequently supplied and wired
> through automatic, exact-host client-ID selection.

## Outcome and scope

Build a small, per-user Windows tray application that monitors Copilot month-to-date consumption across multiple GitHub accounts. Download and run one native executable: it installs itself under `%USERPROFILE%\.ghspend`, enables startup at login, launches the installed copy, and exits the downloaded copy. No administrator rights, separately installed .NET runtime, GitHub CLI, browser extension, or backend service.

The initial release targets Windows 11, with separate `win-x64` and `win-arm64` downloads. Use C#, .NET 11 RC1, Native AOT, and native Win32 windows and controls. No WinForms, WPF, WinUI, WebView, or Electron.

This document is a plan only. No OAuth app, startup registration, or installation has been created.

## Verified foundation and release gates

Read-only prototype tests succeeded for multiple accounts, including distinct identities on github.com and a GHE.com tenant. Each returned token-based consumption and allocation from `GET /copilot_internal/user`.

The relevant response is `quota_snapshots.premium_interactions`, containing `credits_used`, `entitlement`, `unlimited`, `has_quota`, `token_based_billing`, and `timestamp_utc`. The top-level response supplies identity and quota-reset information.

Calculating USD as credits divided by 100 reproduced the corresponding usage portal's scale. Calculating percentage from credits and entitlement reproduced its meter precision. Do not derive spend from `remaining`, `quota_remaining`, or rounded `percent_remaining`: the observed fields were not perfectly consistent.

Resolve these gates before building the full UI:

1. **App-owned authentication:** The successful probes used existing tokens. They do not prove that a newly registered GHSpend OAuth app can read this undocumented endpoint. Validate device-flow tokens from GHSpend itself on github.com and a GHE.com tenant, including enterprise-managed users. Establish the minimum scopes and required enterprise approvals. Do not borrow GitHub CLI, VS Code, or Copilot client IDs.
2. **Host capability:** Supporting a custom GitHub host does not guarantee that it implements the Copilot endpoint. In particular, GitHub Enterprise Server and GHE.com are different products. Detect and report unsupported consumption APIs explicitly.
3. **Branding:** Publicly downloadable GitHub artwork is not permission to use it as this app's icon. GitHub's guidelines prohibit that use without permission. Use the requested GitHub executable/tray icon only with permission; otherwise use an original GHSpend icon and permitted, secondary GitHub integration branding.

If the app-owned OAuth flow is rejected by enterprise policy or the endpoint, stop and resolve registration/approval with the relevant administrator. An alternative authentication mechanism would require a separate decision, not a silent substitution.

## User experience

### Tray behavior

The tray icon is the application's primary entry point. Hover shows a compact native tooltip, for example:

```text
GHSpend | MTD $123.45 | 3 accounts
Updated 16:31
```

Keep within the native tooltip buffer limit, including its null terminator. With `NOTIFYICON_VERSION_4`, request the standard tooltip using `NIF_SHOWTIP`.

Left-click or keyboard activation opens the overview window. Right-click provides Open, Refresh now, Add account, Settings, Start with Windows, and Exit. Closing the overview hides it; Exit stops the process without disabling future login startup.

Tooltip and overview must distinguish a complete fresh total from a partial or stale total. Never silently substitute zero for an unavailable account. If last-known values contribute, label the total as last-known and show account freshness in the overview. Never roll a previous month's cached spend into the new month's total.

### Overview window

A compact, resizable native window contains a total, refresh status, and an accessible account list. Selecting an account shows its details and sparkline. Use native controls for text, actions, and progress; custom painting is limited to the graph.

Each account shows:

| Element | Content |
| --- | --- |
| Identity | Optional display name, verified GitHub login, and host |
| Consumption | Month-to-date USD and raw AI credits |
| Allocation | USD equivalent and percentage consumed |
| Meter | Native progress bar; text retains percentages above 100% |
| History | Rolling 24-hour sparkline of observed spend rate |
| Status | Last successful fetch, source timestamp when available, next refresh |
| Actions | Refresh, reconnect, edit display name, remove |

Use the label **Consumption**, not invoice cost. Explain that USD represents AI-credit consumption value and can differ from billed charges, internal finance budgets, and cross-product AI spend. Display allocation numerically; do not invent the portal's "Standard" classification.

The sparkline is accompanied by text such as "Observed +$12.40 over the last 24 hours" so it is not the sole presentation of information.

### Settings

Provide a global polling interval, default **60 minutes**, plus editable alert thresholds defaulting to **50%, 80%, and 100%**. Proposed interval range: 5 minutes through 24 hours, validated explicitly in the UI. Changes take effect immediately without restarting.

Also provide Start with Windows, Enable notifications, Test notification, and Open data folder. Thresholds are sorted, distinct positive percentages; permit values above 100% because additional usage can be allowed. Per-account threshold overrides are supported, with global defaults inherited unless overridden.

## Account onboarding and authentication

Use an OAuth App with device flow enabled. Embed only the public client ID. No client secret belongs in the executable, configuration, or repository.

The distributed build has a GHSpend-owned github.com registration. Custom hosts use a registration valid for that host. Allow a host-specific client ID in advanced onboarding; deployments can provide an approved default. Do not assume a github.com client ID works on a GHE.com tenant or GHES installation.

Host resolution:

| Host type | Web/auth base | API base |
| --- | --- | --- |
| GitHub.com | `https://github.com` | `https://api.github.com` |
| GHE.com tenant | `https://TENANT.ghe.com` | `https://api.TENANT.ghe.com` |
| Enterprise Server | `https://HOST` | `https://HOST/api/v3` |

For GHES, resolve the endpoint relative to the API base without accidentally discarding `/api/v3`. A successful login with an unsupported consumption endpoint is a visible unsupported-host state, not an account with zero spend.

Onboarding steps:

1. Choose github.com or enter an enterprise host. Show the resolved host type and auth/API destinations. Accept HTTPS hosts only, normalize hostname casing, and reject user-info, query strings, and arbitrary paths. Preserve an explicit supported port for GHES.
2. Send `POST /login/device/code` to the web/auth host with the client ID and the validated minimum scopes. Start scope testing with basic identity access; request broader scopes only if evidence shows they are necessary. Do not request repository or billing-admin access by default.
3. Display the user code, Copy code, Open browser, expiration countdown, and Cancel. Open the returned verification URL only after validating its HTTPS origin against the expected auth host.
4. Poll `POST /login/oauth/access_token` using the returned interval and device grant type. Handle `authorization_pending`, `slow_down`, expiry, cancellation, denied consent, invalid registration, and disabled device flow.
5. Fetch `/user` and the consumption endpoint. Show the authenticated identity for confirmation before saving, especially when adding a second account on the same host.
6. Key accounts by normalized host and immutable GitHub user ID, not login or host alone. Detect duplicate accounts and offer reconnection instead of double-counting them.
7. Persist credentials in Windows Credential Manager. Keep access and refresh tokens together as one versioned credential payload, using a stable account-specific target name. Use `CredWriteW`, `CredReadW`, `CredDeleteW`, and `CredFree`.

Support expiring and non-expiring device-flow tokens. Where available, request `offline_access` and refresh before expiry. Current GitHub documentation permits refreshing device-flow-issued tokens without a client secret. Serialize refresh per account and securely replace the rotated token pair before further requests. Hosts without refresh-token support fall back to explicit reauthentication on expiry, not an embedded secret.

Honor SSO, enterprise app restrictions, IP restrictions, and rate limits. Do not bypass them. Disable automatic cross-origin redirects for authenticated HTTP requests; never send one account's credentials to another host. Keep tokens and device codes out of logs, exceptions, telemetry, and command-line arguments.

Removing an account deletes its local credential and stops monitoring it. Explain that local removal is not necessarily revocation of the OAuth grant; provide the appropriate GitHub application-settings link.

## Consumption and historical data

Encapsulate the undocumented API behind `ICopilotUsageProvider`. Source-generated JSON models map to an internal `UsageSnapshot`; UI and alert code do not depend on the wire schema.

For a valid token-billed snapshot:

```text
consumptionUsd = creditsUsed / 100m
allocationUsd = entitlement / 100m
percentConsumed = creditsUsed / entitlement * 100m
```

Use `decimal`, retain source precision, and round only for presentation. Isolate the 100-credits-per-USD conversion as a provider policy with documentation and tests, rather than scattering it through UI code.

Require present, valid fields. An absent field must not deserialize into an apparently valid zero. For non-token-based billing, show unsupported dollar accounting rather than treating premium-request counts as cents. Unlimited or missing/zero allocations display N/A for percentage and do not generate allocation-percentage alerts.

Persist account ID, fetch time in UTC, source time when present, billing-period identity/reset date, credits used, entitlement, conversion-policy version, and computed values. Preserve successful observations even if a later fetch fails. Reset-period information takes precedence over local calendar guesses.

History uses per-account, per-month JSONL files with a proposed 90-day retention period. This avoids a database/native dependency for a small hourly dataset. Stream loading and compact only at scheduled maintenance, not every UI refresh. Store configuration and alert state as versioned JSON with atomic replace and a recovery copy.

The sparkline plots observed interval spend normalized to USD/hour:

```text
rate = (newConsumption - previousConsumption) / elapsedHours
```

Use actual timestamps, not the configured interval. Changing the interval must not distort the graph. Do not draw across billing resets, long offline gaps, or negative corrections. Label gaps and corrections; never invent a zero or negative spend rate. Rate is an observation between polls, not the exact timing of server-side usage.

First sample: show "Collecting history". Second comparable sample: show the first segment. Retain corrections in history for diagnostics. A crash-truncated final JSONL record can be quarantined with an explicit diagnostic; other corruption is surfaced, not silently discarded.

## Polling and failure behavior

Refresh immediately after adding/reconnecting an account and when the installed app starts. Poll hourly thereafter using one cancellable scheduler with bounded parallelism across accounts and at most one in-flight operation per account.

Run HTTP and storage work away from the UI thread; post immutable state changes back to the window thread. Reuse `HttpClient` without mutable shared authorization headers across accounts.

Manual refresh coalesces with an existing refresh. Resume from sleep triggers one catch-up refresh if overdue, not a burst for every missed interval. Network recovery retries overdue accounts with jitter.

Apply timeouts and bounded exponential backoff for transient failures; honor `Retry-After` and GitHub rate-limit signals. A 401 first attempts a supported refresh flow, then becomes "Sign in required". A 403 may indicate SSO/policy/permissions or throttling and should retain the service's useful diagnostic. A 404 may mean unsupported API or unavailable capability; do not reinterpret it as no consumption.

Missing schema fields, invalid values, disk-write failures, or credential-store failures are visible states. A new sample that could not be saved may be shown as unsaved, but must not be presented as durably recorded. Maintain a bounded, redacted local diagnostic log; no telemetry by default.

## Alert rules and Windows notifications

Default percentage alerts apply independently to each account. A total is informative, but there is no implied shared allocation across unrelated accounts.

Evaluate fresh, validated snapshots. When one or more not-yet-notified thresholds are reached, emit one notification naming the account, consumption, allocation, and highest reached threshold. For a jump from 45% to 105%, send one 100% alert and mark 50%, 80%, and 100% reached for that period.

Persist deduplication state by account, billing period, and threshold. Restarting, manual refresh, and temporary decreases must not re-send the same alert. Reset that state when a new billing period is confirmed. On initial onboarding or when a new threshold is configured below existing consumption, send one current-state alert after the next successful sample.

Allocation changes use the current validated denominator; a newly crossed threshold can alert, but already-notified thresholds do not rearm merely because the allocation changes. Unlimited/unknown allocation never produces a divide-by-zero or misleading 100% alert.

For v1, use **native Shell notifications** through `Shell_NotifyIconW` with `NIF_INFO`, `NIIF_RESPECT_QUIET_TIME`, and account-specific text. This satisfies the requested Windows notification without adding WinRT or COM activation infrastructure. Clicking a notification while the app runs opens the relevant account.

Do not promise persistent Notification Center history or activation after exit from this API. Durable/actionable modern toasts would be a later feature with AUMID registration and AOT-compatible WinRT/COM activation.

Only mark notification submission after the Shell accepts the call; record errors and retry with bounds. Exactly-once delivery cannot be guaranteed across a crash between Shell submission and persistence. DND and Windows notification settings can suppress display even after successful submission. The UI must distinguish "submitted to Windows" from verified user delivery.

## Native implementation

Pin SDK **11.0.100-rc.1.26425.128**, the .NET 11 RC1 SDK listed in Microsoft's release metadata, in `global.json` with prerelease enabled and roll-forward disabled.

Use `net11.0-windows`, `OutputType=WinExe`, `PublishAot=true`, unsafe code where required, and source-generated `System.Text.Json` serialization. Publish separately for x64 and ARM64; pin dependency versions and reject unresolved trimming/AOT warnings.

Use `[LibraryImport]` for handwritten imports, explicit Unicode entry points, blittable structures, `SafeHandle` where ownership applies, and unmanaged function pointers with `[UnmanagedCallersOnly]` for callbacks. CsWin32 may generate definitions where helpful, configured for AOT-compatible non-runtime marshalling; avoid duplicate declarations across generated and handwritten code.

Use a hidden top-level window and standard message loop for tray callbacks and broadcast messages. A message-only window is insufficient for broadcasts such as Explorer's `TaskbarCreated`.

| Surface | APIs/approach |
| --- | --- |
| Windows and controls | `RegisterClassExW`, `CreateWindowExW`, common controls, native dialogs |
| Tray | `Shell_NotifyIconW`, stable icon GUID, `NOTIFYICON_VERSION_4` |
| Explorer restart | Register `TaskbarCreated`; re-add icon and version |
| Context menu | `CreatePopupMenu`, `TrackPopupMenuEx`, keyboard activation |
| Sparkline | Double-buffered GDI painting in a small custom child window |
| Browser | `ShellExecuteExW` with validated URLs |
| Credentials | Windows Credential Manager |
| Startup | HKCU Run registration |
| DPI/accessibility | Per-monitor-v2 manifest, `WM_DPICHANGED`, native tab/focus behavior |

Use a common-controls v6 manifest. Follow system fonts/colors, high contrast, keyboard navigation, and screen-reader-readable textual values. Honor supported theme APIs; do not rely on undocumented dark-mode ordinals. All unmanaged callbacks must prevent managed exceptions from crossing the ABI boundary, while reporting failures explicitly.

## First-run self-installation

Recommended layout:

```text
%USERPROFILE%\.ghspend\
  ghspend.exe
  config.json
  state.json
  history\
  logs\
```

Credentials remain in Windows Credential Manager, not this directory.

Bootstrap sequence:

1. Resolve the actual user profile through Windows, not an assumed `C:\Users\...` path. Compare normalized executable and install paths; if already installed, skip installation.
2. Acquire a per-user installation lock. Reject unexpected reparse-point destinations and ensure the directory has appropriate per-user permissions. Preserve existing configuration and history.
3. Copy the executable to a staging file within the destination, verify the byte hash, and atomically promote it to `ghspend.exe`. Preserve Windows download-origin/security metadata; do not remove Mark of the Web or bypass SmartScreen.
4. Write the quoted absolute executable path with `--startup` to the app's own value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Verify the value and preserve any prior value needed for rollback. Do not modify other startup entries.
5. Launch the installed executable with a short-lived, current-user-restricted readiness handshake. The installed process becomes the per-user singleton, initializes its native window and tray icon, then signals ready.
6. Exit the downloaded process only after successful handoff. If startup registration, launch, or initialization fails, show the specific error and roll back only this attempt's changes; do not report successful installation.
7. Show onboarding from the installed copy. It now runs independently of the original download, which the user can delete. Do not automatically delete arbitrary source/download files.

Use a separate running-instance lock and narrowly scoped IPC so reopening the executable activates the existing overview instead of creating another tray icon. Do not let the bootstrap hold the runtime mutex and deadlock its child.

A repeat launch of the same downloaded version focuses the installed instance. For v1, detect a different already-installed version and explain that replacement requires exiting the installed app; do not overwrite a running executable or silently downgrade it. Automatic updates are out of scope.

Settings can disable startup by deleting only GHSpend's own Run value. If Windows startup settings disable the app externally, do not fight or silently reverse that choice on ordinary launches. Include documented removal steps for the startup value, credentials, and app-owned directory contents.

Sign distributed binaries if a signing identity is available. An unsigned build may trigger SmartScreen; the application must not try to bypass that warning.

## Code organization

Keep the production implementation in one native app plus a small testable core:

| Component | Responsibility |
| --- | --- |
| `GHSpend.App` | Bootstrap, message loop, native windows, tray and platform services |
| `GHSpend.Core` | Accounts, snapshots, money/percentage math, scheduler, alert policy |
| `GHSpend.Tests` | Domain tests, HTTP fixtures, persistence and failure tests |
| Auth services | Host resolver, device flow, token lifecycle, credential-store abstraction |
| Usage provider | HTTP calls, schema validation, provider-specific conversion |
| Storage services | Versioned settings, bounded history, alert ledger |

Use lightweight explicit construction rather than reflection-based service discovery. No plugin system, local web server, or embedded browser is needed.

## Delivery sequence and acceptance

1. **Prove auth and AOT first.** Publish a minimal native executable that performs GHSpend's device flow, reads identity and quota data, writes/reads a credential, and displays a tray icon plus Shell notification. Validate github.com and GHE.com accounts using app-owned registrations. This is the go/no-go gate.
2. **Implement the core.** Add strict snapshot parsing, conversion, period handling, persistence, scheduling, history aggregation, and alert deduplication.
3. **Build the native UX.** Implement overview, add-account flow, settings, account controls, accessible meter values, sparkline, and tooltip.
4. **Implement install/handoff.** Exercise clean install, existing install, quoted Unicode paths, startup registration, readiness failure, and repeat launch.
5. **Harden and release.** Publish signed x64/ARM64 executables where possible, include source/license notices and setup/troubleshooting documentation, and verify no runtime prerequisite on clean Windows machines.

Acceptance checks:

- Two different accounts on github.com and one on a GHE.com tenant coexist without credential crossover or double-counting. Duplicate IDs on different hosts remain distinct.
- Device-flow expiry, slow-down, denied approval, wrong selected account, token rotation, SSO restriction, and unsupported GHES consumption endpoints produce actionable states.
- Default refresh is exactly one hour; interval changes, manual refresh, resume, and rate limiting do not overlap requests or freeze the UI.
- Money and percentage calculations match stored fixtures, including zero, missing, unlimited, over-100%, and non-token-based quotas.
- Sparklines retain history across restart and correctly represent interval changes, gaps, corrections, and monthly reset.
- Thresholds 50/80/100 fire as specified, coalesce jumps, persist deduplication, and rearm only for a new billing period.
- Partial/stale totals and historical-month data cannot masquerade as current complete spend.
- Explorer restart restores the tray icon. DPI changes, high contrast, keyboard-only use, and screen readers remain usable.
- On a clean standard-user Windows installation, run the downloaded file, finish handoff, delete the downloaded file, and verify the installed app continues working and starts at the next login.
- Published native executables work without an installed .NET runtime or `gh`; tests cover both supported architectures and actual Windows UI integration, not just JIT unit tests.
- A multi-day soak shows bounded memory/handles, bounded retained history, correct sleep/resume behavior, and no secrets in files or logs.

## Sources

- [OAuth app device flow and token refresh](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps)
- [Copilot OAuth integration guidance](https://docs.github.com/en/copilot/how-tos/copilot-sdk/setup/github-oauth) - general integration guidance, not a guarantee for the undocumented quota endpoint.
- [GHE.com feature and URL differences](https://docs.github.com/en/enterprise-cloud@latest/admin/data-residency/feature-overview-for-github-enterprise-cloud-with-data-residency)
- [Shell_NotifyIconW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
- [Source-generated P/Invoke](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- [.NET 11 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json)
- [GitHub logo usage rules](https://brand.github.com/foundations/logo)
