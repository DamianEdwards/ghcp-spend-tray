# Verification and release acceptance

This document describes how to verify GHCPSpendTray, what automated checks can
establish, and what still requires platform or release acceptance.
It is not a changelog or a record of every successful test run.

Update it when verification procedures, coverage boundaries, enduring limitations,
or release acceptance gates materially change. Put per-change results, test counts,
screenshots, and investigation narratives in PR descriptions and CI artifacts.
Previous validation reports remain available in this file's Git history.
Use the relevant commit's CI results for its actual verification status; a command
listed here is not a claim that it has passed for the current checkout.

## Supported environments

| Platform | Verification requirements |
|---|---|
| Windows | Windows 11, the SDK pinned in `global.json`, Windows SDK, and Visual Studio C++ tools. Packaging requires both x64 and ARM64 native tools. |
| macOS | Native Apple silicon, the pinned .NET SDK, and stable Swift 6 Command Line Tools. CI/release toolchains are selected by `.github/actions/setup-macos`. Intel and Rosetta execution are unsupported. |

Support the latest patches of macOS 26 and 15, with a 15.0 deployment minimum.
Advance this two-major-version window with each adopted stable major release.
CI builds the arm64 app on macOS 26 and tests that exact artifact on macOS 15
without rebuilding. Local execution on one OS does not establish the other.
Cross-publishing Windows ARM64 likewise does not establish ARM64 execution.

## Run verification

### Windows

Run from the repository root. Build and publish commands share intermediates;
do not run them concurrently in the same checkout.

```powershell
.\tools\verify.ps1
```

This runs release-tooling checks, a Release solution build, and all four managed
.NET test harnesses. For interop, serialization, trimming, or other AOT-sensitive
changes, also run the harnesses as executed x64 Native AOT:

```powershell
.\tools\verify.ps1 -NativeTests
```

CI splits these checks into `-TestShard Application` and
`-TestShard CorePlatformShared` on separate runners. The default local command
retains the complete check set.

For packaging or real WinUI startup changes, publish and package sequentially,
then exercise both populated and empty portable smoke scenarios:

```powershell
.\tools\package.ps1
.\tools\smoke-test.ps1
.\tools\smoke-test.ps1 -Empty
```

Local packaging produces an **unsigned development bundle**, not a public release.
Use `-Store` on the packaging script when checking the Store deployment mode;
see [RELEASING.md](RELEASING.md) for runtime prerequisites and package validation.

#### Packaged startup

Portable smoke does not establish packaged startup. The final-identity
`resources.pri` must merge `GHCPSpendTray.pri`, retaining WinUI/Reactor startup
XAML as well as qualified shell icons. Separate PRI/XBF files being present is
not sufficient. Store builds resolve WinUI theme resources from the shared
Windows App Runtime; self-contained builds carry their runtime resources.

After resource-index or packaging changes, also test the actual package identity.
On an approved machine where Developer Mode is already enabled:

```powershell
powershell.exe -NoProfile -File .\tools\smoke-test-package.ps1
```

The default layout is `artifacts\msix\x64`; use `-Layout` for another development
layout, such as the extracted Store-mode package. The script refuses to replace
an existing development registration, runs populated/empty package activation,
checks package-local storage and the initially disabled StartupTask, and removes
its own registration in `finally`. It does not enable Developer Mode, change
certificate trust, or alter login startup preferences.

CI exercises the exact extracted x64 MSIX in both deployment modes, including
fresh process startup and repeated constrained smoke observations. A failed
observation stops the lane; repeated observations are not retries until green.
Each launch logs activation before calling Windows, bounds the isolated activation
helper to 15 seconds, and then allows 45 seconds for the app to exit. Failures
retain activation phases, helper errors, the returned process ID (when available),
and native smoke diagnostics per scenario. An activation timeout terminates the
helper and any app process from that test's exact development layout; it is not
retried automatically.

Release-tooling checks use in-memory process doubles in
`tools/test-package-smoke.ps1` to verify exact timeout arguments, failure handling,
diagnostic retention, and owned-process cleanup without launching helper processes,
sleeping, or asserting elapsed time. These deterministic checks do not establish
real process startup or COM activation; the packaged startup lanes above cover
that integration.

#### Interactive synthetic demo

To inspect the published app without accessing normal accounts or settings:

```powershell
.\tools\publish.ps1 -Runtime win-x64
$demo = Join-Path ([IO.Path]::GetTempPath()) ("GHCPSpendTray-demo-" + [guid]::NewGuid().ToString('N'))
.\artifacts\publish\win-x64\GHCPSpendTray.exe --demo --portable --data-dir $demo
```

Use `--demo-usage-states` instead of `--demo` for labeled examples of forecast
risk bands, current-usage warnings, estimates off/unavailable, unlimited usage,
custom budgets, and daily reports. The showcase uses fixed synthetic forecasts
for visual review, not live projections.

Use a separate absolute data directory for each verification profile. Exit the
demo before deleting its directory. Demo data is synthetic; this mode does not
establish live authentication or provider behavior.

Optional tray-renderer samples can be generated after building the app harness:

```powershell
dotnet run --project tests\GHCPSpendTray.AppTests -c Release --no-build -- --tray-samples C:\Temp\tray-samples.bmp
```

Append a pixel size such as `24` after the bitmap path to inspect another DPI.
These are renderer samples on light/dark backgrounds, not Explorer screenshots.

### macOS

Run on a supported Apple-silicon Mac, natively rather than under Rosetta:

```bash
bash tools/macos/verify.sh
```

This runs CI/release/Mac tooling tests, managed and executed arm64 Native AOT
Core/shared harnesses, the native app build, Swift platform tests, synthetic
Keychain CRUD, bundle/signature inspection, and populated/empty real-app UI smoke.
`--shared-tests` and `--app` select the separate CI lanes.

App and bridge Mach-O slices must be exactly arm64. Package checks also cover
deployment minimums, dependency paths, executable permissions, embedded Sparkle
helpers, and signatures. An ad-hoc development signature is not production
Developer ID signing, notarization, or Gatekeeper acceptance.

Smoke uses isolated synthetic data and a bounded process lifetime. It does not
authenticate real accounts, deliver notifications, request notification permission,
or change login startup. It does read native notification settings to exercise
the real completion-handler threading path. Render snapshots can omit
GPU-composited SwiftUI text and do not establish pixel-perfect layout or VoiceOver
correctness; screenshot/accessibility permissions are not changed by the tests.

### Read-only app shell commands

The native Windows/macOS executables dispatch `usage` and `prompt` before UI,
single-instance activation, telemetry or credential initialization. Exercise
the actual built executable with isolated synthetic app-owned snapshots:

```powershell
.\tools\prompt\verify.ps1 -AppExecutable .\artifacts\publish\win-x64\GHCPSpendTray.exe -Render
```

```bash
bash tools/prompt/verify.sh "$PWD/artifacts/macos/GHCPSpendTray.app/Contents/MacOS/GHCPSpendTray" --render
```

These checks cover stdout/stderr capture, exit codes, budget overrides,
tray-selected/explicit accounts, saved-state freshness and billing rollover,
forecast bands and adapter hook ownership. They do not fetch live data or start
the UI. Packaged Windows smoke additionally invokes the installed console
execution alias and its `init pwsh | Invoke-Expression` profile pipeline before UI activation.
App initialization output is executable shell code; cached usage output remains
data-only. Emitted-code tests check quoting, repeated initialization, and hook
restoration against the same scripts embedded as assembly resources.
PowerShell coverage includes native stdout piped directly into
`Invoke-Expression`, not only evaluating a captured whole script. Packaged
initialization stdout/stderr are retained on failures; usage JSON is excluded
from diagnostic artifacts because it can contain private consumption data.
The manifest permits separate query
processes; the app's own coordinator still enforces one GUI instance.
Do not treat an unpackaged executable test as proof of alias activation.
Do not treat WSL shell tests as native macOS execution evidence.

### CI and release tooling

For changes to platform routing or release orchestration:

```bash
python3 -m unittest discover -s tools/ci
python3 -m unittest discover -s tools/release
```

The aggregate **Verification** gate must remain active for documentation-only
changes. Platform routing, Markdown linting, native lanes, and their current
dependencies are defined in [verify.yml](../.github/workflows/verify.yml).
The gate rejects failures, cancellations, unexpected skips, and missing results.
Do not infer success for a skipped platform from another platform's passing job.

## Coverage boundaries

| Check | Establishes | Does not establish |
|---|---|---|
| Core and shared/controller/bridge harnesses | Synthetic provider responses, exact decimal accounting, persistence/recovery, scheduling, alerts, budgets/estimates, and source-generated bridge contracts. | Live host compatibility, provider billing semantics, or native UI behavior. |
| Windows platform/application harnesses | Portable path/ACL and singleton behavior, controller/UI contracts, synthetic credentials/HTTP, real HWND callbacks and HICON creation, resource ownership, and native theme policy. | Physical Explorer input, actual notification delivery, installed-package upgrades, or the complete display/theme matrix. |
| Windows UI smoke | Actual WinUI creation and controls, rendering/layout readiness, account/settings flows, focus transitions, tray callbacks, synthetic notifications, and isolated Credential Manager CRUD. | Full accessibility acceptance, all foreground-permission behavior, or live OAuth/Store deployment. |
| macOS native tests and UI smoke | Swift model/platform behavior, real Native AOT bridge interaction, Keychain isolation, popup attachment/resizing, settings drafts, and native close/Quit flows. | VoiceOver, real permission decisions, actual OS shutdown, production signing, or another OS generation unless its exact artifact is executed there. |
| Package and release tooling | Artifact architecture/resources, deployment contracts, signatures/metadata where configured, immutable-source selection, and synthetic release/Store failure handling. | Clean-machine installation, production distribution, Store certification, or execution on a cross-published architecture. |

All fixtures and issue reports must use synthetic accounts and data.
Never include tokens, device codes, signing keys, unredacted settings/history,
or real consumption details. Synthetic credential checks create and remove only
their own uniquely named entries; they must not inspect or clean unrelated items.

UI smoke waits for bounded, observable render/layout readiness before asserting.
Timeouts, missing results, and nonzero exits are failures, not successful skips.
Inspect retained phase/result files and redacted diagnostics rather than relaxing
assertions, adding retry-until-green behavior, or changing user preferences.
Shell acceptance of a notification is not proof that the user saw it.

Responsive-window probes synchronize against the bounds accepted by Windows,
not the requested outer size: monitor/DPI tracking limits can clamp oversized
requests. They must still assert both wide and narrow component layouts and
retain native/XAML sizes and rasterization scale in failure diagnostics.

## Known limitations and manual acceptance

### Windows desktop integration

Check these interactively with synthetic data on the relevant installed build:

- Tray mouse/keyboard activation, overflow and multiple icons; real Explorer
  restart and recovery after sleep, reconnect, and display changes.
- Mixed-DPI monitor/taskbar movement, light/dark modes, mixed Windows/app color
  modes, high contrast, and theme changes while the app remains running.
- Running and pinned taskbar icons, Start/search, fresh pins and upgrades at
  100%, 150%, and 200% scaling. Record stale Explorer caches separately; do not
  change artwork or delete user caches to make a check pass.
- With focus in a text box, Alt+Left and mouse XButton1 should perform Back while
  Backspace, arrows, text-editing shortcuts, and mouse Forward remain unchanged.
  Repeat after Settings recreation, and with dropdowns, text context menus, or
  navigation overlays open so Back does not navigate behind them.
- Keyboard navigation, screen-reader labels, constrained layouts, and notification
  suppression/click routing under real Windows focus and notification policies.
- Partial-data warning triangles, the separate over-limit badge, and complete
  first-account chart/details visibility in the work-area-bounded popup.

The tray menu follows the taskbar's `SystemUsesLightTheme` setting, not necessarily
the app color mode. Its native UxTheme menu APIs are undocumented ordinals; missing
exports produce a diagnostic and retain the default usable menu appearance.
High contrast remains Windows-controlled. Native policy tests are not a visual
check of the menu.

Windows controls tray overflow visibility and can suppress notifications.
Tooltips have a 127-character limit; Usage contains the untruncated account
inclusion details. Displayed percentages may take up to one minute to age out.

### Authentication and consumption

Synthetic device-flow tests do not establish every host's OAuth policy, token
rotation, minimum scopes, or consumption schema. Validate sign-in, automatic
identity verification/save, reconnect, refresh rotation, and explicit credential
removal with approved test accounts on the intended hosts.

`read:user` is the implemented starting scope, not a documented entitlement to
`/copilot_internal/user`. That consumption endpoint is undocumented and can
change. Enterprise-host behavior and portal comparisons require separate
acceptance; unsupported or stale consumption must be unavailable, not zero.

Optional enterprise daily reports additionally require the build-configured API,
supported enterprise membership, Azure CLI authentication, and corporate
consumption-read authorization. Fixtures do not establish live identity consent,
cross-enterprise coverage, ingestion completeness, or reporting lag. Missing
daily reports remain unavailable, not zero. Access tokens and reports stay in
memory; Azure CLI owns refresh credentials.

Period estimates assume UTC calendar months and a continuing average pace,
not a verified provider billing contract or an invoice. A supplied reset must
agree with the next month boundary. Estimates are unavailable before 24 elapsed
hours and qualified as early before 72 hours. Qualified daily data uses up to
seven recent complete reported UTC days; otherwise the period-to-date pace
remains the fallback. Estimated/derived bars do not enter that recent average.
Custom budgets change targets and projected excess, not observed or projected
usage amounts. Timestamp, freshness, rollover, and unknown/unlimited-allocation
fixtures do not establish forecast accuracy.

If Windows credential writes fail with error 8 (`ERROR_NOT_ENOUGH_MEMORY`),
Credential Manager may be at capacity even when RAM and disk are available.
The app records the operation category and native code in `logs\diagnostics.log`
without credential targets or account data. Remove only entries you recognize as
unused in **Control Panel > Credential Manager > Windows Credentials**.
The app does not delete other applications' credentials or use plaintext storage.

### Updates

**Microsoft Store:** native UI smoke holds a synthetic update open through
preparation, download, and installation progress before canceling consent. It
checks progress values, the disabled Update action, and the retryable canceled
state. These and the synthetic availability, restart, and failure tests do not
establish package replacement. Validate using an actual
Store-signed older version with a newer update offered to the same account/device.
Confirm the real consent dialogs, replacement, and new-version relaunch.
The supported update API does not supply the incoming version; GitHub's
independent release version is not a proxy.

**macOS Sparkle:** signed synthetic appcast fixtures exercise the real updater,
including rejection of invalid signatures and incompatible OS requirements.
For opt-in local replacement/relaunch coverage:

```bash
python3 tools/macos/rehearsal.py --signing-identity '<Developer ID identity>'
```

The rehearsal uses two builds of the real app, isolated identities/state,
ephemeral signing seeds, and a native loopback feed. It exercises replacement,
relaunch, preservation of synthetic settings/history/Keychain data, cancellation,
and failed downloads/signatures. Reports remain under `artifacts/macos-rehearsal`.
It does not publish a release or alter production updater configuration.

Rehearsal builds are not notarized and use an automated custom user driver rather
than standard update dialogs. Production HTTPS/Pages delivery, Gatekeeper,
quarantine/translocation, privileged or read-only installation paths, real
account/schema migration, macOS 15 replacement, and normal dialog interaction
remain distinct acceptance checks. See [RELEASING.md](RELEASING.md) for signing,
feed configuration, and immutable release assets. Never retain production
private seeds in reports or source.

### Telemetry

Synthetic Azure exporter and loopback OTLP tests check allowlisted tracked-event
fields, consent/UUID persistence, opt-out, redaction, and separation from diagnostic
logs/traces/metrics. They do not establish real-user delivery rates, active-user
totals, or production retention/deletion behavior.

Windows `--verify-telemetry` requires isolated portable demo mode and a build-time
ingestion configuration. Its events carry the
`GHCPSpendTray.TelemetryVerification` synthetic-source marker. Ordinary demo/smoke
tracked export remains disabled. Use only synthetic profiles and an explicitly
approved telemetry destination; local payload tests are not live ingestion proof.

## Production acceptance gates

Passing automated checks does not by itself establish production acceptance.
Use [RELEASING.md](RELEASING.md) for release procedures and record each release's
results with its PR, workflow run, or release evidence rather than in this file.

### Windows distribution

- Install the Azure-signed bundle as a standard user on a clean Windows 11 system.
- Upgrade between signed versions with unchanged package identity; preserve
  settings, history, credentials, and startup preference.
- Execute on ARM64 Windows hardware, not just cross-publish.
- Confirm login startup remains opt-in and hidden, including externally disabled
  and policy-controlled states, restart, update, and Settings activation.
- Verify uninstall/reset behavior for package-local data and explicit account
  credential removal. Do not assume uninstall removes generic Credential Manager
  entries or revokes OAuth grants.
- Complete the actual Store Package/Publish flow, newer-version update acceptance,
  Windows App Certification Kit checks, listing/privacy/full-trust review, and
  Partner Center certification/publication for the intended distribution.

### macOS distribution

- Execute the same arm64 artifact on both supported OS generations.
- Install the signed/notarized DMG on a clean standard-user Mac with Gatekeeper
  enabled; update between signed versions without losing settings/history,
  credentials, login preferences, or stable signing-team Keychain access.
- Verify manual and opt-in automatic updates, standard Sparkle dialogs, declined
  authorization, unavailable feeds, and installation-path restrictions.
- Verify login startup opt-in/hidden behavior, approval-required and externally
  disabled states, and catch-up after sleep/network recovery.
- Exercise notification grant/denial, Focus suppression, threshold deduplication,
  and notification-click account navigation.
- Check light/dark appearance, small displays, menu-bar overflow, keyboard
  navigation, VoiceOver, and native sheet/window behavior. Include dirty Settings
  close/Quit with Save, Discard, Keep Editing, invalid input, and failed saves.
- Confirm uninstall guidance for residual application-support data and Keychain
  items. Mac releases do not imply App Store review or submission.

## Repository verification controls

[codeql.yml](../.github/workflows/codeql.yml) scans Actions, C#,
JavaScript/TypeScript, Python, and Swift weekly on Wednesday at 10:37 UTC and on
manual dispatch, not on every PR/push. Use **Actions > CodeQL > Run workflow**
for an additional scan. It is not the pre-merge Verification gate.

Keep advanced setup rather than simultaneous default setup. Swift extraction
must observe the actual command-line frontend compilation; the Native AOT bridge
is built before tracing. `Bridge.h` contains declarations, not standalone C/C++
translation units. Do not suppress extraction failures or disable Swift scanning
to make the workflow pass; add C/C++ analysis if native implementation sources
are introduced.

Keep dependency graph/submission, Dependabot alerts/security updates, secret
scanning, push protection, and private vulnerability reporting enabled.
[dependabot.yml](../.github/dependabot.yml) covers NuGet, the pinned .NET SDK,
Actions, and npm tooling. Xcode/Apple SDK pins require manual updates and Mac
verification; add Swift package coverage if external Swift packages are introduced.
Preserve read-only default Actions permissions, full-SHA action pins, and the
required aggregate Verification branch check.
