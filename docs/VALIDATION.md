# GHCPSpendTray validation and release gates

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
