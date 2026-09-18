# GHSpend validation and release gates

Implementation and local verification completed on September 17, 2026.
This is a development build, **not a production-authentication or clean-machine
certification**. The preserved implementation plan describes intended acceptance,
not completed evidence.

## Verified locally

| Check | Evidence |
|---|---|
| Exact SDK | `global.json`: `11.0.100-rc.1.26425.128`, no roll-forward |
| Solution build | Release build, zero warnings and errors |
| Core fixtures | **125 passed**, under both JIT and executed x64 Native AOT |
| Platform fixtures | **24 passed**, under both JIT and executed x64 Native AOT |
| Application integration | **37 assertions passed**, under both JIT and executed x64 Native AOT |
| Production native publishing | `win-x64` and `win-arm64` both published successfully, AOT warnings treated as errors |
| Native executable format | PE machine checked against x64/ARM64; CLR runtime header absent |
| x64 GUI integration | Published executable ran in an isolated portable directory |
| Native UI smoke | Window/message loop, list selection, above-100% text, clamped native progress, graph, settings and onboarding controls |
| GUI resource lifecycle | 50 cycles creating/disposing three dialog types (150 dialogs), with GDI/USER resource counts checked |
| Windows notification | Shell accepted a synthetic test notification; visual delivery is not claimed |
| Credential Manager | A unique `GHSpend/test/<random>` synthetic credential was written, read, deleted, and checked absent |
| Startup isolation | Real GHSpend HKCU Run value compared before/after smoke testing and unchanged |
| Installation isolation | Only portable mode and isolated/fake install tests were used; no real first-run installation |

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
the fixed GitHub CLI client ID for device requests and refresh on all hosts,
ignored legacy client-ID overrides, duplicate/wrong-account/declined-identity handling, account
settings/removal, notification deduplication, and absence of synthetic tokens
from files. All HTTP responses and account credentials are synthetic.

Reproduce:

```powershell
.\tools\verify.ps1 -NativeTests
.\tools\publish.ps1
.\tools\smoke-test.ps1
```

Build and test commands share intermediate project directories; run these
sequentially, not concurrent publishes of the same projects. The smoke script
removes its unique temporary data directory on success; `-KeepData` retains it.
Failures retain their isolated directory for inspection.

## Pending: live OAuth compatibility

At the user's request, the original app-owned registration design was replaced
with the fixed GitHub CLI public client ID `178c6fc778ccc68e1d6a`, verified against
[cli/cli's authentication source](https://github.com/cli/cli/blob/trunk/internal/authflow/flow.go).
Registration/client-ID input is no longer a blocker or a user setting. The consent
screen names **GitHub CLI**, and onboarding explicitly says so.

**No live OAuth or consumption request was performed during implementation.**
No existing credentials from another application were discovered or borrowed,
and no client secret was included. Earlier successful probes in the plan do not
prove that fresh device-flow tokens with the currently requested scopes work.

Remaining input is normal browser consent for the intended github.com and GHE.com
test identities, plus required enterprise/EMU app approval. Do not supply client
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

## Other release acceptance still pending

- **ARM64 execution on ARM64 Windows hardware.** The ARM64 binary and test
  harnesses cross-publish; they have not executed on ARM64 here.
- **Clean standard-user Windows VM installation/login test**, including actual
  downloaded-file handoff, SmartScreen behavior, deletion of the original
  executable after readiness, login startup, externally disabled startup,
  failed startup/launch, Unicode paths, and repeated launch. Do not run this
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
  license and third-party notices when packaging a public release. No third-party
  NuGet application packages or GitHub logo assets are used.

## Current behavior and limits

- Shell notifications report submission, not guaranteed delivery, persistent
  Notification Center history, or activation after exit.
- Installation and startup are per-user. Portable test instances have independent
  mutex/pipe, credential-target, and tray-GUID scopes and cannot toggle startup.
- Removing an account deletes its credential and monitoring configuration.
  Historical observations remain subject to retention; local removal is not
  remote OAuth revocation.
- The sparkline's default long-gap cutoff is six hours. It shows observed
  intervals, not exact server-side timing. At very long polling intervals the
  24-hour display may legitimately have insufficient comparable history.
- `/copilot_internal/user` is undocumented. Schema/capability failures are
  explicit errors, never zero-dollar success.
