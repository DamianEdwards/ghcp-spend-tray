# GHCPSpendTray validation and release gates

## Native macOS budgets and protected settings drafts (October 9, 2026)

The Mac frontend now edits per-account custom USD tracking budgets and explicitly
resets them to API allocation. Source-generated command/preferences contracts are
additive: omitted or false `updateCustomBudget` preserves an override; a null
budget with true clears it. Native usage and estimate labels identify the
effective target while Advanced Details retain raw API allocation and percentage.
Draft preview arithmetic and form validation run in the Native AOT bridge using
the shared budget and threshold rules, not a separate Swift calculation.

The consolidated implementation retains the native Quit approach and additional
coverage contributed by [Sébastien Ros in PR #79](https://github.com/DamianEdwards/ghcp-spend-tray/pull/79),
while preserving PR #80's bridge-side validation and native stack 81.
Quit cancels the initial AppKit termination request and retries only after the
draft is saved or discarded, keeping the bridge's default-mode polling timer
running. The new real-app Quit smoke reproduced a 90-second timeout with the
previous deferred-termination implementation before this fix was applied.

`bash tools/macos/verify.sh` passed locally on Apple silicon/macOS 26.7.1 using
the pinned .NET 10.0.401 SDK, stable Swift 6.4 Command Line Tools, and the selected
stable macOS 26 SDK. The consolidated run includes 179 Core tests and 170 shared/controller/
bridge assertions in both managed and executed arm64 Native AOT, plus native
Swift tests, synthetic Keychain CRUD, arm64-only bundle/signature checks, and
populated/empty real-app UI smoke runs. The CI tooling suite retains two existing
opt-in skips; they are not claimed as passes.

Focused coverage checks budget set/omission/false/clear, unrelated account and
global saves, unchanged diagnostics, weighted menu-bar percentages, estimates,
and draft previews against both finite and unlimited API allocations. Native
fixtures cover custom-budget labels with unlimited or unknown API allocation,
shared allowed input values, all-invalid-field feedback,
override error clearing, exact failed drafts, clean-on-revert behavior, inherited
overrides/toggles, and dirty refresh protection. A native AppKit field editor
remains the same object and keeps focus and selection as inline errors appear
and disappear.
Immediate bridge receipt failures and cancelled saves retain the exact draft.
SwiftUI bounds preferences measure both footer buttons inside the constrained
form, including validation-message insertion.
Synthetic clicks exercise both fixed footer actions in scrolled account, General,
and Notifications forms constrained to 500 by 400 points.

Real native sheet buttons exercise Save, Discard, and Keep Editing, including
invalid Save and synthetic persistence failure. Model coverage guards Back,
sidebar/account switches, Add/Reconnect, close/Quit continuations, and navigation
during asynchronous saves. Explicit system logout/restart/shutdown Apple Event
reasons bypass interactive prompts; no actual OS shutdown was initiated. The
real-app smoke saves and clears budgets through the C ABI, rejects invalid drafts,
preserves a budget across an unrelated save, and renders account budget preferences
at the minimum Settings window size. Real Settings close and Quit use native sheet
buttons: Save completes before navigation/close, invalid Save and Keep Editing
cancel Quit, and Discard restores the draft before closing. Final smoke success
is recorded only during actual app termination after a valid Save-on-Quit has
completed with a clean baseline.

The original synthetic native form screenshots from PR #79 are retained with
their contributor's credit: [custom budget](images/macos-budget-preferences/custom-budget.png),
[constrained account](images/macos-budget-preferences/account-constrained.png),
[General](images/macos-budget-preferences/general-constrained.png), and
[Notifications](images/macos-budget-preferences/notifications-constrained.png).
They document the original form revision, not the exact consolidated layout;
current interaction and geometry assertions exercise the rebuilt app and views.

All new examples are synthetic and isolated. No real account credentials,
consumption data, login-item changes, or production signing/notarization
credentials were used. Windows execution, macOS 15 runtime coverage, VoiceOver
interaction, and actual OS shutdown remain separate checks; this local run does
not claim them.

## Local real-app Sparkle replacement rehearsal (October 9, 2026)

`python3 tools/macos/rehearsal.py --signing-identity '<Developer ID identity>'`
now runs a repeatable, opt-in update exercise with two different builds of the
actual SwiftUI/AppKit app and Native AOT bridge. All embedded code is arm64,
Developer ID signed with hardened runtime, and timestamped. The local runner
uses disposable bundle identities, ephemeral Ed25519 keys, authenticated feeds
and signed DMGs, and downloads solely through a native loopback listener.
It neither publishes a release nor changes production updater configuration.

Local macOS 26 / Apple-silicon execution passes eight real-engine scenarios:
manual download/install/relaunch, background download followed by the existing
Install and Relaunch action, install on quit, cancel download, cancel prepared
installation, corrupted archive, archive signed by a wrong key, and interrupted
download. Each successful case checks exact candidate file hashes, executable
permissions/framework symlinks, a new process at the same installation path,
preservation of synthetic external settings/history files and a device-local
Keychain item, and native update-check/download preferences. The installed B
then rejects an authenticated older A feed without replacing itself. Negative
cases require signature-validation/download error codes or explicit
cancellation and an unchanged, correctly signed, runnable A. Reports and
callback traces remain under `artifacts/macos-rehearsal/<run-id>`.

The rehearsal initially exposed two fixture assumptions: ad-hoc A/B binaries
do not have stable cross-version Keychain trust, and background-install
readiness can temporarily toggle during Sparkle's installer-status probe.
Developer ID signing resolves the former; the latter now waits on actual
readiness notifications and rechecks state at dispatch, rather than delaying
or treating a transient ready sample as final. Neither required relaxing
production signature validation or increasing deadlines.

Automation uses a supported custom Sparkle user driver in the real app; normal
builds retain the standard controller/dialogs. Rehearsal code lives outside the
shipping Swift glob, requires explicit development-only compiler flags, is
rejected by production package validation, and removes only its unique
synthetic Keychain/defaults data. The ordinary development bundle is rebuilt
after the runner finishes. Private seeds are never retained in evidence.

This is strong evidence for the actual downloader, helpers, replacement and
relaunch, **not** a full production acceptance result: these builds are not
notarized, use local HTTP, have unique synthetic identities/state, and automate
choices instead of clicking the standard UI. Notarization/quarantine/Gatekeeper,
privileged/read-only install paths, normal update-dialog interaction, macOS 15
full replacement, real account/schema migration, and public Pages/HTTPS delivery
remain distinct gates. Production Sparkle signing keys, notarization credentials,
tags and public assets were not used for the rehearsal.

## Microsoft Store update detection and installation (October 9, 2026)

Synthetic Windows tests cover periodic/throttled checks, unavailable vs current
status, notification deduplication, About's no-button/current and Update/available
states, download/install progress, consent cancellation, actionable errors,
completion/restart fallback, concurrent operation suppression and disposal.
Update notifications use distinct download artwork, with native icon creation,
DPI, contrast tinting and unchanged About-click routing covered.
Native HWND tests accept Restart Manager shutdown and ignore canceled shutdown.
The source-generated restart registration/cleanup calls are exercised without
initiating a deployment or restarting the test process.

Local Release verification passed all four managed and executed x64 Native AOT
harnesses, and the x64 Native AOT application passed populated and empty-account
UI smoke runs. Those smoke runs inject synthetic Store availability, invoke the
actual About Update button, check consent-cancellation retry, open About through
both the native update-notification callback (including a closed Settings window)
and the in-app update notice, and verify that clearing availability removes the button.
They do not call real Store update APIs.

One-off offline probes additionally passed 37 assertions against the unchanged
production Store service source with controlled Store/dispatcher endpoints and
real Windows restart registration. These cover Store-only gating, main-package
selection, the actual uptime delay, UI-thread ownership, registration before
consent, progress, canceled/failed installs, shutdown cancellation and retained
registration after completion.

A separate x64 Native AOT probe built from the production app project activated
the real StoreContext and initialized it with the production About window HWND
without querying or downloading. With About open and a synthetic install still
pending, ordinary native close was blocked, while actual Restart Manager shutdown
and restart succeeded. Windows App SDK restart also succeeded. Both produced a
different process, accepted `--startup`, reacquired the production single-instance
mutex and initialized the production shell. These isolated probes use synthetic
data and a test entrypoint, not the packaged production bootstrap; they do not
establish package replacement or execution of a newer version. No persistent CI
upgrade test, package registration, certificate or Developer Mode change was added.

The documented Store flow can terminate a desktop app before its install await
returns. Registration therefore precedes the install request, with a 61-second
process-uptime guard for the documented restart minimum. Windows owns termination,
package replacement and relaunch. A surviving completed call requests Windows App
SDK restart; an unsuccessful request is visible and retryable.

An actual Store-signed, older package with a newer update offered to the same
account/device is required to validate the real consent dialogs, replacement
and new-version relaunch. Portable/development package smoke and fake Store
results do not establish that end-to-end behavior. No real Store deployment has
been performed locally for this change. The incoming version is not supplied by
the supported Store update API; the UI deliberately does not invent it or use
GitHub's independently published version as a proxy.

## Windows tray recovery after wake/reconnect (October 8, 2026)

Runtime tray updates and `TaskbarCreated` recovery retry temporary Windows
Shell failures on the hidden window's message loop, at one-second intervals
with at most five retries. Pending dashboard and display changes coalesce
into the latest presentation without resetting that budget. Exhaustion is
logged and shown through the existing application error surface; startup
registration and unrelated callback failures still fail explicitly.
Disposal cancels the recovery timer. Notification submission is not replayed.

A rejected modify/re-add no longer allows the unchanged-render cache to skip
a missing registration when the dashboard returns to its previous values.
Synthetic HWND tests cover transient restart recovery, coalescing, reverted
presentations, version-four callbacks, bounded persistent failure, later
recovery and disposal. Failure diagnostics include the callback message ID
and native error code where available, never exception messages or account
data. These synthetic cases do not establish behavior for every real
Explorer restart or monitor/network transition.

## Windows tray selection and smoke readiness (October 5, 2026)

Windows tray selection no longer waits for the system double-click interval.
The version-four Shell callback handles `NIN_SELECT` and `NIN_KEYSELECT` once,
ignoring raw mouse down/up/double-click messages. Rapid selections remain
ordinary popup toggles (or account navigation for per-account icons), not a
settings shortcut. Settings remains available from the popup gear and the
right-click menu. Notification routing, retired callback filtering, taskbar
recovery and icon ownership are unchanged.

The previous timer arbitration could reopen the popup after double-clicking:
synchronous cold settings-window creation could exceed the double-click
interval before the trailing selection arrived. Removing that arbitration
eliminates the clock-dependent path rather than lengthening its timeout.
Native HWND regression tests send full raw/semantic callback sequences,
including the old trailing timer, and assert immediate dispatch, no
double-click settings action, host-specific routing and retired-ID rejection.

Synthetic UI smoke waits for the accessible button's actual `Click` event,
controller completion, the current committed Reactor revision, loaded
descendants and layout. Probes run at low dispatcher priority after ordinary
UI work. Correctness assertions then execute once; missing controls, incorrect
values and accessibility/layout regressions still fail. Readiness waits use a
five-second bound and record named checkpoints plus model/render revisions,
navigation state, window visibility and foreground information on timeout.
The portable/package process limits remain 30/45 seconds, respectively.

The preview's taskbar-colored background is declarative, so Reactor cannot
clear a parent swatch after the child image updates. Smoke retains exact pixel,
background, accessibility, draft/save isolation and unmount-buffer assertions.
All existing onboarding Back, disclosure, estimate, focus, notification and
synthetic Credential Manager checks remain.

Local sequential verification passed all four managed/executed x64 Native AOT
harnesses (170 Core, 14 platform, 924 Windows integration and 81 shared/bridge
assertions), plus release-tooling and 32 CI/28 release Python regressions.
The x64 Native AOT app passed three consecutive populated/empty portable
observations. Launcher fixtures also pass in PowerShell 7 and 5.1 and reject
assertion failures, timeouts, missing results and nonzero exits without retries.

Each hosted Windows packaging lane runs three populated/empty x64 observations
against its exact extracted development MSIX, including fresh process startup.
The synthetic child alone runs at below-normal priority on one permitted
logical processor. Each iteration keeps separate diagnostics, and the first
failure stops the lane; these are repeated observations, not retries until
green. Self-contained/Store x64 and ARM64 packaging, Windows test shards and
macOS coverage remain unchanged. No Developer Mode or other OS preference is
changed on the developer machine. Finite green observations cannot establish
a literal 100% guarantee for future Explorer, focus, load or runtime behavior.

## Parallel PR/main verification (October 4, 2026)

Windows verification uses two isolated test shards: application integration,
and Core/platform/shared tests. Each runs its managed and executed x64 Native
AOT harnesses; the latter also retains release-tooling checks and the full
Release solution build. Self-contained and Store packaging run on separate
runners, each publishing and validating both x64 and ARM64 and exercising
populated/empty x64 packaged startup. The Store lane retains deployment-mode
reset validation and synthetic Store identity/staging checks.

macOS 26 shared managed/Native AOT tests run independently from the native
bridge/app, Swift platform/Keychain and UI smoke lane. macOS 15 consumes the
exact app artifact without rebuilding or waiting for shared tests. Verification
requires both macOS 26 lanes and the runtime check; Windows matrix jobs use
`fail-fast: false` so one failed shard does not cancel the others. The gate
rejects failures, cancellations, unexpected skips and missing job results.

Default local/release verification remains sequential and complete. Build and
publish commands must not run concurrently in the same checkout. Cache keys,
change routing and PR/push/manual triggers are unchanged; no verification
coverage is moved to a schedule.

The first full-matrix hosted PR
[run 37264941553](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37264941553)
passed every lane, including all managed/Native AOT harnesses, both Windows
deployment modes, synthetic Mac Keychain CRUD, and populated/empty native UI
smoke on macOS 26 and 15. Creation-to-Verification elapsed time was 5:48,
versus the pre-change successful first-attempt PR median of 6:32 (five runs).
The longest Windows packaging lane was 4:58 versus the old 6:09 median;
Windows tests were 4:01 versus 4:53, and macOS 26 was 3:15 versus 3:40.
This is an initial observation, not a long-term percentile or guaranteed SLA.

The follow-up full-matrix
[run 37265434274](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37265434274)
passed every check in 4:16, 35% below the historical PR median and 33% below
the historical main median. Windows packaging lanes both took 3:48; test
shards took 3:50 and 2:52; macOS 26 app/UI and shared tests took 1:43 and 1:53,
with macOS 15 runtime at 0:31. Summed runner time was 18:47 versus the old
mixed-platform median of 15:31: elapsed time improves at the cost of more
parallel runner time.

Native AOT size changes landed in #65 during this work. A newer successful
pre-split [run 37263530902](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37263530902)
used identical application source trees, including those size settings,
and took 7:06 with Windows packaging at 6:49. This provides a same-application
comparison in addition to the historical baseline; neither comparison removes
hosted-runner/cache variability or establishes post-merge main timing.

## Windows period-estimate parity (October 3, 2026)

Windows now consumes the shared calculation, result and per-account persistence
from the macOS implementation merged in main. No duplicate forecasting policy
remains. The only shared adjustment is an optional `TimeProvider` for the
synthetic demo controller; ordinary demo behavior retains the system clock.
macOS UI and bridge commands are unchanged by this Windows integration.

The three macOS reference screenshots on issue #47 were compared with the
Windows estimate surfaces. Windows keeps native Fluent cards and controls while
matching the secondary row hierarchy: Estimated at reset and an approximate
amount, then early/projected-excess/UTC-reset context. Following issue #64,
Usage and account settings keep the amount beside the label with a 12-pixel
gap; the flyout retains right alignment to match its observed consumption.
Projected excess uses a theme-aware caution brush and a warning glyph, not
a projected progress bar. Small positive amounts use `<$1` and small excesses
use Less than $1 over allocation, matching macOS without implying zero.
The existing Advanced information/details disclosure on Usage and account
settings exposes the method, amount, average per day, UTC boundaries and
observation time. An unavailable result exposes its reason in both the compact
row and expanded details.

Local release-tooling verification, the Release solution build and all four
managed and executed x64 Native AOT harnesses passed: 170 Core tests, 14 platform
tests, 911 Windows integration assertions and 81 shared/bridge assertions.
Windows fixtures check draft/save/account-switch isolation, exact equality with
the shared result, matching whole/sub-dollar and UTC text, and unchanged actual
consumption, totals and tray allocation.

An x64 Native AOT app publish passed populated and empty native WinUI smoke.
Populated smoke drives the real preference checkbox, Save and accessible
expanders, checks all three estimate surfaces, and measures that the label
and amount share a baseline without overlap, and forecast text remains smaller
than actual consumption. The geometry assertions now require a 12-pixel
label-to-amount gap in Usage and account settings (including unavailable
estimates), and right-edge alignment in the flyout.
It also checks label association/help, expanded diagnostics on Usage and
account settings, and unavailable/explicit-off behavior. These are synthetic
controls and geometry checks, not proof of pixel-identical cross-platform
appearance, screen-reader behavior or forecast accuracy. ARM64 execution,
theme/accessibility review and real-provider semantics remain acceptance checks.

### Packaged smoke timeout diagnostics

PR #59 verification [run 37171108211](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37171108211)
passed the managed/Native AOT harnesses, both macOS jobs and self-contained
packaged populated/empty smoke. The first Store-packaged populated smoke timed
out at the unchanged 45-second process limit. Its retained artifact contained
only the launcher transcript, not application progress or diagnostics, so the
stalled phase and root cause cannot be established from that run.

The original Store x64 Native AOT build passed local populated/empty packaged
activation with the resolved 2.5.1 runtime; that does not prove the CI timeout
was fixed. Synthetic smoke now writes phase checkpoints, and CI copies only
the result, phase progress and fixed-category diagnostic logs before removing
the development registration, separated by packaging mode and scenario.
Startup exceptions in smoke mode no longer open an unattended error dialog;
ordinary app startup keeps its existing dialog. No timeout or UI assertion is
relaxed and no retry-on-failure was added.

PowerShell 5.1 regressions cover missing startup logs, phase-only timeout
artifacts, assertion results, rotated logs and exclusion of account settings.
Release-tooling checks, the Release solution build and all four managed
harnesses passed. The instrumented Store x64 Native AOT app also passed both
local packaged smoke scenarios, retained their final phase, and removed its
isolated development registration. Hosted confirmation remains required.

## macOS Sparkle updates (October 3, 2026)

The native Swift frontend now embeds checksum-pinned Sparkle 2.10.0 without
changing the `swiftc`/Native AOT build model. Every embedded Sparkle Mach-O
file is thinned to arm64 before inside-out signing. Package inspection covers
framework/helper presence, executable permissions, architecture, dependency
paths, signed-feed/verification configuration and consent/profiling defaults.
Public builds use a retained standard updater controller; development, demo
and smoke modes do not instantiate it or read/write updater preferences.

Local full `bash tools/macos/verify.sh` passes the existing 170 Core tests and
81 shared controller/bridge assertions in managed and executed arm64 Native
AOT, 17 CI-routing tests, 30 release-planning/workflow tests and 52 Mac tooling
tests, plus native Swift fixtures and both populated/empty UI smoke runs.
Updater fixtures cover isolation, duplicate startup, preference forwarding,
disabled actions, update reminders, downloaded/install-on-quit relaunch
availability, startup failure and explicit errors. The real Sparkle updater
reads signed synthetic appcasts from a loopback-only native fixture server and
rejects tampered/unsigned feeds and incompatible OS requirements without
downloading or installing an archive. Test preferences use unique synthetic
defaults domains, and fixture servers/files are removed after each case.

PR #62's first hosted macOS 26 run failed while the Sparkle fixture awaited
Python subprocess/file-sentinel readiness, not in a navigation/UI assertion.
The generic startup error and absent retained diagnostics did not establish
whether Python exited or missed its deadline. The fixture now uses an
in-process Network.framework listener bound exclusively to `127.0.0.1` and
awaits its ready callback and Sparkle's completion callback directly. An actual
HTTP request verifies the listener serves the exact feed bytes before starting
Sparkle. There are no fixture polling loops or unconditional settling delays;
ten-second deadlines only guard missing callbacks. Callback fixtures cover
early completion, duplicate completion, timeout and cancellation.

Per-scenario progress, listener states, HTTP responses, signing exit status and
Sparkle callback errors go to stderr and `artifacts/macos-test-diagnostics`.
The native harness exits with a readable failure instead of trapping on an
uncaught Swift error, and Verify/Release retain these synthetic logs on failure.
The signed/tampered/unsigned/OS-incompatible cases still execute the real
Sparkle updater, and negative cases assert appcast-verification callbacks and
the precise signature-validation/OS-incompatibility error codes rather than
accepting an arbitrary transport failure as success. The existing UI geometry
assertions, readiness thresholds and job limits are unchanged. Hosted
confirmation of the revised fixture remains required.

Real Sparkle signing tools also generate an appcast from a synthetic arm64 DMG.
Fixtures verify the signature of the exact archive bytes, tampered-feed/archive
rejection, private/public-key mismatch, immutable versioned download URLs,
stable-only numeric ordering, preview/Windows/draft exclusion, metadata
constraints, retention of older entries, missing-latest-feed failure, and feed
retry orchestration after an already-published release. Only public test-vector
or ephemeral synthetic keys are used.

Production Apple signing/notarization with the new embedded helpers, GitHub
Pages deployment, macOS 15 execution, and replacement/relaunch between two
Developer ID signed/notarized versions remain release acceptance gates.
Configure the dedicated production Sparkle key pair and Actions-based Pages
site as described in [RELEASING.md](RELEASING.md) before publishing. Exercise
manual and opt-in automatic updates, read-only/translocated installations,
declined authorization, unavailable feeds, login startup, and preservation of
synthetic history/settings/Keychain items. No production keys were generated
or cloud configuration changed during local implementation.

## macOS per-account period estimates (October 3, 2026)

Account preferences now include an off-by-default Show estimated period
consumption toggle. Saving it adds an Estimated at reset row to that account's
popup, Usage page, and account details, with whole-dollar approximate amounts,
an early-period qualification, UTC reset date, and projected over-allocation
when applicable. Advanced Details exposes the method, average per day, UTC
boundaries, and observation time. There is no combined forecast or change to
actual totals, allocation meters, menu-bar icons, or alert thresholds.

The shared C# calculation explicitly assumes UTC calendar months and rejects
provider resets that disagree with the next month boundary. Source observation
time is used when supplied, otherwise fetch time. A source timestamp up to one
minute ahead of the local fetch is clamped to fetch time for the estimate to
tolerate server/client clock skew; the saved source timestamp is unchanged.
Larger future timestamps and previous-period observations remain unavailable.
Frozen data never changes its pace estimate merely because the clock advances.
Freshness and rollover still invalidate it. Estimates are unavailable before
24 elapsed hours and marked early before 72 hours. Unknown/unlimited allocation
does not prevent a dollar
projection. These are transparent estimation assumptions, not a verified
provider billing contract or a promise about invoices.

Local managed and executed arm64 Native AOT runs pass 170 Core tests and
81 shared application/bridge assertions, including calendar/leap-year lengths,
UTC offsets, precise elapsed-time thresholds, source/fetch timestamps, stable
cached projections, stale/error/rollover handling, zero usage, allocation
variants, extreme/invalid decimals, legacy defaults, persistence, omitted-save
preservation, explicit disable, reconnect, and unchanged actual alerts.
The Apple-silicon development app builds with warnings treated as errors and
passes its existing bundle/signature checks. Swift fixtures additionally
cover exact decimal decoding, whole-dollar/sub-dollar text, early/warning/UTC
labels, and enabled/disabled/unavailable account-row sizing and rendering.
Use exact decimal fixtures rather than binary floating-point JSON values.

Populated and empty native smoke runs exercise forecast preference save/read,
the real Native AOT bridge, unchanged totals/tray percentages, popup anchoring,
account settings rendering, and explicit disable. SwiftUI view tests now use
the same stable-SDK selection as app builds. Snapshot captures can omit
GPU-composited text and do not establish pixel-perfect appearance or VoiceOver
behavior. macOS 15/Apple-silicon execution and real-provider period/timestamp semantics
remain validation gates; Windows forecast UI is a separate implementation.

Advanced Details uses one full-width disclosure button shared by Usage and
account details, with an expanded/collapsed accessibility value. Accounts rows
use the same native shaded GroupBox as Usage, show a visible Manage account
action, and remain clickable across their whole area. Native offscreen mouse-event fixtures verify expansion from the heading
text, collapse from the far end of the heading, existing chevron activation,
and account navigation from both the information area and explicit action.
These fixtures send events only to their own synthetic test window.

The first hosted run of the click fixtures failed the whitespace-collapse
assertion after its fixed 250 ms animation wait. Interaction readiness now
requires the expected layout/navigation state and stable fitting/bounds
geometry for 300 ms within a five-second monotonic deadline, using the same
readiness helper as popup smoke. Synthetic clicks use advancing uptime
timestamps and event numbers, and deliver mouse-up exactly once whether
AppKit consumes the queued event during tracking or requires explicit
delivery. Timeout diagnostics retain the phase, first/latest sizes, bounds,
window frame, sample count and OS. Five consecutive local native harness
runs passed without retry-on-failure; hosted confirmation remains required.

Menu-bar activation now makes the shown popover window key as well as
activating the app, so controls receive normal active colors and keyboard
focus without forcing a SwiftUI color environment. Real synthetic popup smoke
requires the active key window at initial presentation, reopening, and live
resizing, alongside its existing attachment and content-size checks. Failure
diagnostics include the popup's key-window and key-eligibility state. Transient
outside-click dismissal, toggle-to-close, and settings navigation are unchanged.

## Store-only shared Windows App Runtime (October 3, 2026)

Store packaging opts into `StorePackage=true` / `WindowsAppSDKSelfContained=false`
with the component-only `Microsoft.WindowsAppSDK.Runtime` reference. GitHub
and ordinary development builds remain self-contained. Both retain Native AOT
and require no separate .NET runtime. Store manifests derive the framework
identity, Microsoft publisher and minimum version from the resolved Microsoft
MSIX, currently `Microsoft.WindowsAppRuntime.2` / `2.5.1.0`.

Local `verify.ps1 -NativeTests` passed all four managed and executed x64 Native
AOT harnesses. Real x64 publishes of both modes passed populated and empty
synthetic UI smoke, without real accounts or startup writes. Store payloads,
including license notices and excluding symbols, were 18.84 MiB for x64 and
19.63 MiB for ARM64. Runtime metadata checks also passed under Windows
PowerShell 5.1, matching the hosted smoke step.

Synthetic bundle mutations reject missing, duplicate or incorrect framework
dependencies, mismatched dependency architecture, bundled runtime DLLs/themes,
and missing application resources. Store staging rejects self-contained input.
Store PRI fixtures require Reactor startup XAML without app-local WinUI themes;
the existing self-contained resource requirements remain enforced. Initial
bundle/staging contract fixtures used explicitly synthetic ARM64 PE metadata
and were deleted after use. After installing the ARM64 C++ tools, real x64 and
ARM64 Native AOT publishing, packaging and deployment mutation regressions
passed in both modes; framework-dependent staging also passed with a synthetic
Store identity. The Store metadata
snapshot and `-Store -SkipPublish` also remained valid after a default
self-contained build restored the project.

The compressed two-architecture bundles measured 60.40 MiB self-contained and
14.37 MiB framework-dependent, a 76% app-bundle reduction excluding the shared
runtime download. ARM64 was cross-published, not executed.

After the owner enabled Developer Mode, real x64 MSIXs from both modes were
extracted and registered under the isolated development identity. Populated and
empty packaged smoke scenarios passed for both, including actual UI creation,
package-local data and disabled Windows StartupTask. Each development
registration was removed afterward; no production registration, account data
or startup preference was changed.

Verify mirrors this by building real Native AOT payloads for both architectures
and modes, installing the resolved x64 framework on its disposable runner when
needed, and smoke-testing both extracted packages. Hosted runs, clean Store
installation/upgrading, ARM64 execution and Store certification remain release
acceptance checks. No production Store submission was made.

## macOS empty popup and menu-bar anchoring (October 3, 2026)

Current macOS builds support **Apple silicon only**, on the latest patches
of macOS 26 and 15, with a 15.0 deployment minimum. This removes Intel
support, not macOS 15 support; Windows x64/ARM64 support is unchanged.
Previously published universal releases and the Intel investigation below
remain historical evidence and are not modified by this policy.

With no connected accounts, the Mac popup now shows an Add Account prompt
without unavailable totals, tray diagnostics, an empty scroll region, or a
refresh button. The same prompt is used by the empty Usage page; actual
initialization and account-data failures retain their existing error paths.

The smaller popup exposed an AppKit sizing mismatch: `NSPopover` positioned
its default 320-point content height before SwiftUI shrank the empty content
to 225 points, leaving a measured 95.5-point gap below the menu-bar button.
The frontend now measures the current fitting size before showing the popup
and enables hosting-controller preferred-content-size updates. No screen
offset or fixed popup height is used.

Native smoke captures both empty and populated popups, checks the empty
prompt stays under 300 points high at its 400-point width, and verifies the
popup remains within 8 points of its menu-bar anchor. Synthetic notice
growth and removal exercise both live expansion and shrinkage in both states. The
sample-only Add Example Account action also exercises the real shared bridge,
updates consumption and allocation, and resizes the still-open popup from
empty to populated without authentication. Managed and Native AOT fixtures
cover repeated additions, unique account identities, refresh retention,
session-only storage, and rejection by a normal-mode controller. These checks
pass locally on macOS 26 / Apple silicon; macOS 15 / Apple silicon remains a CI gate.
AppKit render snapshots can omit GPU-composited text and do not establish
pixel-perfect appearance or VoiceOver behavior.

### Bounded popup geometry synchronization

Before the Apple-silicon-only policy, main Verify
[run 37156192286, attempt 1](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37156192286/attempts/1)
failed on macOS 15.7.9 / Intel with a 536-point gap at the initial populated
popup check. The failed-job rerun passed on the identical commit using the same
universal artifact. The original log recorded no window or screen geometry,
so it cannot distinguish unfinished AppKit positioning, desktop layout or a
production anchoring defect. A successful rerun is not proof of a fix.

The smoke test previously sampled geometry once after a fixed 250 ms sleep.
It now waits for a visible, on-screen menu-bar button with stable geometry
before calling `show` (AppKit does nothing if its positioning view is not
visible). Initial presentation, reopening, notice growth/shrinkage and example
insertion then require correct, stable geometry for 300 ms, sampled every
50 ms, within a five-second monotonic deadline per phase. There is no
reopening, reanchoring, disabled animation or repeated process launch to rescue
a failing assertion. The outer smoke process retains its 90-second limit.

The original inclusive eight-point vertical limit is unchanged. Popup and
button must be visible on the same screen, horizontally attached, and within
the screen frame; hosting-view bounds, actual window content bounds and hosting-controller
preferred size must converge. Growth/shrinkage and empty-to-populated insertion
must also reach their expected content sizes, rather than passing on unchanged
old content; a populated popup may retain its capped scroll height. Menu-bar checks
still require downward (`minY`) placement; an undersized display that cannot
fit the popup below its button fails explicitly rather than accepting a
different edge or a detached, screen-clamped window. Screen origins are not
assumed to be zero. The working production pre-show fitting-size and
preferred-content-size updates are unchanged.

Timeouts print the phase, elapsed time, sample count, readiness reason and
first/latest geometry: positioning rectangle, button frame/bounds/visible
rectangle and screen conversion, anchor/popup window frames and content
bounds, popup visibility, content/preferred sizes, all screen and visible
frames, scale and OS version. These diagnostics contain geometry only, not
account data or credentials, and run only in synthetic smoke mode.

Deterministic Swift regressions cover delayed placement versus a permanent
536-point detachment, missing/invisible geometry, expected-content readiness,
initial empty/populated sizes, growth/shrinkage and example sizes, subpixel
noise versus cumulative drift, deadlines, strict vertical/horizontal
attachment, unconverged sizes, constrained displays and negative screen
origins. Before removal of Intel support, local full verification passed on macOS 26.7 / Apple silicon with
the pinned .NET 10.0.401 SDK and stable macOS 26 SDK: 155 Core tests and
64 shared assertions in managed and executed arm64 Native AOT, universal app
packaging, native model/geometry/notification/Keychain checks, and both real
synthetic popup launches. Additional populated/empty native and Intel-slice
Rosetta launches also passed, without retry-on-failure. This does **not**
establish macOS 15 behavior or prove the original failure's OS-level cause.
The synchronization fix remains in the Apple-silicon-only app; removing Intel
does not justify weakening popup assertions on either supported OS version.

The first PR verification of the Apple-silicon-only change
([run 37159513752](https://github.com/DamianEdwards/ghcp-spend-tray/actions/runs/37159513752))
exposed a second test assumption on macOS 26.6.2: after notice removal, the
window content, hosting view and preferred size had all correctly shrunk from
321 to 261 points, with zero anchor gap, but `NSPopover.contentSize` still
reported 321. That property is not a reliable measurement of the rendered
window when hosting-controller preferred-size updates drive resizing.
Readiness now compares the actual window content bounds with hosting bounds
and preferred size; `NSPopover.contentSize` remains in failure diagnostics
only. The exact CI geometry is a deterministic regression, alongside real
window-size mismatch and permanent-detachment failures. No timeout, attachment
tolerance, production sizing, supported-OS gate or content-growth/shrinkage
expectation is relaxed.

### Apple-silicon-only artifact checks

Builds publish only `osx-arm64` and compile only the arm64 Swift target.
Distribution validation requires exactly `arm64` in both the executable and
Native AOT library, rejects universal and Intel-only Mach-O files, and retains
dependency-path, deployment-minimum and signature checks. Reused bundle output
is replaced rather than merged with old architecture slices. Release metadata
declares only arm64 and is validated before draft staging; historical
published releases are not changed. Build, verification and smoke launchers
reject unsupported hosts, and smoke executes explicitly with `arch -arm64`.

Synthetic tooling regressions cover native-host rejection (including Rosetta),
exact-slice validation, failed Mach-O inspection, checking both binaries before
launch, removed architecture arguments, release metadata/provenance, Mac-only
tool routing, and both fail-closed OS gates with same-artifact reuse. Current
verification uses `macos-26` for the release-toolchain build and `macos-15`
for execution of that exact arm64 artifact without rebuilding. Both are
Apple-silicon labels in the authoritative
[GitHub-hosted runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
Runner architecture is asserted explicitly. Local macOS 26 execution cannot
establish macOS 15 behavior; that remains a hosted CI gate.

The arm64-only change passed local full verification on macOS 26.7 /
Apple silicon with .NET 10.0.401 and the installed stable macOS 26 SDK:
155 Core tests and 64 shared assertions in managed and executed arm64
Native AOT, native model/geometry/notification/Keychain checks, both
populated/empty smoke launches, and package dependency/minimum/signature
checks. Both final Mach-O files report exactly `arm64`, including after
reusing the former universal bundle output; the obsolete generated Intel
build directory is removed. Popup growth/shrinkage and example insertion
remain covered, with measured anchor gaps of at most half a point locally.
Real disposable Mach-O package fixtures additionally reject Intel/universal
slices in either binary, a newer bridge deployment minimum, absolute
non-system dependencies, missing binaries and unsigned bundles. The Intel
fixtures are negative-test inputs only, not supported app builds.
No production signing, notarization, release publication or macOS 15
execution was performed by this local validation.

## macOS 0.1.0 notification callback launch regression

The installed, signed/notarized 0.1.0 app passed signature and Gatekeeper
verification but crashed on macOS 26 while fetching notification settings.
The faulting stack was `_dispatch_assert_queue_fail` in the callback inside
`NativeNotifications.status()`. The older release SDK omits `Sendable` on that
completion-handler declaration, so Swift inferred main-actor isolation even
though UserNotifications invokes it on a background queue.

All three notification completion handlers now explicitly use `@Sendable`,
transferring only values/errors through checked continuations. Smoke mode also
queries real notification settings without requesting permission or sending a
notification, so the callback is no longer hidden behind the demo service.
The first fix passed the same macOS 15-built artifact on macOS 26 without
rebuilding it (Verify run 37132765292). Build and release jobs now explicitly
select Xcode 26.6 / macOS 26.5 SDK on macOS 26, instead of the macOS 15 runner's
Xcode 16.4 default.

The support policy is the latest patch releases of two major versions:
**macOS 26 and macOS 15**. The minimum app deployment target is macOS 15;
macOS 14 Sonoma is dropped. Routine verification runs exactly two Mac jobs:
macOS 26 / Apple silicon runs shared tests, builds the arm64 app once and
smoke-tests it; macOS 15 / Apple silicon downloads that exact artifact, verifies
the executable and bridge are arm64-only and runs native smoke tests without rebuilding. Both
exercise the real read-only notification callback. Hosted image patches are
logged. This covers both OS generations on the only supported Mac architecture.
Advance the window with each adopted stable
major release.

## CodeQL configuration

`.github/workflows/codeql.yml` uses advanced setup and explicitly scans
Actions, C#, JavaScript/TypeScript, Python, and Swift every Wednesday at
10:37 UTC and on manual dispatch. Scheduled runs scan the latest default-branch
commit. CodeQL does not run automatically on pushes or pull requests; use
**Actions > CodeQL > Run workflow** for an additional scan, such as before a
release. This trades pre-merge CodeQL feedback for post-merge scheduled
findings. The required **Verification** checks still run on PRs and main pushes.
Swift analysis uses manual build mode on macOS. Before CodeQL initialization,
`tools/macos/build.sh --bridge-only` uses the pinned .NET SDK to build the
in-process Native AOT bridge. After initialization,
`tools/macos/build.sh --frontend-only` compiles the real Swift frontend against
that bridge, so the extractor observes the Swift compiler invocations without
tracing the unrelated .NET/AOT build. A missing bridge is an error, not a skipped
build. Normal verification and release builds still compile both components
with the same script. Analysis does not require production signing credentials
or run the application.

CodeQL's bundled Clang importer cannot consume the bridging-header PCH produced
by Apple Clang (`PCH file ... built from a different branch`). During Swift
CodeQL extraction only, the shared build script passes the supported
`-disable-bridging-pch` driver flag so both compilers parse the original
`Bridge.h`. Normal verification and release builds retain their usual PCH
behavior. Failed Swift scans retain extractor diagnostics and the build-tracer
log for seven days; a successful app build alone does not prove successful
CodeQL extraction.

GitHub's default setup cannot discover our command-line Swift build: there is
no Xcode project/workspace or Swift package. It also detects `Bridge.h` as C/C++
but fails extraction because the header only declares the C ABI implemented
in C# and contains no C/C++ translation units. The explicit workflow omits
that empty C/C++ analysis, not the bridge implementation or Swift callers.
C# retains no-build analysis, matching the previous default setup.

Disable CodeQL **default setup** in repository settings when enabling this
workflow; default and advanced result uploads cannot coexist. Do not add a
dummy C source, suppress extraction errors, or disable Swift scanning to make
the check green. Add an appropriate C/C++ analysis job if native C/C++ sources
are introduced later.

## Dependency and repository security configuration

Keep the dependency graph, automatic dependency submission, Dependabot alerts
and Dependabot security updates enabled in GitHub repository settings.
Automatic submission resolves the .NET dependency tree, including the shared
engine and Mac bridge. The committed npm lock file supplies the artwork
tooling's direct and transitive dependencies; GitHub also tracks the pinned
Actions dependencies.

`.github/dependabot.yml` schedules weekly Wednesday version updates for NuGet
projects under `src` and `tests`, the pinned .NET SDK in `global.json`, GitHub
Actions, and npm tooling under `tools`. Windows App SDK component updates stay
grouped, as do Actions updates. SDK major and minor version updates remain
ignored.

The Swift frontend currently imports only Apple system frameworks and the
local C ABI header. It has no `Package.swift`, `Package.resolved`, CocoaPods,
or Carthage dependencies, so there is no separate Swift package graph or
Dependabot update entry to configure. Add Swift Package Manager coverage when
introducing external Swift packages. Xcode and Apple SDK updates require
manual changes to the shared `.github/actions/setup-macos` toolchain and
macOS verification; Dependabot does not update these toolchain pins.

Keep secret scanning, push protection, and private vulnerability reporting
enabled on this public repository. Actions use read-only
default token permissions, require full-SHA action pins, and cannot approve
pull requests. The main branch requires pull requests and the aggregate
**Verification** check; the weekly/manual CodeQL scan is not a pre-merge gate.
`tools/ci/test_dependabot.py` guards package coverage alongside the CodeQL
workflow checks.

## macOS implementation evidence (October 1, 2026)

The native Mac frontend shares the C# controller, host-scoped OAuth, decimal
accounting, storage, scheduler and alerts with Windows, rather than
reimplementing them in Swift.
The table records historical October 1 evidence, before Intel support was
removed; its universal/Rosetta results are not current distribution claims.

| Check | Evidence |
|---|---|
| Shared engine | 155 Core tests and 49 shared controller/bridge assertions pass under managed .NET and executed arm64 Native AOT; tray weighting/eligibility, persistence, exclusion-preserving reconnect, and draft-preview isolation use the same C# code on both platforms |
| Native app | Universal arm64/x86_64 SwiftUI executable and C# Native AOT library build with warnings treated as errors; bundle versions, both slices, dependency paths and ad-hoc signatures checked |
| UI smoke | Empty/populated launches, navigation/rendering of all five settings pages, onboarding sheet, exact synthetic $42.75 summary, 34.2% weighted allocation, draft-vs-saved indicators, per-account selection/navigation, unavailable fallback and normal shutdown exercised on Apple silicon. The historical pre-sync Intel slice also passed under Rosetta; Intel is no longer a supported runtime |
| Native model tests | Synthetic bridge/clipboard fixtures exercise immediate github.com sign-in, automatic code copy and retry, automatic completion, cancellation draining, late callback suppression, custom hosts and immutable reconnect. Pie/percentage/unavailable template graphics contain visible antialiased glyphs on a transparent background |
| Notification permission UX | Synthetic service fixtures cover explicit first-use permission, denial without a repeated prompt, opening Settings, refresh after external changes, quiet delivery, actual API errors, in-flight deduplication and side-effect-free demo/background behavior. System Settings deep-link selection may vary by macOS release; the UI includes manual navigation fallback |
| Keychain | Uniquely named synthetic device-local item create/read/update/delete, missing-item behavior and target isolation exercised using Security.framework; no real account credentials read |
| Swift formatting | Exact cents, invalid/negative amount rejection, unavailable-not-zero display and C# timestamp parsing checked |
| Automation | Platform-routing and separate-version tests pass; PowerShell Store/release tests pass with Mac releases excluded from Windows selection; workflows pass actionlint |
| Unified release planning | Synthetic tests cover per-platform stable baselines and bump resets, first-release baseline `0.0.0` (default Minor gives `0.1.0`), no-release selections, numeric package bounds, preview/draft/tag collisions, pagination/API errors, original-plan replay and already-published provenance. No production signing or release is triggered by these tests |
| Windows extraction | Windows platform adapter test project compiles on macOS; full WinUI build/execution requires Windows SDK executables and remains a Windows CI gate |
| Production distribution | Signing/notarization automation implemented; not executed locally without the owner's Developer ID/API credentials |

Reproduce current Apple-silicon-only verification with
`bash tools/macos/verify.sh` on a native arm64 Mac. There is no Intel slice
or Rosetta smoke path in current builds.

Smoke mode requires a unique absolute `--data-dir`, uses only synthetic
accounts, and disables authentication, notification delivery/permission prompts,
and login startup. It reads the app's current native notification settings to
exercise completion-handler threading; it does not change permissions.
The smoke launcher uses a temporary directory and a bounded subprocess
lifetime, then deletes its synthetic data. Render snapshots demonstrate view
creation, not pixel-perfect layout or VoiceOver correctness; AppKit snapshots
can omit GPU-composited SwiftUI text. No screenshot or accessibility
permission is requested or changed by these tests.

### Required before macOS production acceptance

- Run both supported-OS Apple-silicon CI jobs; macOS 26 alone is not macOS 15 evidence.
- Install the signed/notarized DMG on a clean standard-user Mac with Gatekeeper
  enabled, and update between two signed versions without losing settings,
  history, credentials or login preference.
- Complete real device sign-in, automatic identity verification/save, reconnect, rotation and
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

## Packaged startup resource regression (October 1, 2026)

Store version 0.3.0 crashed before the app's UI exception handler was installed:
Windows recorded `Microsoft.UI.Xaml.dll`, exception `0xc000027b`, and WER
signature `0x802b000a`. The new package-level `resources.pri` indexed only shell
icons. Its presence changed packaged WinUI resource resolution, hiding startup
XAML despite `GHCPSpendTray.pri`, `Reactor.pri` and the XBF files being present.

The unchanged installed executable and runtime files reproduced the identical
crash under an isolated development identity with synthetic data. Merging
`GHCPSpendTray.pri` into the final-identity primary index, without changing the
executable, made both populated and empty packaged smoke scenarios pass. The
Store registration, account data, credentials and startup settings were not
changed; the temporary development registration was removed.

The packaging fix now retains WinUI/Reactor XAML and localized resources in
the primary map alongside the qualified icons. Bundle checks reject missing
startup resources, accept valid embedded XBF or existing file candidates, and
still check every shell icon size/theme. Synthetic regressions cover missing
application PRI, the 0.3.0 icon-only primary index, and missing/unindexed startup
XAML under multiple package identities.

Local validation passed a warning-free Release build, 155 core tests, 14
platform tests and 785 application assertions under both JIT and executed x64
Native AOT. x64/ARM64 publishing, development and synthetic Store bundle
validation, Store staging, and actionlint passed. Both smoke scenarios passed
after extracting the newly built x64 MSIX and registering that extracted
payload, matching the new CI path. ARM64 was cross-published, not executed.

PR/main/manual verification now runs this packaged x64 smoke after building,
using Developer Mode on the disposable hosted runner, a bounded timeout,
nonzero-exit/missing-result checks, registration cleanup and an uploaded
transcript. Hosted Actions execution of this new step remains unverified until
the first workflow run. No corrected production release has been published.

This escaped earlier checks because recent UI smoke runs were portable and
package checks inspected shell candidates and runtime file presence, not
packaged activation. The last documented packaged smoke preceded the icon
index change. Portable smoke alone is not evidence for packaged startup.

## Streamlined account sign-in (October 1, 2026)

`verify.ps1 -NativeTests` passed with a warning-free Release build, 155 core
tests, 14 platform tests and 785 application assertions under managed and
executed x64 Native AOT. x64/ARM64 Native AOT publishing and package resource
validation passed. The portable UI smoke retains coverage for the account
entry points, Change host editor, Back controls and existing usage/tray flows.
This includes the latest `main` Credential Manager capacity/error diagnostics;
its regression cases use the automatic sign-in callback contract.

Direct native UI interaction against the real controller with synthetic HTTP
and in-memory credentials verified that a single Add account click generates
the code and copies it to the actual Windows clipboard, with visible success
feedback and a primary Open browser action. Change host cancelled the attempt
and displayed host settings. After simulated browser authorization, the
account was saved and Complete appeared without an extra confirmation click.
No real browser OAuth authorization, installed credentials, or account data
were used. Screenshots cover the code/copy state and automatic completion;
live enterprise authorization remains a manual acceptance item.

## Empty Usage and status-icon refinement (September 30, 2026)

With no connected accounts, Usage shows only the setup message and primary
Add account action. Loading and failed initialization retain their own
loading/retry path. The empty native smoke scenario asserts there is no total,
tray diagnostic block or Refresh control, and invokes Add account to confirm
the onboarding route. A separate live synthetic UI interaction also confirmed
the message, absence of the old detail controls, and working setup action.

Status icons now use native Segoe UI glyphs and four-times supersampled shapes,
downsampled into transparent premultiplied BGRA at the target DPI. Fractional
coverage replaces the hard-edged pixel font and circles. The numeric style uses
its space for the number, leaving the percentage unit in its tooltip/preview
label; partial and over-allocation marks remain distinct. A taskbar-colored
preview swatch handles different app/taskbar themes without adding a background
rectangle to the actual Shell icon.

`verify.ps1 -NativeTests` passed with 155 core tests, 14 platform tests and
774 application assertions under both JIT and executed x64 AOT. Coverage
includes transparent corners, antialiased coverage, exact color premultiplication,
native HICON creation and bounded GDI/USER resources. Both architectures
published and packaged successfully. Populated/empty native UI smoke passed,
including exact preview-to-renderer pixel parity and the preview background.
Renderer output was inspected at 16px and 24px with light/dark surfaces.

These samples are renderer previews, not Explorer screenshots. Background
capture of the new empty page returned black content in this desktop session;
the accessibility tree and actual Add account navigation were verified instead.
Real Explorer rendering across the full DPI/theme matrix remains manual.

## Combined feature integration (September 30, 2026)

The taskbar packaging fix, revised account confirmation/avatar, configurable
tray indicators with unsaved previews, approved connected-dollar artwork and
standard Back input are integrated and checked together.

- `verify.ps1 -NativeTests`: warning-free Release build, 155 core tests,
  14 platform tests and 638 application assertions under JIT and executed x64
  Native AOT.
- `package.ps1`: x64 and ARM64 Native AOT publishing and unsigned development
  bundle validation passed. Both packages contain the new transparent dollar
  assets and final-identity shell resource index.
- The SVG generator's check mode reproduces every checked-in raster and ICO.
  All seven sizes in the approved concept preview match byte-for-byte. The
  executable ICO embeds the exact same PNGs as the corresponding package
  target-size assets; release-tooling checks enforce this relationship.
- The combined x64 published executable passed both populated and empty
  portable native UI smoke scenarios, including Back-window recreation, tray
  callbacks, draft-preview pixel parity, Save isolation and resource cleanup.
- Direct UI interaction against the integrated application with synthetic HTTP
  and in-memory credentials exercised avatar confirmation, connecting/success,
  Alt+Left from an account text field, live numeric/per-account/empty-selection
  previews, and Save. The synthetic settings file retained the original tray
  choices during preview and changed only after Save. The new dollar artwork
  was inspected in the window icon and About page.

No real OAuth flow, package installation, OS preference changes, Explorer
restart or ARM64 execution was performed in this integration pass. Physical
mouse Back, installed/pinned taskbar behavior and the full multi-monitor/theme
matrix remain manual acceptance items. Synthetic review windows were closed;
the user's installed app and account data were not modified.

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
raw callback deduplication, rapid ordinary selection, focus transitions,
accessible settings/onboarding actions,
detail disclosures, synthetic notification submission and a uniquely named
synthetic Credential Manager write/read/delete. Shell accepting a notification
does not prove visual delivery. Synthetic callbacks do not reproduce all Explorer
foreground-permission behavior.

## Packaged taskbar icon regression (September 30, 2026)

The approved connected-dollar artwork replaces the original bars in the
application ICO, in-app logo and package assets. All shell icons retain
transparent corners. The package includes default,
unplated, and light-unplated candidates at 16, 20, 24, 30, 32, 36, 40, 48, 60,
64, 72, 80, 96, and 256 pixels. A separate package-identity `resources.pri`
indexes these under `Files/Assets/Square44x44Logo.png`.

`verify.ps1` passes a warning-free Release build, 147 core tests, 14 platform
tests, and 128 application assertions. Its icon-tooling regression suite
rejects missing light/dark files, files present but absent from the PRI, wrong
dimensions, opaque corners, a mismatched PRI identity, and a manifest pointing
to another icon or requesting a background color. A second synthetic package
identity verifies that indexing does not hardcode the development identity.

`package.ps1` successfully publishes x64 and ARM64 Native AOT and creates an
unsigned development bundle. Validation extracts both packages' PNGs and PRI
and checks dimensions, transparent corners, opaque artwork, identical themed
artwork, the final identity, resource URI, and each TargetSize/AlternateForm
pair. This verifies shipped resources, not just source filenames.

Visual shell acceptance remains manual: install an approved development or
signed package, open Settings, and check the running and pinned taskbar icons
in Windows light/dark modes at 100%, 150%, and 200% display scaling with a
visible accent color. Check Start/search too. Verify both a fresh pin and an
upgrade of an existing pin; record any stale Explorer icon-cache behavior
separately rather than changing artwork or deleting user caches. No package
registration, OS theme/scale changes, or signed upgrade was performed for this
regression, and cross-publishing does not establish ARM64 shell behavior.

## Settings Back input regression checks

The application harness covers the shared Back action on account details, add
and reconnect forms, all sign-in stages, hidden/stale targets, busy operations,
canceled-operation callbacks and settings closure. Back cancels active sign-in
without leaving the form; a subsequent Back returns to the accounts list.
Top-level settings pages have no Back history and leave the input unhandled.

The native UI smoke path checks both visible account Back controls and the
settings-scoped Alt+Left and GoBack keyboard accelerator registration after
rerenders and settings-window recreation. It also requires hidden accelerator
placement on the window root: the shortcuts remain active without WinUI's
automatic Alt+Left tooltip following the pointer across unrelated controls.
Ordinary control tooltips remain enabled. Physical input delivery is a separate
manual acceptance check (posting Win32 mouse messages did not generate WinUI
pointer events in the local smoke environment):
with focus in a text box, verify Alt+Left and mouse XButton1 perform the same
action as the visible Back control, while Backspace, Left, Ctrl+Left,
selection shortcuts and mouse Forward retain their ordinary behavior. Repeat
after closing/reopening settings, and verify Back does not dismiss the tray
flyout or invent navigation on a top-level settings page. Open a combo-box
dropdown, text context menu or narrow-window navigation pane and confirm that
Back does not navigate the account page behind it.

## Configurable tray indicators (September 30, 2026)

The bounded tray implementation adds pie/percentage styles, weighted roll-up or
per-account icons, and persisted account inclusion. Static application branding
and packaging assets are unchanged.

`.\tools\verify.ps1 -NativeTests` passed: Release build without warnings, release
tooling checks, 155 core tests, 14 platform tests and 562 application assertions
under both JIT and executed x64 Native AOT. Sequential x64 and ARM64 app publishing
passed with AOT warnings treated as errors. Populated and empty portable smoke
scenarios passed against the final x64 executable; startup registration was
unchanged. ARM64 was cross-published, not executed.

- Core fixtures cover omitted-field defaults in version 1 settings, source-generated
  JSON round trips, invalid enum values, allocation weighting, every non-fresh
  status, unknown/zero/unlimited allocations, malformed and mismatched snapshots,
  future observations, freshness deadlines, both billing-period policies, empty
  selections, fractional/over-allocation values and sums exceeding decimal range.
- Synthetic controller coverage checks native settings draft reload, persisted
  exclusions, weighted presentation and failed refreshes without changing
  last-known dollar totals. Live-preview coverage checks unsaved style, mode and
  selection, neutral fallback, partial/unavailable observations, time-based expiry,
  failed saves and successful preview-to-tray parity. Draft-only changes produce
  no Shell calls and leave the persisted configuration and installed icons unchanged.
- Native tests create real HICONs at 16, 20, 24, 32, 48 and 64 pixels; check
  partial/unavailable geometry, transparent corners, fractional antialias
  coverage and correctly premultiplied foreground colors without ClearType
  fringes or a baked-in taskbar background; and
  use an injected Shell boundary to check stable GUIDs/callback IDs, mode changes,
  reordering, reselection, rejection cleanup, version-4 recovery and disposal.
  Repeated icon lifetimes/replacements are measured with `GetGuiResources` to
  detect accumulated GDI/USER handles. These checks do not exercise Explorer UI.
- Preview images share `TrayUsage.Create`, `TrayIconRenderer.Pixels`, DPI sizing
  and the system/high-contrast palette with installed icons. They use a reusable
  WinUI `WriteableBitmap`, not temporary HICONs or files. Native font rendering
  uses short-lived GDI objects that are released before returning pixels. The UI smoke
  checks exact preview pixel parity, unchanged installed HICONs before Save,
  repeated same-size buffer reuse, native resource counts, and clearing the image
  source when its settings page unmounts.
- The isolated UI smoke path exercises the real WinUI style/mode/account controls,
  per-account keyboard callbacks, retired callbacks, no-selection access, and
  simulated `TaskbarCreated`/display-change messages against this app's own icons.
  It does not restart Explorer or change the user's Windows theme, DPI, contrast,
  startup registration, or installed app.

Optional renderer samples can be generated from the application harness:

```powershell
dotnet run --project tests\GHCPSpendTray.AppTests -c Release --no-build -- --tray-samples C:\Temp\tray-samples.bmp
```

The bitmap contains exact 16px output and 2x nearest-neighbor magnifications,
composited on the light/dark palette backgrounds. Append a pixel size (for
example `24`) after the output path to inspect higher-DPI output.
Columns are unavailable, zero, 0.1%, 50%, 100%, 105%, 1000%, partial 50%,
unlimited (infinity), and partial unlimited;
rows are light pie, light number, dark pie, dark number. Renderer samples are
not screenshots of Explorer's notification area.

**Remaining manual acceptance:** real Explorer mouse/keyboard/overflow behavior
with multiple icons, a real Explorer restart, mixed-DPI monitor/taskbar movement,
live theme and high-contrast switching, and ARM64 execution. Native palette reads
use Windows contrast colors when enabled; regular palettes follow the system
taskbar theme. Each existing icon's monitor DPI is reevaluated on display/theme
notifications and dashboard updates. Tooltips have Windows' 127-character limit;
the Usage page is the untruncated source of inclusion reasons. Percentages may
take up to one minute to age out between updates. Windows retains control of
overflow visibility and can suppress notifications.

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

The October 1 sign-in flow supersedes the earlier confirmation screenshots.
Add account immediately starts github.com device authorization, displays and copies
the code, and emphasizes Open browser. Change host cancels the attempt and opens
host-specific options. Reconnect uses the original identity/registration.
Browser authorization proceeds directly through identity/consumption checks and
credential/settings persistence; no confirmation callback, decision buttons or
temporary pre-confirmation avatar cache remain.

Synthetic fixtures cover automatic start/copy/completion, clipboard contention and
retry, host switching, duplicate and mismatched reconnect identities, code/token/
identity/consumption failures, timeout, persistence rollback and cancellation.
Diagnostic assertions check fixed failure phases and exception categories while
rejecting device codes, access tokens, signed avatar URLs and raw exception messages.
The post-save avatar cache remains bounded and nonfatal; normal cache security
checks remain in the Core/application harnesses.

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

## Credential Manager write failures

If sign-in fails with Windows error 8 (`ERROR_NOT_ENOUGH_MEMORY`), the user's
Windows credential store may have reached its capacity even when RAM and disk
space are available. This is not an OAuth rejection. The app records the failed
credential operation and native error code in `logs\diagnostics.log`, without
credential targets, tokens, account identities, or exception messages.

Open **Control Panel > Credential Manager > Windows Credentials** and remove
only entries you recognize as unused, then retry sign-in. The app does not delete
other applications' credentials or fall back to plaintext token storage.
Synthetic application tests cover actionable error-8 guidance, redacted
diagnostics, unchanged handling of other errors, and preserving configuration
when the credential write fails.
