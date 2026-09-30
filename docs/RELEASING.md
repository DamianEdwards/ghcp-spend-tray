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
The same prerequisites apply locally. Python is only needed to regenerate
checked-in artwork, not for CI builds.

### Packaged shell icons

`python .\tools\generate-logo.py` regenerates the existing coin/bar artwork and
the `Square44x44Logo.targetsize-*` PNGs. Keep the default, `altform-unplated`,
and `altform-lightunplated` variants at all 14 sizes, even though both themes
use identical artwork. Transparent source pixels and the manifest's
`BackgroundColor="transparent"` alone do not prevent Windows from adding an
accent-color plate; the shell needs the qualified unplated candidates.
See [Windows app icon construction](https://learn.microsoft.com/windows/apps/design/iconography/app-icon-construction).

The app project copies these assets into each published payload.
`package.ps1` uses Windows SDK MakePri and `packaging\priconfig.xml` to generate
`resources.pri` after assigning the final package identity. This shell index
maps `Files/Assets/Square44x44Logo.png` to its size/theme candidates. It is
separate from the existing `GHCPSpendTray.pri` and `Reactor.pri` runtime
resources, which must remain in the package. `test-package.ps1` extracts and
checks the actual icon PNGs and shell PRI from both architecture packages.

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

## Submit an update to the Microsoft Store

Version 0.1.0 was published manually in the Store. For subsequent updates,
configure the Partner Center-associated Entra application with the Manager role
and the federated credential above; do not create a client secret for this
workflow. The app must be live and support the Store submission API.
Microsoft currently supports automated updates for free products.

1. Merge the workflow to `main`, then choose **Actions > Store Package and
   Publish > Run workflow** from `main`. Leave **version** empty to use GitHub's
   latest published non-prerelease release. To target another immutable release,
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
3. Both architectures are rebuilt with the Partner Center identity, not the
   Azure certificate's publisher. The workflow validates the unsigned bundle,
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

The **Verification** CI job exercises both development and Store-shaped packaging,
using a synthetic Store identity without production credentials or API calls.
The offline release-selection tests reject drafts, mutable releases, missing or
duplicate assets, mismatched source/version metadata, and GitHub API failures.

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
