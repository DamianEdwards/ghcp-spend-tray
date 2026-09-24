# Windows platform services

These services use the Windows SDK and .NET's built-in Windows APIs, with
source-generated P/Invoke for Credential Manager and Shell services. The visible
UI is Microsoft UI Reactor/WinUI 3; these platform helpers remain independent.

## Native UI integration

Call `Bootstrap.Start(args)` on the native message-loop thread. A null result
means an existing instance accepted activation; the new process should exit.
Otherwise retain the returned
`BootstrapRuntime` until the message loop ends and dispose it **on that same
thread**, since the runtime owns a named mutex.

The Reactor startup callback creates a hidden top-level Shell callback HWND,
registers the tray icon, and starts the application controller. Win32 is not used
to build visible windows or controls.

- `DataDirectory` supplies the configuration/history/log directory.
- `IsPortable` means the UI must disable startup registration controls.
- `RegisterActivationCallback(Action)` registers a **worker-thread** callback.
  Use `ReactorApp.UIDispatcher` rather than manipulating controls directly.
- Subscribe to `Diagnostic` to surface activation callback failures.
- Call `SignalReady()` **only after** native window creation and successful tray
  icon registration. Do not call it from a `finally` block or on initialization
  failure. It releases queued singleton activation requests.

Activation requests arriving during initialization are coalesced until both a
callback and explicit readiness are present. The activation protocol contains a
single fixed command; it cannot execute commands, open arbitrary paths, or ask a
running app to exit. Both IPC pipes and named mutexes are current-user restricted.
Production is a per-user singleton, including across Windows sessions; portable
instances have independent scopes derived from their absolute data directories.

## Installation and startup

A ZIP build runs from its extracted folder, including all Windows App SDK
dependencies. `Bootstrap.Start` does not copy files, stage updates, or touch
startup. It secures `%USERPROFILE%\.ghspend` for per-user data (or the explicit
portable directory) and acquires the runtime singleton.

Startup is opt-in through Settings. Only the `GHSpend` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` is changed. Its value is
the current executable's quoted absolute path followed by `--startup`. A moved
application folder requires saving the enabled startup setting again.
`StartupApproved` policy is never changed.

`StartupRegistration.Enabled` reports whether the current executable's Run value is present;
it does **not** claim Windows' Startup settings permit that entry to run.
`IsRegistered` also recognizes a registration pointing to an older application
location, allowing the Settings toggle to remove or update it. `SetEnabled(bool)`
implements an explicit user preference change.

`InstallationTransaction`, `WindowsInstallFiles`, `ReadinessHandoff`, and installer
warning plumbing are retained as legacy helpers with regression tests. They are
not invoked by the ZIP bootstrap; `--handoff` is explicitly rejected. The old
single-file installation contract does not apply to a WinUI folder distribution.

## Portable development and tests

```powershell
.\ghspend.exe --portable --data-dir D:\scratch\ghspend-isolated
```

Portable mode requires both options and an absolute local path. It never performs
installation or startup registration. The real installation tree, profile root,
and volume root cannot be used as portable data directories. Use a dedicated
directory: it receives a private ACL and the app may write its data there.
`--startup` and the private `--handoff` argument cannot be combined with portable
mode. All ZIP launches reject the obsolete handoff argument.

The independent test harness links these platform sources without referencing
the main UI or Core:

```powershell
dotnet run --project tests\GHSpend.PlatformTests\GHSpend.PlatformTests.csproj
dotnet publish tests\GHSpend.PlatformTests\GHSpend.PlatformTests.csproj -c Release -r win-x64 -p:PublishAot=true
.\tests\GHSpend.PlatformTests\bin\Release\net10.0-windows\win-x64\publish\GHSpend.PlatformTests.exe
```

Tests use in-memory startup/installer abstractions and unique project-local
directories/IPC names. They exercise native copy, Mark of the Web, ACLs, junction
rejection, activation, process-verified readiness, portable startup, and injected
rollback failures. **They never run the installer, write the real Run key, or
read/write real credentials.** The harness cleans its isolated data directory.
Full ZIP extraction/SmartScreen behavior and login startup remain clean-machine/manual
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
the extracted application folder and app-owned `.ghspend` data. If the executable cannot run, delete **only**
the `GHSpend` Run value and the app's `GHSpend/` generic credentials in Windows
Credential Manager before removing its directory. Local credential deletion does
not revoke the OAuth grant; revoke that separately in the relevant GitHub
host's application settings if desired.
