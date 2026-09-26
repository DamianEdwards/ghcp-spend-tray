# GHCPSpendTray Core

The core targets .NET 10 and is Native AOT compatible. It has no package dependencies.
All JSON serialization uses generated metadata; `CoreJsonContext.Default.TokenSet` is
the public metadata for platform Credential Manager payloads.

## Integration

1. Construct one long-lived `HttpClient` using `HttpTransport.CreateClient()`. It
   disables redirects and cookies; never replace it with a client that follows
   redirects. Tests inject a fake message handler and never make network requests.
2. Construct `DeviceFlowClient`, the platform's `ICredentialStore`, `TokenManager`,
   and `CopilotUsageProvider`. Credentials are never stored by `JsonStore`.
3. Load `JsonStore.LoadSettingsAsync()` and surface its recovery diagnostics.
   Validate and save changed settings before applying `MonitorService.UpdateSettings()`.
4. Implement `INotificationSink.SubmitAsync` to return true **only** when Windows
   accepts Shell notification submission. `AlertService` persists only accepted
   thresholds. Acceptance is not proof of display or user delivery; a crash between
   submission and ledger persistence can produce a duplicate.
    Dollar increments are optional global defaults with nullable per-account overrides
    (`null` inherits, `0` disables). `UsageAlert.SpendMilestoneUsd` carries the highest
    newly crossed USD level; it may coexist with allocation thresholds in one alert.
    Persisted dollar high-water marks are per account/period, independent of the
    selected increment, so changing increments cannot rearm already-reported amounts.
5. Subscribe to `MonitorService.StateChanged` and marshal to the native UI thread.
   Events run off-thread; their state is detached from mutable configuration.
   Subscribe to `DiagnosticReported` on both monitor and store.
6. `StartAsync()` starts the background loop and returns immediately. `RefreshAsync()`
   shares per-account work with existing callers; cancelling the caller stops its
   wait, not another caller's operation. Dispose the monitor on exit.
7. Call `NotifyResume()` on resume and `NotifyNetworkRecovery()` on restored
   connectivity (the latter adds up to 30 seconds of retry jitter). Only overdue accounts run.
   To remove an account, **await `RemoveAccountAsync(key)` before deleting its
   credentials**, then save settings without it. This prevents token rotation from
   writing credentials back after deletion. `AlertService.RemoveAccountAsync` and
   `JsonStore.DeleteAccountHistoryAsync` provide optional local-data deletion.
   Local deletion does not revoke the host's OAuth grant.

Onboarding is explicit: resolve host; select `GitHubOAuth.ResolveClientId(host, account.OAuthClientId)`;
`BeginAsync`; show user code and validated verification URL; `PollAsync`;
`GetIdentityAsync`; `FetchWithTokenAsync`; confirm immutable identity; save credentials
and settings; refresh. Account keys include canonical host and immutable numeric ID,
allowing multiple users on one host without double counting.
For reconnect, await `PauseAccountAsync(key)` before replacing the saved credential.
It cancels and drains in-flight work without changing persistent settings or credentials.
Then call `UpdateSettings` with the account still present to resume monitoring.

## Authentication gate

GHCPSpendTray selects project-owned GHCPSpendTray registrations with `GitHubOAuth.ResolveClientId`:
`github.com` uses `Ov23ctzkXY5CJhfKQo7T`, while `msft.ghe.com` uses
`Ov23ox38SoD1bIpzU9zZ`. Other custom hosts require a host-specific OAuth Client ID
registered on that host with Device Flow enabled. A custom host's ID is saved on
the account in `config.json`; absent IDs in older settings retain the built-in
registrations for github.com and msft.ghe.com. Changing a hostname clears the
onboarding ID; reconnect requires the same host and registration. Refresh and
Credential Manager lookup use the saved ID, never another host's default. No
client secret is embedded and no existing CLI token
is read. GitHub's consent screen should identify GHCPSpendTray. The application was
registered with Device Flow and expiring tokens enabled, without a redirect URI.
Windows credential targets include the selected host's client ID; accounts from the old CLI
registration must reconnect rather than reusing tokens from that registration.

The user confirmed successful live sign-in and consumption/allocation display
in the portable github.com walkthrough on September 23, 2026. `read:user` is the
implemented starting scope, not a proven minimum; the exact granted scopes were
not independently inspected. `offline_access` is explicit opt-in. Enterprise
compatibility and live refresh rotation remain unverified. CLI repository scopes
are not added. SSO/approval failures are surfaced, not bypassed.

## Accounting and durability

- Version 1 conversion: 100 token-based AI credits = USD 1 **consumption value**,
  not an invoice. Calculations use `decimal`; round only when displaying.
- Required quota fields are nullable wire fields and missing values are rejected.
  Premium-request billing is unsupported. Missing/zero allocation or unlimited
  allocation produces no percentage or allocation alerts.
- Reset timestamps define billing periods when available. Only otherwise is a UTC
  calendar month used. `UsageAggregation.Total` excludes expired periods and labels
  partial/last-known totals. A complete total also requires fresh account state.
- Local observations remain available for retention and diagnostics, but sampled
  refreshes are not presented as a per-day consumption chart: the quota
  snapshot does not supply a verified daily breakdown.
- Configuration and alert ledgers use flush-to-disk, atomic promotion, and a recovery
  copy. Unsupported or damaged primary schemas recover visibly; two invalid copies
  fail rather than silently resetting account configuration or alert state.
- History uses SHA-256 account directories and UTC fetch-month JSONL files. Reads
  stream with size limits and explicit corruption diagnostics. Before appending,
  an invalid unterminated tail is quarantined; complete corrupt records remain
  available for diagnosis. Retention runs daily, not on UI refresh.
- A successful network sample that cannot be saved has `StorageError` and an explicit
  unsaved diagnostic. Successful prior observations are not discarded on later
  errors. Bounded logs contain only fixed diagnostic codes and hashed account keys,
  never raw exception bodies, OAuth data, or HTTP response content.

## Verification

From the repository root:

```powershell
dotnet run --project .\tests\GHCPSpendTray.Tests\GHCPSpendTray.Tests.csproj -c Release
dotnet publish .\tests\GHCPSpendTray.Tests\GHCPSpendTray.Tests.csproj -c Release -r win-x64 -p:PublishAot=true -o .\tests\GHCPSpendTray.Tests\artifacts\native-x64
.\tests\GHCPSpendTray.Tests\artifacts\native-x64\GHCPSpendTray.Tests.exe
```

The console harness uses fake HTTP, fake credentials, virtual time, and isolated
project-local test directories removed after each run. It neither discovers existing
credentials nor contacts live quota or OAuth endpoints.
