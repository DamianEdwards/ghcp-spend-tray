# GHCPSpendTray releases

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
4. For eventual Store packages, pass these exact values to `tools\package.ps1`.
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

Environment **variables**:

| Name | Value |
|---|---|
| `MSIX_IDENTITY_NAME` | Stable production package identity name |
| `MSIX_PUBLISHER` | Exact Azure signing certificate subject, including all DN components |
| `MSIX_PUBLISHER_DISPLAY_NAME` | Human-readable publisher name |

The production environment also retains `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`,
`STORE_PUBLISHER_DISPLAY_NAME`, and `STORE_ID` from Partner Center. **Store Package**
uses these values to build upload artifacts; **Release** does not consume them.
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

No Azure client secret, PFX, or private key is needed. The workflow grants
`id-token: write`, logs in with `azure/login`, and signs through
`azure/artifact-signing-action` using Azure CLI credentials. It timestamps with
SHA-256. Signing is mandatory; missing or partial configuration stops release.
Only the outer `.msixbundle` needs signing: its signature covers the contained
architecture packages. Signature verification uses Windows SDK SignTool.

The `windows-2025` runner needs Visual Studio x64/ARM64 C++ tools and a Windows SDK
22621 or newer. `actions/setup-dotnet` installs the SDK pinned in `global.json`.
The same prerequisites apply locally. Python is only needed to regenerate
checked-in artwork, not for CI builds.

## Release a version

On Markdown-only pushes and pull requests, **Verify / Verification** lints
Markdown without running the SDK setup, build, tests, or packaging. Other
changes and manual Verify runs perform the full checks; the Release workflow
also verifies its source before publishing.

1. Merge to `main` and wait for **Verify / Verification** to succeed for the
   exact source commit. For non-Markdown changes, it runs JIT and x64 Native
   AOT tests, publishes both architectures, and builds and validates an unsigned
   development bundle.
2. Run **Actions > Release > Run workflow** from `main`. Supply an increasing
   three-part version, such as `0.2.0`, and select whether it is a prerelease.
   Every release, including previews, needs a higher numeric package version;
   preview labels do not participate in MSIX version ordering.
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
- `release.json` -- version, identity and source commit.

GitHub artifact attestations are associated with the bundle and symbols archive.
A failed run leaves a draft, not an unsigned public release. Rerun the original
workflow run to resume the same source/version; it may replace draft assets but
never replaces a public release or moves a tag. If a later version has already
been tagged, release a new higher version instead.

## Local build and verification

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
publishing both architectures from the same source.

Packaging uses an explicit source manifest and Windows SDK `MakeAppx`, not a
capture of an existing installation. This preserves the component-only Windows
App SDK graph and Native AOT PRI/XBF resource handling without introducing the
umbrella SDK solely for Visual Studio single-project packaging.
The app remains full-trust, self-contained, and Store-shaped.

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

## Build a Store submission package

The first submission has been sent for certification; Store publication has not
yet been confirmed. The **Store Package** workflow
automates artifact preparation only. It never calls Partner Center, modifies a
draft, submits certification, signs with Azure, or publishes a GitHub release.
It needs no additional credentials beyond the existing `production` variables.

1. Merge this workflow to `main`, then choose **Actions > Store Package > Run
   workflow** from `main`.
2. Enter an existing immutable GitHub release version without the `v` prefix,
   such as `0.1.0`. A preview release is allowed when explicitly selected.
3. Approve the `production` environment. The workflow verifies the published
   release's source metadata against its immutable tag, checks out that exact
   application commit separately from the packaging automation, and installs
   the SDK pinned by that released source.
4. Both architectures are rebuilt with the Partner Center identity, not the
   Azure certificate's publisher. The workflow validates the unsigned bundle,
   including the publisher display name, and uploads an Actions artifact named
   `GHCPSpendTray-<version>-store`.
5. Download and extract that artifact. Upload only
   **`GHCPSpendTray-<version>-store.msixbundle`** to the **Packages** section of
   your Partner Center submission. The artifact also includes
   `store-package.json` (Store identity and source commit) and `SHA256SUMS`
   for traceability; do not upload the outer Actions ZIP as an app package.

The Store bundle is intentionally unsigned and is **not a direct-install
download**. Microsoft signs it during Store publication. The signed bundle on
GitHub Releases has a different publisher and must not be uploaded instead.
This produces a Store-identity build from released source, not a byte-identical
copy of the signed GitHub package.

For an initial submission, complete Partner Center's pricing/availability, properties,
age ratings, privacy policy, listing text/screenshots, and notes explaining the
full-trust tray application and how certification can evaluate its sign-in
experience. Describe data handling and the undocumented GitHub consumption API
accurately. The approved policy is maintained in [PRIVACY.md](PRIVACY.md).
Partner Center also accepts the policy text directly in Properties.
No compliance declarations or listing claims are filled automatically.
Run the Windows App Certification Kit and resolve package/listing issues before
submitting. A successful package build does not establish Store certification.

Actions artifacts expire after 30 days. Rerun the workflow to regenerate a package
from the same immutable source. Each *subsequent Store update* needs a higher
numeric package version; a prerelease label alone does not increase it.

The **Verification** CI job exercises both development and Store-shaped packaging,
using a synthetic Store identity without production credentials or API calls.
The offline release-selection tests reject drafts, mutable releases, missing or
duplicate assets, mismatched source/version metadata, and GitHub API failures.

## Updates and eventual Store submission automation

A GitHub-hosted `.appinstaller` feed is possible: a stable HTTPS descriptor can
reference versioned release URLs for the bundle and request App Installer update
checks. It adds feed publication, identity/version coordination, HTTP behavior and
long-running-process update testing. It is deliberately not implemented now.
GitHub releases are manually installed/upgraded; no updater or update prompt runs.

For Store publishing, the Store signs the submitted MSIX packages and manages
updates; Azure signing remains for direct GitHub distribution. Store API access is
not an Azure Artifact Signing key. It uses a separate Partner Center-associated
Entra application, tenant/client ID and credential with the required submission
permissions. Microsoft's documented flow assigns that application the Manager
role and obtains a client key. Keep such credentials in a protected environment,
never in the repository or chat.

Microsoft's current
[Store GitHub Actions guidance](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions)
requires the application to be published and live before automating updates.
Complete the first submission above before enabling later Store API automation.
Future setup requires a Partner Center-associated Entra application and Store
credentials, separately from the Azure signing identity. Do not paste a client
secret into chat; configure it directly in a protected GitHub environment.

Store submission is not wired into these workflows yet. The Store CLI's `publish`
command can delete and recreate a pending draft, losing staged metadata; the
future update workflow must explicitly safeguard existing submissions rather
than running that command blindly. Reserving a name alone does not authorize publication.
The undocumented consumption API and host-specific OAuth support also remain
product/review risks independent of packaging.

References:
[MSIX signing](https://learn.microsoft.com/windows/msix/package/signing-package-overview),
[Store submission API prerequisites](https://learn.microsoft.com/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services).
