# GHSpend validation and release gates

Initial implementation and local verification completed on September 17, 2026;
project-owned OAuth integration and user-reported live verification followed on September 23.
This is a development build, **not a production-authentication or clean-machine
certification**. The preserved implementation plan describes intended acceptance,
not completed evidence.

## Verified locally

| Check | Evidence |
|---|---|
| Exact SDK | `global.json`: `10.0.401`, no roll-forward (user-approved migration) |
| Solution build | Release build, zero warnings and errors |
| Core fixtures | **145 passed**, under both JIT and executed x64 Native AOT |
| Platform fixtures | **24 passed**, under both JIT and executed x64 Native AOT |
| Application integration | **63 assertions passed**, under both JIT and executed x64 Native AOT |
| Production native publishing | `win-x64` and `win-arm64` both published successfully, AOT warnings treated as errors |
| ZIP distribution | Extracted x64 ZIP passed the empty-state/accessible-action native smoke test; project license and available dependency notices are included |
| Native executable format | PE machine checked against x64/ARM64; CLR runtime header absent |
| x64 GUI integration | Published executable ran in an isolated portable directory |
| Reactor UI smoke | Hidden startup, populated cost flyout, gear/empty-state button invocation through accessibility peers, settings navigation and account-onboarding deep link |
| Tray activation regression | Synthetic version-4 mouse/keyboard callbacks through the Shell HWND, duplicate selections, repeated hide/reopen, and Settings/flyout activation ordering checked against native window visibility |
| Account details disclosure | Compact summary, initially collapsed history/diagnostic expanders, accessible expansion revealing labeled raw credits; warning mapping tested independently |
| Visual inspection | Published x64 Reactor flyout inspected at 125% DPI, including the rendered cost, allocation meter and custom graph |
| Windows notification | Shell accepted a synthetic test notification; visual delivery is not claimed |
| Credential Manager | A unique `GHSpend/test/<random>` synthetic credential was written, read, deleted, and checked absent |
| Startup isolation | Real GHSpend HKCU Run value compared before/after smoke testing and unchanged |
| Installation isolation | ZIP build runs in place; portable tests never register startup or self-install |

## Reactor migration evidence

### Tray activation correction (September 24, 2026)

The user reported intermittent left-click failure while the tray menu's Open
command worked. The previous handler toggled using Reactor's cached `IsVisible`.
In the pinned Reactor source, `OnNativeActivated` sets that cache to true even
for deactivation, so it is not a reliable toggle predicate after hiding.
The flyout also synchronously hid itself from `Deactivated`, allowing focus
transitions during presentation to undo the open request.

Tray selection now queues an idempotent open operation. Focus-loss dismissal
is deferred, checks the actual foreground HWND, and ignores callbacks belonging
to a superseded presentation. Foreground activation is explicitly requested;
Windows may refuse it, which produces a redacted diagnostic rather than a
focus-stealing workaround. Repeated tray activation leaves the flyout open;
outside focus changes and Escape remain dismissal mechanisms.

The smoke harness sends `NIN_SELECT` and `NIN_KEYSELECT` to its own Shell HWND
with version-4 icon-ID packing instead of calling only `ShowFlyout` directly.
It checks native visibility after duplicate activation, hidden-window reopening,
opening from Settings, and a queued deactivation followed by a newer open.
Synthetic callbacks do not grant Explorer's foreground permission, so these
checks do not claim to reproduce every real Explorer/taskbar input sequence.

The application uses `Microsoft.UI.Reactor` `0.1.0-preview.16` with the UI components
from `Microsoft.WindowsAppSDK` `2.5.1`, the latest stable NuGet release checked on
September 23, 2026. The initial migration used `2.2.0`; the upgrade to `2.5.1`
passed all JIT/x64 Native AOT suites, clean x64/ARM64 native publishing, and
populated/empty Reactor UI smoke tests, including Advanced details expansion.
Old publish folders were removed before publishing so outdated runtime DLLs
could not mask packaging problems. No warning suppressions or application
compatibility workarounds were needed for this package upgrade.

### Component-only distribution

The umbrella package was then replaced with direct WinUI `2.3.9`, Interactive
Experiences `2.1.9`, and DWrite `2.1.0` references, preserving the component
versions from SDK `2.5.1`. Base `2.0.4` and Foundation `2.3.12` are transitive.
AI, ML, Search, Widgets, and the umbrella Runtime package are no longer in the
resolved dependency graph. The SDK's self-contained build collects the referenced
components' payloads and registration fragments; files are not pruned after
publishing to simulate a smaller dependency graph.

Both architectures were clean-published and all suites and x64 native UI smoke
tests passed again. The headless application test harness disables its own
duplicate WinAppSDK module initializer: the referenced application executable
already supplies that initializer. Production initialization remains enabled.
Publish-time guards reject reintroduced optional packages or their DLL payload.
The extracted slim x64 ZIP passed both populated and empty-state UI smoke tests.
Process-module inspection confirmed `Microsoft.UI.Xaml.dll` and
`Microsoft.WindowsAppRuntime.dll` loaded from that extracted folder, not an
installed Windows App Runtime.

Measured on September 23, 2026 (MiB; runtime payload excludes debug symbols):

| Architecture | Payload before | Payload after | ZIP before | ZIP after |
|---|---:|---:|---:|---:|
| x64 | 131.83 | 75.88 | 52.13 | 29.45 |
| ARM64 | 138.10 | 81.04 | 50.69 | 28.66 |

Both ZIPs are about **43.5% smaller**. The 86 locale folders remain intact.
PDBs are still generated but moved to `artifacts\symbols\<runtime>`; their
relocation is not counted as runtime-payload savings in this table.

### Original Native AOT compatibility investigation

During the initial migration, an isolated .NET 11 RC1
Native AOT proof failed during IL scanning; the compiler's `--noscan` workaround
published but failed at WinRT startup. Aligning WinAppSDK versions did not fix
that path. A .NET 10 Native AOT proof displayed a Reactor window and exited
successfully after including the documented WindowsAppSDK#6394 resource-copy
workaround. The user approved the .NET 10 migration; no scanner or warning
suppression workaround is used in production.

Self-contained ZIPs include the Windows App SDK DLLs, PRI/XBF resources, image
assets, the project license, and the resolved packages' available license/notice
files and NuGet metadata. The user approved running from the extracted folder rather than
self-installing. The root bootstrap now only secures the data directory and
coordinates the per-user singleton. Legacy installer transaction tests still
exercise the retained, inactive helper code; they are not the ZIP launch path.

Ten new domain cases cover exact USD milestones, jump coalescing, restart and
correction deduplication, billing resets, unknown/unlimited allocations,
combined percentage/dollar notifications, per-account overrides, increment
changes, failed submissions, legacy state compatibility, and decimal precision.
Controller fixtures also verify settings persistence and actual notification
submission paths. Geometry fixtures cover normal and negative-coordinate
monitors and a top-edge fallback. Mixed-DPI positioning uses Shell icon bounds
and monitor work areas in physical pixels rather than approximate global DIPs.

The core suite covers exact decimal accounting, missing/invalid fields,
non-token billing, exhausted/unlimited allocations, host routing, immutable
identities, malicious verification origins, slow-down/expiry/cancellation,
secretless refresh rotation, credential isolation, 401/403/404/rate limits,
atomic settings/ledger recovery, truncated and corrupt history, retention,
sample-rate graphs, period boundaries, alert coalescing/deduplication/retries,
single-flight polling, concurrency limits, sleep/network recovery, interval
changes, account quiescence, and visible storage failures.

The platform suite uses fake startup and installer stores for failure injection.
It also tests real native copying/Mark of the Web, private ACLs, junction rejection,
singleton activation, and process-verified readiness in isolated locations.
It does not run a real downloaded-to-installed shell handoff.

Application fixtures exercise device-flow onboarding, confirmed identities, two
accounts on github.com plus a separate enterprise account, consumption totals,
host-specific project-owned IDs for device requests and refresh, enterprise reconnect,
registration-scoped credentials, rejection of unregistered hosts before network access,
ignored legacy client-ID overrides, duplicate/wrong-account/declined-identity handling, account
settings/removal, notification deduplication, and absence of synthetic tokens
from files. All HTTP responses and account credentials are synthetic.

Reproduce:

```powershell
.\tools\verify.ps1 -NativeTests
.\tools\publish.ps1
.\tools\smoke-test.ps1
.\tools\smoke-test.ps1 -Empty
```

Build and test commands share intermediate project directories; run these
sequentially, not concurrent publishes of the same projects. The smoke script
removes its unique temporary data directory on success; `-KeepData` retains it.
Failures retain their isolated directory for inspection.

## Live OAuth: initial user-confirmed success; broader compatibility pending

On September 23, 2026, the user registered the project-owned **GHCPSpend** OAuth
application and supplied public client ID `Ov23ctzkXY5CJhfKQo7T`. This replaces
the earlier GitHub CLI client ID. The registration has Device Flow and expiring
access tokens enabled and was accepted without any redirect URI. Onboarding
identifies GHCPSpend, and there remains no user-configurable client-ID field.
Credential targets are registration-specific; existing accounts must reconnect.

**User-reported result, September 23, 2026:** after following the portable
github.com sign-in walkthrough with the rebuilt application, the user explicitly
confirmed that sign-in completed and consumption/allocation appeared. This
establishes initial project-owned device-flow and quota-access compatibility
for that tested setup, not a guarantee for every host or account.

The agent did not inspect account data, credentials, or live response bodies.
No account identifiers, consumption amounts, tokens, or device codes are recorded
in this evidence. Portal-value comparison, the exact granted scopes, and live
refresh-token rotation were not independently verified.

Remaining checks include additional github.com identities and GHE.com
test identities, plus required enterprise/EMU app approval. A separate registration
has now been supplied and wired for the enterprise host, as described below;
its live authorization and consumption access have not yet been confirmed. Do not supply client
secrets or paste access/refresh tokens. `read:user` remains the implemented starting
scope, not an established minimum for the undocumented quota endpoint;
`offline_access` is optional. The CLI's repository/organization/gist scopes are
not requested automatically.

For each approved host, complete device authorization, verify `/user`, then verify
`/copilot_internal/user` with the newly issued token. Confirm token-based
consumption/entitlement precision against the corresponding portal without
placing real payloads, account data, or tokens in source/logs. Test optional
refresh rotation and reauthentication; record the minimal successful scopes
and policy approvals without storing secrets. A host/policy denial remains a
visible release blocker; there is no alternate client-ID setting or scope escalation.

### Enterprise device authorization blocked

On September 23, the user reported that an enterprise-host attempt failed
immediately after Start device sign-in, before any device code was displayed.
The former generic capability error maps to HTTP 404 or 501; the precise status
was not captured. Given this stage, the failure was in `POST /login/device/code`,
not `/user` or `/copilot_internal/user`. No conclusion about Copilot quota
availability follows from this failure.

The client ID used by that failed attempt belonged to the github.com registration. GitHub's
[enterprise OAuth integration guidance](https://github.com/github/github-mcp-server/blob/main/docs/oauth-login.md#github-enterprise-server-and-ghecom)
calls for an app registered on the target GHE.com host. An absent/incompatible
host registration or policy restriction is the likely blocker, not yet
independently confirmed.

The user subsequently registered GHCPSpend on `msft.ghe.com` and supplied public
client ID `Ov23ox38SoD1bIpzU9zZ`. The implementation now chooses that ID for this
exact normalized host in device authorization, token polling, refresh, and
Credential Manager targets. github.com retains `Ov23ctzkXY5CJhfKQo7T` and its
existing credential targets. Hosts without a built-in registration fail visibly
before an OAuth request is sent. No client secret or existing user token was
supplied or read. Live enterprise sign-in must still be retried by the user.

HTTP 404/501 diagnostics now identify device authorization, token exchange,
token refresh, identity lookup, or consumption lookup explicitly. They preserve
the HTTP status without exposing response bodies, credentials, or device codes.
Synthetic tests cover both statuses at all five stages.

## Other release acceptance still pending

- **ARM64 execution on ARM64 Windows hardware.** The ARM64 binary and test
  harnesses cross-publish; they have not executed on ARM64 here.
- **Clean standard-user Windows ZIP/login test**, including extraction,
  SmartScreen behavior, opt-in login startup, externally disabled startup,
  moved application folders, Unicode paths, and repeated launch. Do not run this
  on the development user's real profile without approval.
- **No-.NET-runtime clean-machine execution.** Native PE format is verified;
  absence of runtime prerequisites still needs the clean-machine check.
- **Interactive Explorer restart, mixed-DPI/high-contrast and screen-reader
  acceptance**, keyboard-only flows, and notification click routing. Native
  controls, tab/dialog navigation, DPI/font updates and tray recovery are
  implemented, but these broader OS/user tests are not all exercised by the
  automated smoke test.
- **Multi-day sleep/resume and memory/handle soak with authorized accounts.**
  Short native resource tests and simulated scheduler cases are not a substitute.
- **Signing and binary-distribution notices.** Binaries are unsigned; no
  signing identity was supplied. GHSpend source is licensed under MIT; see
  [LICENSE](LICENSE). Native AOT includes .NET components; include their applicable MIT
  license and third-party notices when packaging a public release. Reactor and
  Windows App SDK are pinned NuGet dependencies; no GitHub logo assets are used.

## Current behavior and limits

- Shell notifications report submission, not guaranteed delivery, persistent
  Notification Center history, or activation after exit.
- Data and startup preferences are per-user. Portable test instances have independent
  mutex/pipe, credential-target, and tray-GUID scopes and cannot toggle startup.
- Removing an account deletes its credential and monitoring configuration.
  Historical observations remain subject to retention; local removal is not
  remote OAuth revocation.
- The sparkline's default long-gap cutoff is six hours. It shows observed
  intervals, not exact server-side timing. At very long polling intervals the
  24-hour display may legitimately have insufficient comparable history.
- `/copilot_internal/user` is undocumented. Schema/capability failures are
  explicit errors, never zero-dollar success.
