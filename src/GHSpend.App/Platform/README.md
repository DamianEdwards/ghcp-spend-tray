# Windows platform services

These services use the Windows SDK and .NET's built-in Windows APIs, with
source-generated P/Invoke for Credential Manager, file copying, and readiness
client verification. There are no UI framework or reflection dependencies.

## Native UI integration

Call `Bootstrap.Start(args)` on the native message-loop thread. A null result
means an existing instance accepted activation or an installed child successfully
initialized; the original process should exit. Otherwise retain the returned
`BootstrapRuntime` until the message loop ends and dispose it **on that same
thread**, since the runtime owns a named mutex.

Subscribe to `Bootstrap.Warning` (`Action<string>`) **before** calling `Start` to
display/log non-fatal installer cleanup warnings. These fixed messages contain
neither exception text nor credential data. Warnings also go to `Trace`; a failed
cleanup or warning subscriber does not roll back a successful handoff.

- `DataDirectory` supplies the configuration/history/log directory.
- `IsPortable` means the UI must disable startup registration controls.
- `RegisterActivationCallback(Action)` registers a **worker-thread** callback.
  Post a private window message rather than manipulating controls directly.
- Subscribe to `Diagnostic` to surface activation callback failures.
- Call `SignalReady()` **only after** native window creation and successful tray
  icon registration. Do not call it from a `finally` block or on initialization
  failure. The installer checks the connecting client's process ID.

Activation requests arriving during initialization are coalesced until both a
callback and explicit readiness are present. The activation protocol contains a
single fixed command; it cannot execute commands, open arbitrary paths, or ask a
running app to exit. Both IPC pipes and named mutexes are current-user restricted.
Production is a per-user singleton, including across Windows sessions; portable
instances have independent scopes derived from their absolute data directories.

## Installation and startup

A published Native AOT executable installs only into
`Environment.GetFolderPath(UserProfile)\.ghspend\ghspend.exe`. JIT/development
executables must use portable mode. A separate installation mutex cannot
deadlock the child's runtime mutex.

Installation rejects reparse-point ancestors, ambiguous/device/remote paths,
protects the directory and staged executable with current-user/SYSTEM ACLs, and
copies to a uniquely named staging file. `CopyFileW` preserves alternate data
streams, including `Zone.Identifier`; source and stage SHA-256 hashes must agree.
Promotion uses a same-directory move or `File.Replace` with a recovery backup.
Existing configuration/history are not replaced. Launch uses the Windows shell,
not an unblock operation or a SmartScreen bypass.

First installation writes and verifies only the `GHSpend` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Its value is a quoted
absolute executable path followed by `--startup`. Upgrades, ordinary installed
launches, and `--startup` launches do not re-enable startup. Windows'
`StartupApproved` policy is never changed.

`StartupRegistration.Enabled` reports whether the expected Run value is present;
it does **not** claim Windows' Startup settings permit that entry to run.
`SetEnabled(bool)` implements an explicit user preference change.

The installer waits up to 30 seconds for the installed window/tray's readiness.
Failure stops only its own spawned child, restores this attempt's executable
change and exact previous Run value/kind, and reports the error. An unstoppable
child prevents executable rollback; the diagnostic explains this, and any
recovery backup is retained. If rollback itself fails, backups are retained and
the error explicitly reports incomplete recovery. No arbitrary source/download
file is deleted.

An identical download activates the installed running process, or launches the
already-installed copy without modifying startup. A different binary is rejected
while GHSpend is running. When stopped, an older file version is rejected;
versions that cannot be compared require explicit manual replacement. This is
not an automatic updater.

## Portable development and tests

```powershell
.\ghspend.exe --portable --data-dir D:\scratch\ghspend-isolated
```

Portable mode requires both options and an absolute local path. It never performs
installation or startup registration. The real installation tree, profile root,
and volume root cannot be used as portable data directories. Use a dedicated
directory: it receives a private ACL and the app may write its data there.
`--startup` and the private `--handoff` argument cannot be combined with portable
mode. A handoff argument is accepted only from the installed executable.

The independent test harness links these platform sources without referencing
the main UI or Core:

```powershell
dotnet run --project tests\GHSpend.PlatformTests\GHSpend.PlatformTests.csproj
dotnet publish tests\GHSpend.PlatformTests\GHSpend.PlatformTests.csproj -c Release -r win-x64 -p:PublishAot=true
.\tests\GHSpend.PlatformTests\bin\Release\net11.0-windows\win-x64\publish\GHSpend.PlatformTests.exe
```

Tests use in-memory startup/installer abstractions and unique project-local
directories/IPC names. They exercise native copy, Mark of the Web, ACLs, junction
rejection, activation, process-verified readiness, portable startup, and injected
rollback failures. **They never run the installer, write the real Run key, or
read/write real credentials.** The harness cleans its isolated data directory.
Full shell/SmartScreen handoff and login startup remain clean-machine/manual
integration checks, not silently enabled test modes. The separate application's
`--smoke-test` mode round-trips and deletes its own uniquely named synthetic
credential, never a user's existing account credentials.

## Credentials and removal

`CredentialVault.Read(target)`, `Write(target, payload)`, and `Delete(target)`
operate on generic Credential Manager entries whose targets start with
`GHSpend/`. Payloads are opaque UTF-16 strings, limited to Windows' 2,560-byte
credential blob size. A missing entry reads as null; missing deletion is
idempotent. Windows failures have fixed, credential-free diagnostics. No token
payloads are logged or included in command-line arguments.

To remove the app, disable Start with Windows, remove accounts through GHSpend
to delete their local credential entries, exit from the tray menu, and remove
the app-owned `.ghspend` directory. If the executable cannot run, delete **only**
the `GHSpend` Run value and the app's `GHSpend/` generic credentials in Windows
Credential Manager before removing its directory. Local credential deletion does
not revoke the OAuth grant; revoke that separately in the relevant GitHub
host's application settings if desired.
