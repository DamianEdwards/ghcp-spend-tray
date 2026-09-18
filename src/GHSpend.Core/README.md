# GHSpend Core

The core targets .NET 11 and is Native AOT compatible. It has no package dependencies.
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

Onboarding is explicit: resolve host; use the fixed `GitHubOAuth.ClientId`;
`BeginAsync`; show user code and validated verification URL; `PollAsync`;
`GetIdentityAsync`; `FetchWithTokenAsync`; confirm immutable identity; save credentials
and settings; refresh. Account keys include canonical host and immutable numeric ID,
allowing multiple users on one host without double counting.
For reconnect, await `PauseAccountAsync(key)` before replacing the saved credential.
It cancels and drains in-flight work without changing persistent settings or credentials.
Then call `UpdateSettings` with the account still present to resume monitoring.

## Authentication gate

GHSpend uses the GitHub CLI OAuth app's public ID, `178c6fc778ccc68e1d6a`, through
`GitHubOAuth.ClientId`. It is not user-configurable; account/configuration models
do not persist client IDs. The lower-level device-flow protocol methods accept
an explicit ID for testability, but application onboarding and account refresh
always use the fixed ID. No client secret is embedded and no existing CLI token
is read. GitHub's consent screen identifies GitHub CLI rather than GHSpend.

`read:user` is still an **unverified starting scope**, not a proven minimum for
the undocumented Copilot endpoint. `offline_access` is explicit opt-in. New
device-flow tokens must be validated on github.com and an approved GHE.com tenant
before claiming production authentication support. CLI repository scopes are
not added. SSO/approval failures are surfaced, not bypassed.

## Accounting and durability

- Version 1 conversion: 100 token-based AI credits = USD 1 **consumption value**,
  not an invoice. Calculations use `decimal`; round only when displaying.
- Required quota fields are nullable wire fields and missing values are rejected.
  Premium-request billing is unsupported. Missing/zero allocation or unlimited
  allocation produces no percentage or allocation alerts.
- Reset timestamps define billing periods when available. Only otherwise is a UTC
  calendar month used. `UsageAggregation.Total` excludes expired periods and labels
  partial/last-known totals. A complete total also requires fresh account state.
- `HistoryAnalysis.Build` returns actual elapsed-time USD/hour observations, with
  explicit reset/gap/correction markers and a text-friendly observed increase.
  Its default maximum comparable gap is six hours; the UI can set a different
  explicit gap policy (for example for a configured daily poll).
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
dotnet run --project .\tests\GHSpend.Tests\GHSpend.Tests.csproj -c Release
dotnet publish .\tests\GHSpend.Tests\GHSpend.Tests.csproj -c Release -r win-x64 -p:PublishAot=true -o .\tests\GHSpend.Tests\artifacts\native-x64
.\tests\GHSpend.Tests\artifacts\native-x64\GHSpend.Tests.exe
```

The console harness uses fake HTTP, fake credentials, virtual time, and isolated
project-local test directories removed after each run. It neither discovers existing
credentials nor contacts live quota or OAuth endpoints.
