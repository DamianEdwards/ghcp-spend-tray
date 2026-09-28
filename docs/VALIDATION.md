# GHCPSpendTray validation and release gates

## macOS implementation evidence (September 28, 2026)

The native Mac frontend shares the C# controller, host-scoped OAuth, decimal
accounting, storage, scheduler and alerts with Windows, rather than
reimplementing them in Swift.

| Check | Evidence |
|---|---|
| Shared engine | 147 Core tests and 38 shared controller/bridge assertions pass under managed .NET and executed arm64 Native AOT |
| Native app | Universal arm64/x86_64 SwiftUI executable and C# Native AOT library build with warnings treated as errors; bundle versions, both slices, dependency paths and ad-hoc signatures checked |
| UI smoke | Empty/populated app launches, menu-bar creation, navigation/rendering of all five settings pages, onboarding sheet, exact synthetic $42.75 summary, cross-language settings save, and normal shutdown exercised on Apple silicon and the Intel slice under Rosetta |
| Keychain | Uniquely named synthetic device-local item create/read/update/delete, missing-item behavior and target isolation exercised using Security.framework; no real account credentials read |
| Swift formatting | Exact cents, invalid/negative amount rejection, unavailable-not-zero display and C# timestamp parsing checked |
| Automation | Platform-routing and separate-version tests pass; PowerShell Store/release tests pass with Mac releases excluded from Windows selection; workflows pass actionlint |
| Windows extraction | Windows platform adapter test project compiles on macOS; full WinUI build/execution requires Windows SDK executables and remains a Windows CI gate |
| Production distribution | Signing/notarization automation implemented; not executed locally without the owner's Developer ID/API credentials |

Reproduce with `bash tools/macos/verify.sh`. To exercise the Intel slice on
Apple silicon with Rosetta already installed:

```bash
python3 tools/macos/smoke-test.py artifacts/macos/GHCPSpendTray.app --arch x86_64
```

Smoke mode requires a unique absolute `--data-dir`, uses only synthetic
accounts, and disables authentication, notifications and login startup.
The smoke launcher uses a temporary directory and a bounded subprocess
lifetime, then deletes its synthetic data. Render snapshots demonstrate view
creation, not pixel-perfect layout or VoiceOver correctness; AppKit snapshots
can omit GPU-composited SwiftUI text. No screenshot or accessibility
permission is requested or changed by these tests.

### Required before macOS production acceptance

- Run both native architecture CI jobs; Rosetta is not Intel hardware evidence.
- Install the signed/notarized DMG on a clean standard-user Mac with Gatekeeper
  enabled, and update between two signed versions without losing settings,
  history, credentials or login preference.
- Complete real device sign-in, identity confirmation, reconnect, rotation and
  explicit credential removal with approved test accounts, on supported hosts.
- Confirm login startup remains opt-in/hidden, including approval-required and
  externally disabled states, and catch-up after sleep/network recovery.
- Exercise notification permission grant/denial, Focus suppression, threshold
  deduplication, and notification-click account navigation.
- Check light/dark appearance, small displays, menu-bar overflow, keyboard
  navigation, VoiceOver, and native sheet/window behavior interactively.
- Confirm uninstall cleanup guidance, residual Keychain items, and stable
  signing-team access across upgrades.

These outstanding OS/distribution checks are not established by synthetic
tests. Mac releases do not imply App Store review or submission.

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
| Production signing/release | v0.1.0 preview published successfully by Release run 36073933389; public immutable release, trusted timestamped MSIX signature, downloaded checksums, source metadata and GitHub attestations verified |
| First Store package | v0.1.0 rebuilt from immutable released source for x64/ARM64 using Partner Center identity; unsigned bundle, publisher display name and payload validation pass. The user confirmed the initial release was published in the Store |
| Store update tooling | Offline guards cover default latest non-prerelease release selection and explicit version overrides, immutable source/version integrity, release-note conversion, token acquisition failure, read-only access checks, omitted optional submission references, pending drafts, published-version ordering, listing preservation, JSON media type on writes, copied-package removal, upload failure and commit. Synthetic post-commit polling checks skipped states, known successes and failures, malformed/unknown responses, API errors and bounded timeout; a separate local status query has synthetic read-only and error-response checks. Read-only 0.2.0 Actions run 36278087270 verified OIDC and product identity without creating a submission. Publish run 36281125356 rejected draft creation without JSON media type; run 36282303754 created a draft but failed to update copied package entries. Run 36283943525 uploaded the package and received `CommitStarted`; a subsequent local read-only Store status query confirmed `Certification`. Live default release selection, Store approval and publication remain unverified |

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
- End-to-end Store Package and Publish Actions run after merge, including Partner
  Center acceptance of a new update and Store certification/publication.
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
