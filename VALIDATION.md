# GHCPSpendTray validation and release gates

## Local evidence (September 24, 2026)

| Check | Evidence |
|---|---|
| SDK | `global.json` pins .NET `10.0.401` |
| Solution | Renamed GHCPSpendTray Release build succeeds without warnings/errors |
| Core | 145 fixture tests pass under JIT and executed x64 Native AOT |
| Platform | 14 startup, path and IPC tests pass under JIT and executed x64 Native AOT |
| Application | 63 synthetic integration assertions pass under JIT and executed x64 Native AOT |
| Native publishing | x64 and ARM64 publish with AOT warnings treated as errors |
| Package creation | Windows SDK MakeAppx validates both MSIX manifests and creates a bundle |
| Bundle inspection | Identity/version, exact x64/ARM64 set, PE architecture, absent CLR header, required resources, full trust, and disabled startup declaration verified |
| Release tooling | Version boundary/rejection cases and PowerShell syntax pass; both workflows pass actionlint |
| Packaged launch | After the user enabled Developer Mode, x64 development-package activation passed populated and empty native UI scenarios, package-local storage, and initially disabled Windows StartupTask checks. The test registration was removed afterward; no login startup or certificate trust changes were made |
| Portable UI smoke | Populated and empty synthetic scenarios pass with the renamed Native AOT executable |
| Production signing/release | Not executed locally; requires configured protected environment and Azure OIDC authorization |

The platform suite checks explicit portable arguments, protected paths, private
portable-directory ACLs, junction rejection, singleton readiness/activation and
credential target validation. Its fake Windows startup store covers opt-in enable/
disable, unchanged settings, externally disabled state, policy-controlled states,
declined requests and writes not retained by Windows.

Domain/application fixtures use synthetic HTTP and credentials. They cover device
flow, host-scoped registrations, refresh, rate limits, schema failures, exact
decimal accounting, settings/history recovery, alert deduplication, account
removal and credential isolation. No live credentials or consumption data are
included in test fixtures.

## Reproduce

Run sequentially because builds share intermediates:

```powershell
.\tools\verify.ps1 -NativeTests
.\tools\package.ps1
.\tools\smoke-test.ps1
.\tools\smoke-test.ps1 -Empty
```

For an approved isolated development package registration on a machine where
Developer Mode is already enabled:

```powershell
powershell.exe -NoProfile -File .\tools\smoke-test-package.ps1
```

The package smoke script refuses to replace an existing development package.
It activates the app through its package identity with synthetic demo data,
checks package-local storage and the disabled Windows startup task, and runs both
populated/empty native UI smoke paths. It removes its registration in `finally`.
It does not change Developer Mode, certificate trust, or login startup preferences.

The portable smoke path checks hidden startup, tray mouse/keyboard callbacks,
duplicate activation, focus transitions, accessible settings/onboarding actions,
detail disclosures, synthetic notification submission and a uniquely named
synthetic Credential Manager write/read/delete. Shell accepting a notification
does not prove visual delivery. Synthetic callbacks do not reproduce all Explorer
foreground-permission behavior.

## Required before production acceptance

- Azure-signed bundle installation as a standard user on a clean Windows 11 system.
- Signed upgrade between two versions with unchanged package identity; settings,
  history, credentials and startup preference remain correct.
- Login startup remains opt-in and hidden; user-disabled and policy states remain
  respected across restart, update, and Settings activation.
- Uninstall/reset behavior for package-local data, and explicit account credential
  removal before uninstall. No generic Credential Manager cleanup is assumed.
- ARM64 execution on ARM64 Windows hardware (cross-publishing is not execution).
- End-to-end GitHub Actions, OIDC signing, attestation and draft publication with
  actual environment configuration. No public release was created by local work.
- Windows App Certification Kit, Partner Center identity, privacy/listing assets,
  full-trust capability review and Store submission acceptance.

## Authentication evidence and limitations

The user confirmed initial github.com device sign-in and consumption/allocation
display on September 23, 2026. That does not establish compatibility on every host
or packaged authentication/credential behavior.

The host-specific public registrations remain unchanged. Changing the app's code
name does not rename the OAuth applications in GitHub's administration UI; update
their display names/logos separately to GHCPSpendTray.

Enterprise authorization/consumption, live refresh rotation, portal comparisons
and the exact minimum granted scopes remain unverified. `read:user` is the
implemented starting scope, not a documented entitlement to
`/copilot_internal/user`; that endpoint is undocumented and can change.
Do not supply secrets or unredacted account/financial data for validation.
