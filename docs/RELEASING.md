# GHCPSpendTray releases

Windows and macOS are independently versioned and released. Windows keeps
the existing `v<version>` tags and MSIX/Store identity; macOS uses
`macos-v<version>` tags and an Apple-silicon-only Developer ID signed/notarized app.
Dispatch **Release** and choose a version bump independently for each platform.
Only selected platforms build, request environment approval, and publish.
Shared changes should normally receive a release on both platforms, but their
numbers need not match.

## Choose platforms and version bumps

The **Release** workflow has these inputs:

| Input | Choices | Default |
|---|---|---|
| Windows version bump | `no release`, `Major`, `Minor`, `Patch` | `Minor` |
| macOS version bump | `no release`, `Major`, `Minor`, `Patch` | `Minor` |
| Mark selected releases as previews | Checkbox applying to both selected platforms | Unchecked |

For each selected platform, the workflow paginates GitHub releases and uses
the highest three-part version among that platform's published non-preview
releases. It does not use the repository-wide "latest" designation, draft
releases, or previews as the baseline. Major resets minor and patch to zero;
Minor resets patch to zero; Patch increments patch only. With no stable
release for a platform, the starting baseline is `0.0.0`: the default Minor
produces `0.1.0`, Major produces `1.0.0`, and Patch produces `0.0.1`.
The baseline itself is never published.

For example, Windows `v0.3.1` plus **Minor** becomes `v0.4.0`; if Mac has no
stable release, its **Minor** selection becomes `macos-v0.1.0`. Set a platform
to **no release** to skip it, including its signing configuration/approval.
Selecting **no release** for both produces a successful no-op summary, with
no native build or publication.

The preview checkbox changes GitHub release classification, not the numeric
version or baseline. Existing tags, drafts and public previews still reserve
their numbers: if stable `v0.3.1` plus Minor would reuse preview `v0.4.0`, or
would be below an already tagged version, planning stops before either
platform builds. It never silently advances to a different version or promotes
an existing preview. Select a larger bump or **no release** instead.
Package-specific limits are checked before builds.

The read-only planning job displays both calculated versions in its summary
and saves `release-plan-<run-id>` as an immutable Actions artifact before
either platform starts. Release dispatches share a concurrency group so
version selection/publication cannot race another run of this workflow.
The Windows and Mac jobs then run independently using the same protected
**production** environment; one can succeed even if the other fails.
They share environment protection rules, while each signing step references
only its platform's credentials.

### Retrying a release

Use **Re-run all jobs** on the original run. The plan is restored, not
recalculated from a now-newer stable release. If a platform already published,
the planner verifies the public release's tag, source, preview status and
`releaseRunId` metadata before skipping it. Unpublished platforms resume the
same planned version; only same-source tags and draft assets may be reused.
Public releases are never overwritten.

Re-running failed jobs alone retains the original successful planning outputs,
so it also never bumps again, but an ambiguous failure after publication may
need **Re-run all jobs** to recognize the completed platform. A missing or
expired plan (retained for 90 days), different source/run provenance, or a
newer tag stops the retry. If planning or artifact upload failed before any
platform began, start a new dispatch rather than inventing a replacement plan.
Do not run older copies of the pre-unification release workflows concurrently.

## Reserve the Microsoft Store product

1. Complete enrollment in the Microsoft Store Windows developer program in
   Partner Center. Under **New product**, choose **MSIX or PWA app**.
   Do not choose **EXE or MSI app**.
2. Enter **GHCPSpendTray**, check availability, and reserve the name.
   Reservation does not publish the app.
3. Open **Product management > Product identity**. Record the assigned
   **Package/Identity/Name**, **Package/Identity/Publisher**, and
   **Package/Properties/PublisherDisplayName**, plus the Store ID.
   These identity values are not necessarily the display name.
4. For eventual Store packages, pass these exact values to `tools\package.ps1 -Store`.
   The visible application name stays GHCPSpendTray.

Reference: [Store product identity](https://learn.microsoft.com/windows/apps/publish/view-app-identity-details).

## Direct distribution identity and signing

An MSIX package's `Identity.Publisher` must match its signing certificate's
subject. Obtain the exact subject from the Azure Artifact Signing certificate
profile. Do not substitute the account name or a guessed `CN=...` string.

Partner Center may assign a different publisher from that certificate. If so,
GitHub and Store packages must use different publisher identities unless a
supported alignment is arranged. They will have different package families,
data directories and installation identities. Do not promise an in-place
GitHub-to-Store upgrade. There is no migration code.

The app keeps its Windows singleton scoped by user and package family; a separate
Store family will not activate a GitHub-family process by mistake. Generic
production credentials currently use app/registration/account targets, so two
families should not be used simultaneously against the same accounts.

Local packaging defaults to `GHCPSpendTray.Development` with
`CN=GHCPSpendTray Development`. Release automation rejects those defaults.
Use a stable, production name (for example `GHCPSpendTray`, or the assigned Store
name if appropriate) and the exact signing subject before the first public release.

## GitHub Actions configuration

The workflows adapt the protected-environment, pinned-action, Azure OIDC,
attestation, and verify-before-publication approach from
[dotnet-steward](https://github.com/DamianEdwards/dotnet-steward).
Configure a GitHub environment named **production**, restrict it to `main`,
and require a reviewer. Protect `main` and require **Verification**.
Both Windows and macOS GitHub releases use this environment. Add the Apple
settings listed below alongside the Windows settings; no separate Mac
environment or Azure federation change is required.

Environment **variables**:

| Name | Value |
|---|---|
| `MSIX_IDENTITY_NAME` | Stable production package identity name |
| `MSIX_PUBLISHER` | Exact Azure signing certificate subject, including all DN components |
| `MSIX_PUBLISHER_DISPLAY_NAME` | Human-readable publisher name |

**Store Package and Publish** uses a separate protected GitHub environment named
**microsoft-store**. It already requires reviewer approval; restrict its
deployment branches to `main` before using it. Set its environment **variables**
to `STORE_ID`, `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`, and
`STORE_PUBLISHER_DISPLAY_NAME` from Partner Center (copy the previously
configured Store variables from `production`). **Release** does not consume them.
Both channels use the name `DamianEdwards.GHCPSpendTray`, but their configured
publishers differ; they therefore have separate package families.

Environment **secrets**, matching the reference repository's names:

| Name | Value |
|---|---|
| `AZURE_CLIENT_ID` | Entra application or managed identity client ID |
| `AZURE_TENANT_ID` | Tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Signing subscription ID |
| `AZURE_SIGNING_ENDPOINT` | Regional HTTPS Artifact Signing endpoint |
| `AZURE_SIGNING_ACCOUNT` | Artifact Signing account name |
| `AZURE_CERT_PROFILE` | Public-trust certificate profile name |

Configure a federated identity credential on the Azure identity:

```text
Issuer:   https://token.actions.githubusercontent.com
Subject:  repo:DamianEdwards@249088/ghcp-spend-tray@1375200120:environment:production
Audience: api://AzureADTokenExchange
```

This repository uses GitHub's immutable OIDC subject format, including the owner
and repository IDs. Do not replace it with the older name-only subject.
Inspect the effective subject prefix before configuring federation for a fork:

```powershell
gh api repos/DamianEdwards/ghcp-spend-tray/actions/oidc/customization/sub
```

Grant that identity the certificate-profile signer role at the necessary signing
profile/account scope (Azure may display **Trusted Signing Certificate Profile
Signer**). This is distinct from subscription Contributor. Reusing an account
does not automatically authorize this repository's OIDC subject.

For Store publishing, set these **microsoft-store environment secrets**:

| Name | Value |
|---|---|
| `STORE_TENANT_ID` | Tenant ID of the Partner Center-associated Entra tenant |
| `STORE_CLIENT_ID` | Client ID of the Store publishing app, assigned the Manager role in Partner Center |

Create a separate federated credential on that Store app registration:

```text
Issuer:   https://token.actions.githubusercontent.com
Subject:  repo:DamianEdwards@249088/ghcp-spend-tray@1375200120:environment:microsoft-store
Audience: api://AzureADTokenExchange
```

`azure/login` uses this credential without a subscription or client secret,
and the workflow requests a token for `https://manage.devcenter.microsoft.com`
via Azure CLI. The Store publishing identity is distinct from the signing
identity used by **Release**. Store submission API documentation illustrates
client-secret authentication, but Entra supports federated client credentials.
Use the read-only access check below before the first Store submission.

No Azure client secret, PFX, or private key is needed. The workflow grants
`id-token: write`, logs in with `azure/login`, and signs through
`azure/artifact-signing-action` using Azure CLI credentials. It timestamps with
SHA-256. Signing is mandatory; missing or partial configuration stops release.
Only the outer `.msixbundle` needs signing: its signature covers the contained
architecture packages. Signature verification uses Windows SDK SignTool.

The `windows-2025` runner needs Visual Studio x64/ARM64 C++ tools and a Windows SDK
22621 or newer. `actions/setup-dotnet` installs the SDK pinned in `global.json`.
The same prerequisites apply locally. Node.js/npm is only needed to regenerate
checked-in artwork, not for .NET builds or packaging.

### Packaged shell icons

The approved connected-dollar SVG masters in `src\GHCPSpendTray.App\Assets`
are the source for the application ICO, in-app logo, badge, Store logos and
the `Square44x44Logo.targetsize-*` PNGs. Regenerate or check them with the
pinned, development-only renderer:

```powershell
npm --prefix .\tools ci --no-audit --no-fund
npm --prefix .\tools run generate
npm --prefix .\tools run check
```

The small optical master is used at 16-24 pixels; larger sizes use the regular
master. ICO frames embed the same PNG bytes as their shell counterparts.
Keep the default, `altform-unplated`,
and `altform-lightunplated` variants at all 14 sizes, even though both themes
use identical artwork. Transparent source pixels and the manifest's
`BackgroundColor="transparent"` alone do not prevent Windows from adding an
accent-color plate; the shell needs the qualified unplated candidates.
See [Windows app icon construction](https://learn.microsoft.com/windows/apps/design/iconography/app-icon-construction).

The app project copies these assets into each published payload.
`package.ps1` uses Windows SDK MakePri and `packaging\priconfig.xml` to generate
`resources.pri` after assigning the final package identity. This primary index
maps `Files/Assets/Square44x44Logo.png` to its size/theme candidates **and merges
the generated `GHCPSpendTray.pri`**, including WinUI/Reactor XAML and localized
resources, into the package's resource map. The original `GHCPSpendTray.pri`
and `Reactor.pri` must also remain in the package. Store builds retain Reactor
startup XAML here, but resolve WinUI's theme resources from the shared framework
instead of copying them into the app. An icon-only `resources.pri`
shadows runtime resource resolution and crashes packaged WinUI at startup,
even when all the separate PRI/XBF files are present.

`test-package.ps1` extracts and checks the actual icon PNGs, primary PRI and
startup XBF files from both architecture packages. Its startup-resource checks
run for development, signed GitHub and Store bundles. After changing resource
generation, also run the isolated packaged smoke test documented in
`docs/VALIDATION.md`: portable activation does not exercise this lookup path.

## Release a Windows version

On PRs and main pushes, a Linux change-detection job routes verification before
allocating native runners. Windows-only changes run Windows checks; Mac-only
changes run Mac checks on Apple silicon across macOS 26 and 15. Changes to shared C# code,
SDK/build configuration, CI routing, or unrecognized paths run both. Markdown
changes run the Markdown job without native builds. Manual Verify runs run all
checks. The stable **Verification** gate requires all applicable jobs to
succeed and fails if change detection fails. Selected Release jobs check that gate
on the pinned main commit and rerun their platform's verification before
publishing, even when its previous main checks were legitimately skipped.

Verify runs managed/Native AOT tests and packaging/startup smoke on separate
Windows runners after change detection. Each job caches only NuGet packages,
keyed by OS/architecture, job, SDK and dependency inputs; restores still run,
and build outputs are never cached. Builds and publishes remain sequential
within each checkout. Mac runners also cache NuGet packages by architecture.
The final **Verification** job requires both Windows jobs for Windows-relevant
changes, the Mac arm64 build plus older-macOS runtime compatibility for
Mac-relevant changes, and Markdown lint when
applicable. Every unneeded job must be skipped; missing or invalid routing
decisions fail closed. Failed change detection or Markdown
lint, cancelled jobs and unexpected skips fail the gate. Branch protection
and the release-source check continue to use the same **Verification** name.

1. Merge to `main` and wait for **Verify / Verification** to succeed for the
   exact source commit. For Windows-relevant changes, it runs JIT and x64 Native
   AOT tests, publishes both architectures, and builds and validates an unsigned
   development bundle. It then extracts the built x64 MSIX, registers the
   isolated development package, and runs both populated and empty synthetic
   packaged UI smoke scenarios. Crashes, missing results, nonzero exit codes
   and timeouts fail verification; the registration is removed afterward.
   Developer Mode is enabled only on the disposable hosted runner, and the
   `packaged-smoke-diagnostics-self-contained` and
   `packaged-smoke-diagnostics-store` artifacts retain the transcripts from
   the independent deployment-mode jobs. Both jobs publish and validate x64
   and ARM64; both run populated and empty x64 packaged startup smoke.
2. Run **Actions > Release > Run workflow** from `main`. Choose the Windows
   bump and set macOS to **no release** for a Windows-only release. Choose
   whether the selected releases are previews. The planning job calculates
   the versions using the rules above; no explicit version input is required.
   Every release, including previews, needs a higher numeric package version.
3. Approve the `production` environment. The workflow pins the selected commit,
   rechecks verification, rebuilds, signs, verifies, attests, tags that source,
   and uploads a draft release. It downloads the assets and verifies their bytes,
   package signature and bundle attestation before making the release public.

Tags are `v0.2.0`; the MSIX version is `0.2.0.0`. The final component stays zero
for Store compatibility. Release versions are supplied as build properties,
so the tag points to the exact verified source without a generated version commit.
The project version is only a local development default.

Published assets:

- `GHCPSpendTray-<version>.msixbundle` -- signed x64 and ARM64 application bundle.
- `GHCPSpendTray-<version>-symbols.zip` -- native debug symbols, outside the app.
- `SHA256SUMS` -- hashes calculated after signing.
- `release.json` -- version, identity, source commit and originating workflow run ID.

GitHub artifact attestations are associated with the bundle and symbols archive.
A failed run leaves a draft, not an unsigned public release. Rerun all jobs of
the original workflow to restore the same source/version; it may replace draft assets but
never replaces a public release or moves a tag. If a later version has already
been tagged, release a new higher version instead.

## Local build and verification

This section covers Windows; macOS instructions follow below.

```powershell
.\tools\verify.ps1 -NativeTests
.\tools\package.ps1 -Version 0.2.0 `
  -IdentityName 'GHCPSpendTray' `
  -Publisher 'CN=<exact certificate subject>' `
  -PublisherDisplayName '<publisher display name>'
```

The placeholder values above must be replaced; they are not signing credentials.
Local output is unsigned. Do not distribute it as a signed release.
`-SkipPublish` packages existing matching-version payloads; use it only after
publishing both architectures from the same source and in the same deployment
mode. GitHub and local development builds remain self-contained by default.

Packaging uses an explicit source manifest and Windows SDK `MakeAppx`, not a
capture of an existing installation. This preserves the component-only Windows
App SDK graph and Native AOT PRI/XBF resource handling without introducing the
umbrella SDK solely for Visual Studio single-project packaging.
The app remains full-trust and Native AOT in both deployment modes.

The Windows app project sets `OptimizationPreference` to `Size` for x64 and
ARM64 in both deployment modes. This favors smaller executables and enables
Native AOT data dehydration on Windows, with potential startup, throughput,
and memory tradeoffs.

Use `.\tools\package.ps1 -Store` for a framework-dependent Store build, with
the assigned Partner Center identity arguments for submission. The conditional
`Microsoft.WindowsAppSDK.Runtime` reference does not add the umbrella SDK.
Packaging reads the resolved architecture-specific Microsoft framework's
manifest to set its exact name, publisher and minimum version as a
`PackageDependency`. The Store installs that dependency; no separate .NET
runtime is required. The bootstrapper is a no-op under package identity, while
unpackaged synthetic smoke runs require the runtime to be installed.

Store payloads, symbols, layouts and bundles use `artifacts\publish-store`,
`symbols-store`, `msix-store` and `release-store`, separately from the default
GitHub/development outputs. Store validation requires the declared framework
and rejects app-local WinUI/DWrite runtime DLLs. Staging also requires that
mode; a self-contained bundle cannot accidentally be submitted as a Store build.
The Store publish keeps its resolved NuGet metadata outside the payload at
`artifacts\publish-store\project.assets.json`, so later self-contained restores
do not change the dependency used by `-Store -SkipPublish` or staging.
The first installation may still need to download the shared framework, which
includes more components than our slim self-contained payload. The smaller app
package primarily benefits runtime reuse and subsequent app updates.

With Developer Mode already enabled and permission to register/remove an isolated
test package, build with the default development identity and run:

```powershell
.\tools\package.ps1
powershell.exe -NoProfile -File .\tools\smoke-test-package.ps1
```

The script does not enable Developer Mode, install certificates, or change startup
preferences. Production signing, clean signed installation/upgrading, login
startup, ARM64 execution and Store certification require separate release checks.
See [VALIDATION.md](VALIDATION.md).

## Submit an update to the Microsoft Store

Version 0.1.0 was published manually in the Store. For subsequent updates,
configure the Partner Center-associated Entra application with the Manager role
and the federated credential above; do not create a client secret for this
workflow. The app must be live and support the Store submission API.
Microsoft currently supports automated updates for free products.

1. Merge the workflow to `main`, then choose **Actions > Store Package and
   Publish > Run workflow** from `main`. Leave **version** empty to use GitHub's
   highest-version published non-prerelease **Windows** release. The selector
   paginates releases and excludes Mac tags, drafts, and previews rather than
   trusting GitHub's repository-wide "latest" release. To target another immutable release,
   including a prerelease, enter its version without the `v` prefix, such as
   `0.2.0`. The Store package version must still exceed the last published
   version; rerunning an already submitted release is not a safe retry. For the
   first run, turn **publish** off and **verify_access** on to test OIDC and read
   the live Store product without creating a draft. After that succeeds, run
   again with **publish** on to submit the update. Turn both inputs off to build
   a manual upload artifact without using the Store publishing identity.
2. Approve the `microsoft-store` environment. The workflow resolves the selected
   release, verifies its metadata and immutable tag, checks out that exact
   application commit separately from the packaging automation, and installs
   its pinned SDK. It derives the Store notes from the same immutable GitHub
   release's **What's Changed** PR titles, dropping contributor credits and
   the full-changelog link. Unexpected formatting, empty notes or more than
   1500 characters stop the workflow before any Store draft is created.
3. Both architectures are rebuilt with `-Store` and the Partner Center identity,
   not the Azure certificate's publisher. The selected Windows release must
   include this Store deployment support; older immutable source is not patched
   by the workflow. The workflow validates the unsigned framework-dependent bundle,
   including publisher display name, and uploads the
   `GHCPSpendTray-<version>-store` Actions artifact before submission.
4. With **publish** enabled, the workflow signs in via GitHub OIDC and verifies
   the live Store product and published package version. It refuses to replace
   an existing pending draft,
   an unpublished predecessor, or a package with an equal or higher version.
   It creates a new submission copying the published listing and availability,
   sets the `en-us` release notes to the derived plain text, marks the copied
   published packages `PendingDelete` while retaining their file entries, adds
   this bundle as `PendingUpload`, verifies the Store retained the package
   changes and notes, uploads the bundle in a ZIP through the Store API, and
   commits the update for certification. A single `en-us` listing is required; other
   locales need reviewed translations rather than silently reusing English.
   Publish mode is **Immediate**, so an approved update goes live without a second
   manual release action. Check Partner Center for certification and publication.
   The workflow polls the Store status for up to ten minutes after `CommitStarted`.
   `PreProcessing` or a later documented status confirms commit acceptance even if
   intermediate states were missed between polls. Known failure states stop the
   run with Store details; a timeout, unreadable response or unknown status leaves
   the outcome **indeterminate**, not safe to resubmit. A successful workflow run
   means the commit was accepted, **not** that the update was approved or is live.

If a submission/upload fails after draft creation, inspect and resolve that
draft in Partner Center before rerunning; automation does not delete drafts or
retry a partial submission. Do not edit an API-created draft in Partner Center
and then try to commit it with the API: Microsoft warns that mixing methods can
leave the draft unusable. Resolve or delete it manually, then start a new run.
Store API authentication and product validation failures stop before draft
creation. If **publish** is disabled, download and extract the artifact, then
upload only `GHCPSpendTray-<version>-store.msixbundle` to Partner Center;
`store-package.json` and `SHA256SUMS` are traceability files, not app packages.

To inspect an existing submission's asynchronous commit outcome without
changing it, run this on your own machine with PowerShell 7 and a client secret
for the Partner Center-associated Entra application:

```powershell
.\tools\get-store-submission-status.ps1 -TenantId '<tenant-guid>' `
  -ClientId '<app-client-guid>' -StoreId '<store-product-id>' `
  -SubmissionId '<submission-id>'
```

The script prompts privately for the secret (never pass it on the command line),
obtains a Store API token, and makes only a `GET` request to the submission
status endpoint. Inspect `StatusDetails.errors` for failure statuses, including
`CommitFailed` and `PreProcessingFailed`. This temporary local credential is
separate from the secretless GitHub OIDC workflow; do not commit or save it,
and revoke it when diagnostics are complete. A `CommitStarted` response to the
workflow's commit request does not establish that the commit succeeded. If
polling cannot confirm acceptance, inspect the existing submission before
rerunning the publish job; it never deletes or automatically resubmits a draft.

The Store bundle is intentionally unsigned and is **not a direct-install
download**. Microsoft signs it during Store publication. The signed bundle on
GitHub Releases has a different publisher and must not be uploaded instead.
This produces a Store-identity build from released source, not a byte-identical
copy of the signed GitHub package.

For an initial submission, complete Partner Center's pricing/availability, properties,
age ratings, privacy policy, listing text/screenshots, and notes explaining the
full-trust tray application and how certification can evaluate its sign-in
experience. Describe data handling and the undocumented GitHub consumption API
accurately. The approved policy is maintained in [PRIVACY.md](../PRIVACY.md).
Partner Center also accepts the policy text directly in Properties.
No compliance declarations or listing claims are filled automatically.
Run the Windows App Certification Kit and resolve package/listing issues before
submitting. A successful package build does not establish Store certification.

Actions artifacts expire after 30 days. Rerun with **publish** disabled to
regenerate a package from the same immutable source. Each subsequent Store
update needs a higher numeric package version; a prerelease label alone does
not increase it. The Store submission API copies the previously published
listing: review existing listing text before automating an update. Only the
`en-us` release notes, package and publish mode are changed. For version 0.2.0,
the five GitHub **What's Changed** titles become the Store release notes;
review the release body before submission.

The **Verify** workflow exercises self-contained development and
framework-dependent Store packaging for x64/ARM64, including dependency and
resource mutation regressions. On the disposable hosted runner it installs the
resolved x64 Microsoft framework when needed and runs packaged smoke scenarios
for both modes, then stages a synthetic Store identity without production
credentials or API calls.
The offline release-selection tests reject drafts, mutable releases, missing or
duplicate assets, mismatched source/version metadata, and GitHub API failures.

## macOS development and releases

### Local macOS builds

Support follows a two-major-version window: **macOS 26 and macOS 15**, using
their latest patch releases, on **Apple silicon only**. Intel Macs are no
longer supported by current builds. Previously published universal releases
remain unchanged historical assets, not current-support evidence.
macOS 14 Sonoma is no longer supported. Advance
this window, the deployment targets, metadata and runtime checks together when
adopting a new stable major release.

Use an Apple-silicon Mac with native arm64 tools (not under Rosetta), a
supported macOS version, the .NET SDK in `global.json`, Python 3, and a stable
Swift 6 compiler/Apple SDK. CI and release jobs use **Xcode 26.6 with the
macOS 26.5 SDK**, selected explicitly by `.github/actions/setup-macos` on
`macos-26` runners, rather than inheriting runner defaults.
The minimum deployment target is macOS 15; the build SDK is independent
of the oldest supported runtime. Routine verification has exactly two Mac jobs:

| Runtime target | Work |
|---|---|
| macOS 26 / Apple silicon (`macos-26`) | Shared tests, one arm64 app build, native smoke tests |
| macOS 15 / Apple silicon (`macos-15`) | Download that exact arm64 artifact, verify executable and bridge slices, native smoke tests without rebuilding |

Both jobs use the latest available hosted-runner images for their OS major and
log the actual patch version and assert native arm64 execution. These labels
are the arm64 images listed in GitHub's
[hosted-runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
They cover both supported OS generations on Apple silicon. Both exercise
the real read-only notification-settings callback and remain gated on relevant
Mac/shared-code and build-tooling changes.

For the closest local/release match, install Xcode 26.6 and run:

```bash
DEVELOPER_DIR=/Applications/Xcode_26.6.app/Contents/Developer bash tools/macos/verify.sh
```

Adjust the path if Xcode was installed under a different name. Command Line
Tools are also supported for local development, but their compiler version may
differ from CI; the workflow logs its actual Xcode, Swift, and SDK versions.
`xcode-select --install` installs the tools if missing. No .NET platform
workload or third-party UI package is required.

```bash
bash tools/macos/verify.sh
open artifacts/macos/GHCPSpendTray.app
```

The build publishes the C# Native AOT shared library for `osx-arm64` and
compiles the SwiftUI frontend for `arm64-apple-macos15.0`. It replaces the
bundle's executable and bridge outright, including when reusing an old
universal output directory. Package and signing checks require **exactly
arm64** in both Mach-O binaries; Intel-only and universal artifacts fail.
Unsupported hosts and the removed smoke `--arch` argument fail explicitly.
Its ICNS uses the shared dollar artwork and the optically tuned small PNGs,
not a separate Mac logo. Both binaries and their runtime are bundled;
users need no .NET installation. macOS 15 is
the minimum deployment target. Keychain, notifications, and login-item calls
are implemented natively in Swift; the shared C# application controller handles
auth, storage, scheduling, accounting, and alert decisions. There is no IPC
server, web frontend, or helper process.

`packaging/macos/version.txt` is only the local Mac development default.
`bash tools/macos/build.sh 0.2.0 Preview` overrides version/channel locally.
The Windows project's development version is independent.
If preview Command Line Tools select an SDK that lacks the SwiftUI macro
plugin, the script selects their installed stable `MacOSX26.sdk` instead.
You can explicitly set a stable SDK without changing global developer tools:

```bash
SDKROOT="$(xcrun --sdk macosx26.5 --show-sdk-path)" bash tools/macos/verify.sh
```

Use an SDK actually installed on your machine. Local output is **ad-hoc signed
development software**, not notarized public distribution. Do not change
Gatekeeper settings or install a local trust certificate to distribute it.

### Apple signing configuration

Use the existing protected **production** GitHub environment shared with
Windows releases, restricted to `main` and requiring a reviewer. Add the
following Apple settings there. An Apple Developer Program membership and a
**Developer ID Application** certificate/private key are required, but there
is no Mac App Store app, entitlement, or submission workflow.

| Environment setting | Purpose |
|---|---|
| Variable `MACOS_SIGNING_IDENTITY` | Exact `Developer ID Application: ... (TEAMID)` identity or certificate SHA-1 fingerprint |
| Secret `MACOS_CERTIFICATE_P12` | Base64-encoded exported Developer ID certificate **and private key** |
| Secret `MACOS_CERTIFICATE_PASSWORD` | Password protecting that P12 |
| Secret `MACOS_NOTARY_KEY` | Contents of a team App Store Connect API `.p8` key authorized for notarization |
| Secret `MACOS_NOTARY_KEY_ID` | API key ID |
| Secret `MACOS_NOTARY_ISSUER` | API issuer ID |

These credentials cannot be generated from source or inferred from Windows
signing configuration. Store them in environment secrets, not workflow files,
repository files, issue comments, or command transcripts. API-key use here is
only for the notarization service, not App Store submission.

The workflow imports the certificate into a temporary unlocked Keychain,
signs the native library and app inside-out with hardened runtime and a secure
timestamp, and notarizes/staples the app. No JIT or disabled library-validation
entitlement is needed for Native AOT. It then creates, signs, notarizes and
staples a DMG containing the app and an Applications link. Temporary signing
credentials/Keychain are removed by the script's exit trap.
The temporary signing Keychain is added to the user's search list for
`codesign` identity/key and certificate-chain lookup, then removed on exit.
Existing search entries, including entries added during signing, are preserved.
Passing `codesign --keychain` alone is insufficient for all lookup paths.

Each of the two notarizations has a 60-minute wait ceiling; the Mac release
job has a 180-minute total ceiling for both submissions plus builds and packaging.
Successful submissions return immediately. Submission receipts are saved before
waiting and retained with status JSON in a `macos-notarization-<run>-<attempt>`
Actions artifact, even on failure. A timeout does not reject or cancel the
submission at Apple. Inspect or continue waiting on its recorded ID with
`notarytool info` / `notarytool wait` before starting another upload. A new run
does not automatically resume an earlier submission; do not blindly rerun after
a wait timeout. The **Notarization Status** workflow can inspect the recorded
UUID from `main` using the same protected production credentials; optionally
enable its wait input to continue waiting for up to 60 minutes. It never signs,
uploads, or publishes anything, and retains status (and rejection logs when
available) as an Actions artifact. Timeout changes apply only to new workflow
code/runs.

The signing step reports each preparation/signing/notarization stage and checks
the imported identity before configuring private-key access. A P12 without a
matching private key, a mismatched `MACOS_SIGNING_IDENTITY`, and an untrusted
certificate chain produce distinct errors. GitHub secret names being present
does not validate their contents. Export the **Developer ID Application**
certificate with its private key from **login > My Certificates**, not just
the downloaded `.cer`; the identity selector must refer to that certificate,
not Apple's intermediate. Fix chain issues with the genuine Apple intermediate
and system-default trust, never an **Always Trust** override.

### Publish a macOS version

1. Merge to `main` and wait for **Verify / Verification** on the exact commit.
   Mac-relevant changes run managed and native shared tests plus an arm64
   build/smoke check with the release toolchain on macOS 26 / Apple silicon,
   plus the same artifact on macOS 15 / Apple silicon without rebuilding.
2. Dispatch **Release** from `main`, choose the macOS bump, and set Windows
   to **no release** for a Mac-only release. Choose the preview designation,
   review the calculated versions, and approve the **production** deployment.
   macOS version components are limited to major `0..9999`, minor/patch
   `0..99`; `0.0.0` is forbidden. A bump exceeding these limits fails rather
   than wrapping to a different component.
3. The workflow validates source/version, reruns Mac verification, rebuilds with
   that version, signs/notarizes, exercises the hardened app with synthetic
   data, attests the DMG, and creates a `macos-v<version>` draft release.
   It downloads the draft assets and verifies their exact bytes, notarization,
   Gatekeeper acceptance and attestation before publishing.

Mac release assets are `GHCPSpendTray-macOS-<version>.dmg`,
`release-macos.json`, and `SHA256SUMS`. Metadata records platform, version,
source commit, originating workflow run ID, bundle identifier, architectures,
and notarization. Current metadata requires `architectures: ["arm64"]`;
universal/Intel metadata is rejected before draft staging. Mac releases
use `--latest=false` so they do not displace the repository's Windows "latest"
download. Store selection additionally filters to Windows tags and does not
rely on that convention.

The bundle identifier is `com.damianedwards.GHCPSpendTray`; keep both it and
the signing team stable across updates. The app's data directory is
`~/Library/Application Support/GHCPSpendTray`; OAuth credentials are
non-synchronizing, device-local login Keychain items under that service,
partitioned by OAuth registration and canonical host/user identity.

Missing signing configuration or a rejected notarization stops publication.
A failed release can leave a draft/tag; rerun all jobs of the original workflow
to restore its version plan. A public release is never replaced and an existing tag
cannot move. Newer tags require a new higher version within the same platform.
Before production acceptance, complete the Mac checklist in
[VALIDATION.md](VALIDATION.md), including clean install/update, login startup,
Keychain prompts, notification permissions, and VoiceOver.

## Linux main-branch signing

Linux development signing is automatic after a relevant `main` push passes the
entire Verify gate. It is separate from the manual Windows/macOS Release workflow
and does not create a release tag or GitHub Release. The Linux lane currently
produces x86-64 artifacts; signing does not establish ARM64 acceptance.

Before merging, configure the existing **production** GitHub environment:

| Kind | Name | Value |
| --- | --- | --- |
| Environment secret | `LINUX_SIGNING_KEY_BASE64` | Base64-encoded export of a dedicated, passphrase-protected OpenPGP signing private key/subkey |
| Environment secret | `LINUX_SIGNING_KEY_PASSPHRASE` | Its nonempty, single-line passphrase |
| Environment variable | `LINUX_SIGNING_FINGERPRINT` | Full fingerprint of the exact signing key/subkey, not a short ID or a different primary key |

Restrict environment deployment branches to `main`, retain required reviewers
where appropriate, and protect changes to the workflow/signing scripts. Existing
environment approvals still apply: automatic scheduling does not bypass them.
Never give signing secrets to PR jobs. Do not reuse an everyday personal key or
commit private material. Keep an offline backup/revocation plan and rotate the
key before expiry. Azure Authenticode and Apple Developer ID credentials are not
Linux OpenPGP signing keys.

Create or select the dedicated signing key outside the checkout, then upload it
directly without writing a secret export into the repository. For example, after
setting `SIGNING_FINGERPRINT` to the full signing-key fingerprint:

```bash
gpg --armor --export-secret-subkeys "$SIGNING_FINGERPRINT" |
  base64 -w 0 |
  gh secret set LINUX_SIGNING_KEY_BASE64 --env production --repo DamianEdwards/ghcp-spend-tray
gh secret set LINUX_SIGNING_KEY_PASSPHRASE --env production --repo DamianEdwards/ghcp-spend-tray
gh variable set LINUX_SIGNING_FINGERPRINT --env production \
  --repo DamianEdwards/ghcp-spend-tray --body "$SIGNING_FINGERPRINT"
```

Use `--export-secret-keys` instead of `--export-secret-subkeys` only when the
dedicated signing key is itself the primary key. The passphrase command prompts
for the secret; do not put it on the command line. Publish the corresponding
public key and exact fingerprint through a trusted project channel.

The `linux_sign` job downloads the unsigned artifact from the **same** successful
run and checks its build checksum. It signs the existing AppImage, without
executing or repacking it, using the standard `.sha256_sig` and `.sig_key`
sections. GnuPG performs signing and verification in disposable isolated
keyrings. The verifier pins the configured fingerprint and rejects expired,
revoked, invalid or mismatched signatures. All other bytes, including the
runtime and compressed payload, must remain unchanged.

Only after embedded and detached signature verification, GitHub attestation
creation, and provenance verification against this workflow's main-branch source
commit does the job upload **linux-signed** (30-day retention). It contains the
AppImage, detached `.asc` signature, post-signing `.sha256`, public key,
`SIGNING.txt` and `provenance.jsonl`. Missing credentials fail the job: there is
no unsigned fallback for this artifact. `linux-devel` remains a clearly separate
unsigned CI/build input. Local builds, PR runs, feature-branch pushes and manual
Verify runs do not produce publisher-signed artifacts.

Before executing a downloaded image, verify GitHub provenance:

```bash
gh attestation verify GHCPSpendTray-linux-devel-x86_64.AppImage \
  --repo DamianEdwards/ghcp-spend-tray \
  --signer-workflow DamianEdwards/ghcp-spend-tray/.github/workflows/verify.yml \
  --source-ref refs/heads/main
```

Then verify the embedded signature using this repository's verifier and the
independently trusted fingerprint:

```bash
python3 tools/linux/sign_appimage.py verify GHCPSpendTray-linux-devel-x86_64.AppImage \
  --fingerprint "$SIGNING_FINGERPRINT"
```

Alternatively, import the trusted public key into your chosen verification
keyring and use `gpg --verify IMAGE.AppImage.asc IMAGE.AppImage`. A bundled
public key alone is not proof of publisher identity. A SHA-256 checksum is not a
signature, and `--appimage-signature` only displays one. AppImage signatures are
not enforced automatically by the desktop and do not remove FUSE/executable
permission requirements. Signed development artifacts are not yet versioned
production Linux releases or an automatic update channel.

## Updates

A GitHub-hosted `.appinstaller` feed is possible: a stable HTTPS descriptor can
reference versioned release URLs for the bundle and request App Installer update
checks. It adds feed publication, identity/version coordination, HTTP behavior and
long-running-process update testing. It is deliberately not implemented now.
GitHub releases are manually installed/upgraded; no updater or update prompt runs.

For Store publishing, the Store signs the submitted MSIX packages and manages
updates; Azure signing remains for direct GitHub distribution. Store API access
is separate from Azure Artifact Signing. The workflow uses the submission API
directly rather than the Store CLI's `publish` command, which can delete an
existing pending draft and discard staged metadata.
The undocumented consumption API and host-specific OAuth support also remain
product/review risks independent of packaging.

References:
[MSIX signing](https://learn.microsoft.com/windows/msix/package/signing-package-overview),
[Store submission API prerequisites](https://learn.microsoft.com/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services),
[Store submission API process](https://learn.microsoft.com/windows/uwp/monetize/manage-app-submissions),
[Store update prerequisites](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions).
