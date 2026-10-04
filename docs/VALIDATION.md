# GHCPSpendTray validation and release gates

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
time is used when supplied, otherwise fetch time; frozen data never changes its
pace estimate merely because the clock advances. Freshness and rollover still
invalidate it. Estimates are unavailable before 24 elapsed hours and marked
early before 72 hours. Unknown/unlimited allocation does not prevent a dollar
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
Swift analysis uses manual build
mode on macOS, with `tools/macos/build.sh` running after CodeQL initialization
so the extractor observes the real Swift compiler invocations. The pinned
.NET SDK builds the in-process Native AOT bridge; analysis does not require
production signing credentials or run the application.

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
duplicate activation, focus transitions, accessible settings/onboarding actions,
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
Columns are unavailable, zero, 0.1%, 50%, 100%, 105%, 1000%, and partial 50%;
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
