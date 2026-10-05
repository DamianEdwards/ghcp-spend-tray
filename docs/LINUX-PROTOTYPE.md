# Native Linux prototype

This is a **development build**, not a production Linux release. Local builds
and the `linux-devel` CI artifact are unsigned. After protected credentials are
configured, successful applicable main pushes produce a separate `linux-signed`
artifact with embedded OpenPGP signing and GitHub provenance.
See [signing setup and verification](RELEASING.md#linux-main-branch-signing).
The helper uses the real shared application controller for GitHub device-flow
sign-in, account persistence, polling, consumption, and notifications.
Synthetic accounts are available only with explicit `--demo` or `--demo-empty`
helper arguments; they never read or write real account data.

## Download, open, and set up

Download **GHCPSpendTray-linux-devel-x86_64.AppImage**, allow execution in your
file manager's properties if necessary, and open it. ARM64 builds use
`aarch64` in the filename; they are not yet validated.

The AppImage opens **graphical setup**, not a standalone consumption window:

1. Confirm the detected desktop, or select GNOME, KDE Plasma, or Hyprland.
2. Read the integration details and approve the per-user installation.
3. Choose **Install / Update** and follow the desktop's activation instructions.
4. Use the panel indicator for everyday consumption, Details, and Refresh.

No terminal, administrator password, separately installed Python, Qt setup
runtime, or .NET runtime is required for the normal setup flow. The AppImage
bundles its graphical setup/runtime, shared Native AOT helper, and all three
frontend payloads. It installs the selected frontend, not another desktop.
Missing **desktop** components are reported before installation rather than
silently installed with elevated privileges.

| Desktop | Native interaction and activation |
| --- | --- |
| GNOME Shell 50 | Icon-anchored Shell popup, with GTK/libadwaita preferences. Setup attempts to enable the extension. A logout/login may be needed for discovery and is required to reload updated JavaScript. |
| KDE Plasma 6.3+ | Native compact indicator/full popup and persistent appearance settings. Add **GHCPSpendTray** through the panel's **Add Widgets** action. Log out/in after updates. |
| Hyprland + Waybar + Quickshell 0.2.1+ | Native StatusNotifierItem icons in Waybar's tray open the Quickshell popup and select the clicked account. Reload Waybar yourself after setup; monitoring starts during installation, and login startup is opt-in. |

The setup screen has **Open native controls** and **Remove** actions and remains
available as **GHCPSpendTray Setup** in the application launcher. The Plasma
action explains the native widget controls; it does not script panel changes.
Enabling the GNOME extension or adding the Plasma widget remains a desktop
activation step, not something AppImage can universally bypass.

Setup follows the desktop's light/dark preference, presents desktop choices as
selectable cards, and keeps paths/Waybar configuration under **Installation
options**. Progress and actionable results are inline; diagnostic output is
available under **Show details**, not an empty console on the welcome screen.
Changing desktops clears installation consent.

This is deliberately an **AppImage with installation**, not a fully portable
app. Extensions/widgets must live where the desktop can discover them.
It is distributed by the project, not through extensions.gnome.org: that
store's [review rules](https://gjs.guide/extensions/review-guidelines/review-guidelines.html#scripts-and-binaries)
prohibit bundling the binary helper in an extension submission.

## Stable installation, updates, and removal

Setup copies the original AppImage to a stable location under `$XDG_DATA_HOME`
(default `~/.local/share`). Moving or deleting the original download does not
break the installed application.

| Installed location under the data directory | Purpose |
| --- | --- |
| `ghcp-spend-tray-desktop/` | Stable AppImage, bundled setup/Python/Qt runtime, and license notices |
| `ghcp-spend-tray-demo/` | Native AOT helper, shared application icon and license |
| `applications/ghcp-spend-tray.desktop` | Graphical setup/update/removal launcher |
| `gnome-shell/extensions/ghcp-spend-tray-demo@ghcpspendtray/` | Selected GNOME UI, preferences, settings schema and icon |
| `plasma/plasmoids/io.github.ghcpspendtray.demo/` | Selected Plasma widget and native configuration |
| `ghcp-spend-tray-hyprland/` | Selected Quickshell popup and reversible Waybar integration metadata |
| `applications/ghcp-spend-tray-hyprland.desktop` | Alternative launcher for the Hyprland popup |
| `dbus-1/services/io.github.ghcpspendtray.LinuxDemo.service` | Automatic session-bus activation of the helper |

The helper and Quickshell launcher run from extracted installed files, not
from a repeatedly mounted or extracted image. The setup launcher uses AppImage
extract-and-run, so subsequent setup/removal does not require a FUSE mount.
The AppImage and installed desktop launchers reuse the existing shared
`ghcpspendtray-logo.svg` branding asset. Launcher icons point to the owned,
installed copy, not the original download or a generic system-monitor icon.

To update, open a newer downloaded AppImage and choose **Install / Update**
for the desktop integration. Updates replace the stable image, bundled runtime,
helper and selected frontend together. There is no network auto-updater yet.
If integrations for multiple desktops are installed, update each selected
frontend. File replacements are ownership-checked and rolled back on failure.
Concurrent setup operations are rejected.

After a GNOME update, **log out of the desktop and back in**. Disabling and
re-enabling an extension does not reload its JavaScript inside GNOME Shell.
Preferences run separately, so sign-in can succeed while the panel still runs
old code. An error mentioning `LinuxDemo2`, `LinuxDemo3`, or `UpgradeRequired`
means that older panel must be reloaded; do not remove or reconnect the account.
The helper rejects old account requests with reload guidance, while retaining
their shutdown command for safe upgrades/removal. Saved accounts are preserved.

To remove, open **GHCPSpendTray Setup** and choose **Remove**. It confirms removal,
stops owned helper/popup processes, reverses owned Waybar edits and an unchanged
app-owned login startup entry, and removes
all this package's installed surfaces/runtime/AppImage. The original download,
GNOME/Plasma preferences, account state, keyring credentials, and unrelated files
are retained. Remove accounts in native controls before uninstalling to delete
their credentials. No general tray
extension or system-wide package is modified.

## Waybar integration

Setup reuses an existing `tray` (including one inherited from an included file),
or adds `tray` to the selected bar's `modules-right` after installation consent.
It does not replace the user's tray configuration. An owned legacy
`custom/ghcp-spend-tray` integration is migrated reversibly. JSONC comments,
included files and CSS are preserved. It never starts or restarts Waybar.
Actual pie/percentage and independent per-account icons come from the helper,
not a custom text module. Watcher restarts trigger re-registration.

The default search checks `$XDG_CONFIG_HOME/waybar/config` and `config.jsonc`
(default config home is `~/.config`). Use the graphical file chooser for a
different file and enter a zero-based bar index when the configuration is an
array containing multiple bars. Multiple bars require explicit selection.
Pass the actual target file for a symlinked configuration.

Includes may be absolute, `~/`, or environment-expanded paths, including globs.
Relative includes are refused before editing: their resolution depends on
Waybar's launch directory/search path. Existing unowned modules with the same
name are refused.

Removal restores the config byte-for-byte when otherwise unchanged. If the user
made later unrelated edits, it preserves those and removes only the owned tray
placement. Existing tray placements are never removed. Ambiguous changes and
modified legacy module definitions require reconciliation rather than being
overwritten. Reversal metadata is readable only by the installing user.

The popup opens top-right on the focused Hyprland monitor, with its size bounded
by the screen. Escape, Close and a Hyprland focus grab support dismissal. Exact
anchoring to an arbitrarily positioned Waybar item is not implemented.
No Hyprland configuration is installed. The popup launcher can restart stopped
monitoring; **General > Start monitoring at login** registers the helper in
`$XDG_CONFIG_HOME/autostart/ghcp-spend-tray.desktop`. Modified or unowned startup
entries are never overwritten/deleted. Quickshell and Waybar remain
host desktop dependencies; Quickshell must match the host Qt installation.

## Presentation and contract

All frontends use the shared controller's consumption totals and account cards:
large monthly total, allocation progress, freshness, Details, and Refresh.
KDE and Hyprland share a Qt Quick usage view. Errors clear usage to
**Unavailable**, not zero. Over-allocation is retained in labels and only the
visual progress bar is clamped.

GNOME preferences and the Plasma/Hyprland popup's **Settings** support sign-in,
cancel, reconnect, account refresh, display names, percentage alert overrides,
USD alert increments, and confirmed removal. Blank overrides inherit global
settings; zero disables USD increment alerts. Reconnect retains the original
host, OAuth client, identity, and preferences. A wrong identity is rejected.
All three desktops expose the shared global refresh interval, notifications,
percentage/USD thresholds, test notification, and login startup settings.
They support pie/percentage icons, weighted roll-up/per-account mode, account
inclusion, and a draft preview computed by the shared backend without saving.
The same renderer supplies PNG images to GNOME/Qt Quick and ARGB pixels to the
native tray. Legacy desktop-local style preferences no longer override the
shared settings.

Account preferences include opt-in period estimates with early/unavailable
caveats. Details include observed credits, consumption, allocation, percentage,
source/reset/next-refresh timestamps, period identity, and estimate inputs.
Last-known and partial totals are labeled; cached avatars are local files.
Controls also provide copy-code, host-specific OAuth grant management, data and
startup folders, project/privacy/support links, and Quit. On systemd desktops,
login1 resume signals invoke the shared resume refresh; ordinary polling remains
active if that platform service is unavailable.

### Credentials and sign-in

Install **libsecret-1** and run a Secret Service provider on the desktop session
bus. GNOME normally uses GNOME Keyring. KDE can use KWallet with its Secret
Service interface enabled; legacy KWallet-only D-Bus interfaces are not supported.
Hyprland must start a provider such as GNOME Keyring or a Secret-Service-enabled
KWallet, and unlock it (for example with the desktop's PAM integration).
The application never installs, starts, restarts, or unlocks a provider using a
stored password; standard keyring prompts remain provider-controlled.

Choose the HTTPS GitHub host and, when required, its enterprise OAuth client ID.
Select **Sign in**, copy the displayed device code, open the verification page,
and approve on that host. The shared backend verifies identity and consumption
access before saving an account. Device codes expire; Cancel stops polling.
Closing preferences does not cancel sign-in: reopen them to continue or cancel.

Tokens are stored only through libsecret in the provider's default collection,
under schema `io.github.ghcpspendtray.Credentials.v1`, scoped by host, verified
user ID, and OAuth client ID. There is **no plaintext fallback**. Missing
providers, locked keyrings, cancellation, corrupt credentials, and failed
deletion are surfaced rather than treated as missing credentials. Failed
deletion keeps the account configured so it can be retried. Keyring encryption
and unlock policy are controlled by the provider; configure a protected keyring.

Non-secret account settings, history, and avatar cache live under
`$XDG_STATE_HOME/ghcp-spend-tray` (default `~/.local/state/ghcp-spend-tray`), in an
owner-only directory separate from the installed binaries. Updates and
uninstallation preserve it. Desktop notifications use
`org.freedesktop.Notifications`; failed submission is not acknowledged.

| Contract | Value |
| --- | --- |
| Bus name | `io.github.ghcpspendtray.LinuxDemo` |
| Object path | `/io/github/ghcpspendtray/LinuxDemo` |
| Interface | `io.github.ghcpspendtray.LinuxDemo4` |
| Snapshot | Source-generated JSON, `version: 4`, `demo: false` by default |
| Methods | `GetSnapshot`, `GetSignIn`, `Execute`, `Preview`, `Refresh`, `AddDemoAccount` (demo only), `SetStyle`, `Quit` |
| Signal | `Changed` carrying the latest snapshot |

GNOME uses push updates; Plasma polls every ten seconds; Quickshell polls while
open. Native tray registration/settings reconciliation runs once per second
and emits icon/tooltip change signals. This is presentation refresh, not GitHub
polling. KDE uses native D-Bus calls with timeout/error handling. Quickshell uses
the whitelisted helper `--command` adapter.
`Execute` accepts a bounded JSON account request (`signin`, `cancel`, `save`,
`refresh`, `remove`, `settings`, `testNotification`, or `resume`). Sign-in starts asynchronously; `GetSignIn` returns its
phase and temporary user code/verification URL only in direct replies, never
in broadcast snapshots. Access/refresh tokens never cross this frontend IPC
boundary or appear in command arguments. The same-user session bus carries
account presentation data; it is not a boundary against other apps running as
that user. Legacy installation IDs/bus paths intentionally remain stable for
updates and removal; v2/v3 support only `Quit` for installer compatibility.

## Building and verification

Use Linux and the SDK pinned in `global.json`. Build dependencies include a
C/C++ toolchain, zlib development files, Python with venv/pip, and
`glib-compile-schemas`. These are developer requirements, not end-user setup
requirements.

```bash
python3 -m venv artifacts/linux/build-env
artifacts/linux/build-env/bin/python -m pip install -r tools/linux/requirements-build.txt
artifacts/linux/build-env/bin/python tools/linux/package-native.py
PATH="$PWD/artifacts/linux/build-env/bin:$PATH" bash tools/linux/verify.sh
```

Packaging freezes the Qt Widgets setup and Python adapter, builds the Native
AOT helper, stages the desktop integrations, and emits an AppImage and SHA-256
sidecar. The Qt setup is not an Avalonia frontend. AppImage build-tool/runtime
downloads have explicit SHA-256 pins; mismatches fail rather than running
unverified tooling. Framework license notices are included with the runtime.

Verification additionally requires GJS, D-Bus utilities, Node and Qt 6
declarative tools/modules: `qmlformat`, `qmltestrunner`, QtTest, Quick Controls,
Layouts, Templates, Window and WorkerScript. Set `QT_TOOLS_DIR` for nonstandard
Qt tool locations. libsecret-1 and `gnome-keyring-daemon` are required for
isolated credential-store tests. GnuPG is required for ephemeral signing tests;
these do not use or create a publisher key.
The native GNOME preferences layout checks also require GTK 4/libadwaita
introspection data, Adwaita icons, Xvfb (`xvfb-run`) and `xauth`. Run
`python3 tools/linux/test-prefs.py` for the focused, synthetic sign-in and
responsive-layout checks; screenshots go to `artifacts/linux/preferences/`.

Tests cover managed/shared and Native AOT logic, real-controller device flow
with synthetic HTTP and credentials, isolated libsecret CRUD and locked/missing
keyrings, GJS/D-Bus interoperability,
Qt Quick parser/card interactions, Waybar edits, transactional install/rollback,
and the actual AppImage's graphical chooser and consent gate. Installed-image
smoke tests delete the original download, reinstall from the stable copy,
exercise the bundled popup adapter without host Python, and remove the
installed image while running from it.
Private fake tray hosts check pixmap dimensions, exact PNG/ARGB pixel agreement,
weighted and over-allocation tooltips, selected-account activation, removal,
all-excluded fallback, and watcher restart. Startup and resume tests use only
temporary files and synthetic private-bus services.

All installation tests use temporary HOME/XDG paths and private buses. They do
not enable extensions, alter the user's bar, or restart desktop processes.
Internal `--install`, `--remove`, `--files-only` and `--smoke-ui` options exist
for this isolated tooling; users normally use the graphical interface.

## Limitations and remaining acceptance

A checksum alone does not authenticate a publisher. Download the `linux-signed`
artifact only after its protected signing job succeeds, and verify its signature
and provenance before running it. AppImage does not enforce signatures at launch.
The local artifact remains unsigned, and synthetic signing tests do not establish
publisher identity. The production GitHub signing job still requires owner
credentials and a successful live run. Versioned Linux releases and a trusted
update channel remain separate work; Linux is excluded from the manual production
Release workflow and Windows solution.

AppImage avoids distro-specific installer formats, not every host requirement:
normal first launch needs executable permission and a working AppImage/FUSE
environment. Extract-and-run is available where mounting is unavailable, but
that fallback may require an AppImage-aware launcher or a command. It is not a
universal guarantee of terminal-free first launch on every distribution.
No glibc is bundled. CI builds on Ubuntu 24.04; Fedora-local builds can require
a newer glibc. ARM64 and broad distro compatibility remain unverified.

The graphical setup and shared Qt Quick controls have isolated runtime coverage.
The GNOME Shell popup/preferences, Plasma widget, and Hyprland compositor
interactions still require live native acceptance, including enable/reload,
focus, themes, scaling and multiple monitors. Syntax checks do not establish
that acceptance. The earlier hybrid extension's manual acceptance does not
validate this replacement. Live GitHub authorization, real KWallet behavior,
and desktop-specific keyring prompts still require manual acceptance. Automated
tests use synthetic accounts only and do not access the user's keyring.
