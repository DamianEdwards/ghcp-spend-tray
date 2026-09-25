# GHCPSpendTray implementation scope

This document records the current product/deployment scope, not proof of release
acceptance. See [VALIDATION.md](VALIDATION.md) for evidence and open gates.

## Application

- Windows 11 22H2+ full-trust desktop tray application.
- C#, .NET 10 Native AOT, Microsoft UI Reactor and native WinUI 3 controls.
- Cost-first flyout anchored to the Shell tray icon, separate settings window,
  explicit Exit, and idempotent repeated activation.
- Multiple identities keyed by normalized GitHub host and immutable user ID.
- Host-specific public OAuth clients, device-code sign-in, no embedded secret,
  no borrowing credentials from another application.
- Secretless refresh where supported and explicit host/SSO/policy failures.
- Decimal AI-credit consumption accounting; unknown is never silently zero.
- Per-account percentage and dollar-increment alerts with persistent deduplication.
- Versioned JSON configuration/state and bounded JSONL history with recovery.
- Credentials in Windows Credential Manager; no token/device-code logging.

## Packaging and deployment

- Product, namespaces, projects, executable and UI use **GHCPSpendTray**.
- Source-defined MSIX package containing the self-contained WinUI/AOT payload.
- x64 and ARM64 packages in one `.msixbundle`.
- Windows SDK packaging of publish output preserves component-only SDK references
  and required PRI/XBF resources; no captured installer or umbrella SDK dependency.
- Read-only installed binaries and package-local application data.
- Opt-in manifest-declared Windows `StartupTask`; respect external and policy
  state, and launch quietly in the tray at login.
- No old-format compatibility, data migration, self-installation or self-update.
- Explicit portable mode retained only for isolated development/testing.

## Release automation

- Verify workflows on pull requests, `main`, and manual dispatch.
- Protected `production` release environment with pinned actions.
- Explicit increasing numeric version, tagged verified source, both native builds.
- Mandatory Azure Artifact Signing using GitHub OIDC; no PFX/private key storage.
- Signed bundle, symbols archive, checksums and source/identity metadata.
- GitHub attestations and draft upload/download verification before publication.
- Never overwrite public release assets or move an existing tag.
- Build-only Store Package workflow rebuilds an immutable release's exact source
  with Partner Center identity for the first manual Store submission. It does not
  contact Partner Center or alter pending submissions.
- Direct-distribution publisher must match the signing certificate subject.
- Store identity is assigned in Partner Center and may be a different package
  family from direct distribution. No automatic transition is assumed.

## Deferred

- Microsoft Store submission automation, after product reservation, enrollment,
  initial listing/submission and API authorization.
- Automatic GitHub update checks through `.appinstaller`.
- Rich app notifications/activation replacing the current Shell notifications.
- Additional enterprise-host registrations and live compatibility confirmation.

See [RELEASING.md](RELEASING.md) for reservation and release setup instructions.
