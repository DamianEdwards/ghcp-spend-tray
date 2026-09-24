# Windows platform services

GHCPSpendTray is a full-trust MSIX desktop app. Windows owns installation,
upgrades, Start menu registration, startup registration, and removal.
There is no self-installer, updater, installer handoff, or legacy data migration.

## Bootstrap and lifetime

Call `Bootstrap.Start(args)` on the native UI thread. A null return means another
instance accepted activation. Retain and dispose `BootstrapRuntime` on that same
thread because it owns a mutex.

- `DataDirectory` is `ApplicationData.Current.LocalFolder\Data` for packaged apps.
  Preserve the Windows-managed ACL on package storage; private ACL enforcement
  applies only to explicit portable directories.
- `IsStartup` reflects the package's startup extension `--startup` argument.
  Startup activation must not show the flyout.
- `IsPortable` disables Windows startup integration and isolates development data.
- Register the activation callback before `SignalReady()`, which is called after
  successful Shell HWND/tray icon creation. Callbacks run on a worker thread;
  marshal UI operations through Reactor's dispatcher.
- Subscribe to `Diagnostic` for IPC failures. Early activations are coalesced
  until the callback and UI readiness are both available.

Mutexes and pipes are restricted to the current user. Packaged instances are
scoped by user and package family; portable instances by user and data directory.
The activation protocol accepts only a fixed activation command, not arbitrary
paths, commands, or exit requests.

Unpackaged normal launches fail visibly. Development launches must explicitly use
`--portable --data-dir <absolute-local-directory>`. Profile roots, volume roots,
and the package data tree are rejected as portable data directories.

## Windows startup task

`packaging\AppxManifest.xml` declares `GHCPSpendTrayStartup`, disabled initially,
with `--startup` parameters. `StartupRegistration` wraps Windows `StartupTask`.
It reads actual OS state, not a saved JSON preference. Settings handles enabled,
disabled, user-disabled, and both policy-controlled states. No registry Run or
StartupApproved values are written.

Only an explicit user setting change invokes `RequestEnableAsync` or `Disable`.
A declined request is an error rather than reported success. External user
disablement can only be reversed through Windows startup settings. Reopening or
activating the Settings window refreshes the toggle from Windows state.

## Credentials and testing

`CredentialVault` uses source-generated P/Invoke and generic Windows Credential
Manager targets beginning `GHCPSpendTray/`. Production targets also include the
OAuth registration and account identity; portable targets include a data-directory
hash. No tokens are stored in settings, logs or command-line arguments.

Remove accounts before uninstalling to delete their credentials. MSIX removal is
not OAuth revocation and is not relied on to clean generic Credential Manager
entries. The host's OAuth grants page provides revocation separately.

`GHCPSpendTray.PlatformTests` links platform sources, uses fake Windows startup
state, and confines filesystem/IPC checks to unique directories/scopes.
`tools\smoke-test.ps1` uses synthetic accounts and isolated portable data.
`tools\smoke-test-package.ps1` explicitly registers/removes only the development
package, tests package-local data, and reads its initially disabled startup task.
It requires pre-enabled Developer Mode; it never changes Windows trust/settings.
