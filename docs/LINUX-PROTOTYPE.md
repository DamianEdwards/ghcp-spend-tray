# Native Linux prototype

This is a **synthetic-data-only experiment**, not a production Linux release.
Authentication, credential storage, network polling, and real GitHub consumption
remain disabled. Synthetic accounts reset with the helper.

## Download, open, and set up

Download **GHCPSpendTray-linux-demo-x86_64.AppImage**, allow execution in your
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
| KDE Plasma 6.3+ | Native compact indicator/full popup and persistent appearance settings. Add **GHCPSpendTray Demo** through the panel's **Add Widgets** action. Log out/in after updates. |
| Hyprland + Waybar + Quickshell 0.2.1+ | An indicator added to the existing Waybar opens a Quickshell layer-shell popup. Reload Waybar using your usual session controls after setup; the first click starts the popup automatically. |

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
| `ghcp-spend-tray-demo/` | Native AOT helper and license |
| `applications/ghcp-spend-tray.desktop` | Graphical setup/update/removal launcher |
| `gnome-shell/extensions/ghcp-spend-tray-demo@ghcpspendtray/` | Selected GNOME UI, preferences, settings schema and icon |
| `plasma/plasmoids/io.github.ghcpspendtray.demo/` | Selected Plasma widget and native configuration |
| `ghcp-spend-tray-hyprland/` | Selected Quickshell popup and reversible Waybar integration metadata |
| `applications/ghcp-spend-tray-hyprland.desktop` | Alternative launcher for the Hyprland popup |
| `dbus-1/services/io.github.ghcpspendtray.LinuxDemo.service` | Automatic session-bus activation of the helper |

The helper and Waybar status command run from extracted installed files, not
from a repeatedly mounted or extracted image. The setup launcher uses AppImage
extract-and-run, so subsequent setup/removal does not require a FUSE mount.

To update, open a newer downloaded AppImage and choose **Install / Update**
for the desktop integration. Updates replace the stable image, bundled runtime,
helper and selected frontend together. There is no network auto-updater yet.
If integrations for multiple desktops are installed, update each selected
frontend. File replacements are ownership-checked and rolled back on failure.
Concurrent setup operations are rejected.

To remove, open **GHCPSpendTray Setup** and choose **Remove**. It confirms removal,
stops owned helper/popup processes, reverses owned Waybar edits, and removes
all this package's installed surfaces/runtime/AppImage. The original download,
GNOME/Plasma preferences, and unrelated files are retained. No general tray
extension or system-wide package is modified.

## Waybar integration

Setup adds `custom/ghcp-spend-tray` to the selected existing Waybar's
`modules-right`. It preserves JSONC comments and leaves included files and CSS
untouched. It never starts a second Waybar or restarts the user's bar.

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
made later unrelated edits, it preserves those and removes the owned module.
An edited module definition requires manual reconciliation instead of being
overwritten. Reversal metadata is readable only by the installing user.

The popup opens top-right on the focused Hyprland monitor, with its size bounded
by the screen. Escape, Close and a Hyprland focus grab support dismissal. Exact
anchoring to an arbitrarily positioned Waybar item is not implemented. No
Hyprland config or login service is installed. Quickshell and Waybar remain
host desktop dependencies; Quickshell must match the host Qt installation.

## Presentation and contract

All frontends use the shared controller's consumption totals and account cards:
large monthly total, allocation progress, freshness, Details, and Refresh.
KDE and Hyprland share a Qt Quick usage view. Errors clear usage to
**Unavailable**, not zero. Over-allocation is retained in labels and only the
visual progress bar is clamped.

GNOME preferences support Pie/Percentage appearance and adding synthetic
accounts. Plasma supports a persistent amount/percentage indicator preference
in native configuration and adding synthetic accounts in the popup's Settings.
Hyprland Settings currently supports adding synthetic accounts.

| Contract | Value |
| --- | --- |
| Bus name | `io.github.ghcpspendtray.LinuxDemo` |
| Object path | `/io/github/ghcpspendtray/LinuxDemo` |
| Interface | `io.github.ghcpspendtray.LinuxDemo2` |
| Snapshot | Source-generated JSON, `version: 2`, `demo: true` |
| Methods | `GetSnapshot`, `Refresh`, `AddDemoAccount`, `SetStyle`, `Quit` |
| Signal | `Changed` carrying the latest snapshot |

GNOME uses push updates; Plasma polls every ten seconds; Quickshell polls while
open. Waybar requests indicator data every ten seconds. This is presentation
polling, not GitHub polling. KDE uses native D-Bus calls with timeout/error
handling. Quickshell and Waybar use the whitelisted helper `--command` adapter.
There are no credentials or authentication methods in this prototype contract.

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
Qt tool locations.

Tests cover managed/shared and Native AOT logic, GJS/D-Bus interoperability,
Qt Quick parser/card interactions, Waybar edits, transactional install/rollback,
and the actual AppImage's graphical chooser and consent gate. Installed-image
smoke tests delete the original download, reinstall from the stable copy,
exercise the bundled Waybar adapter without host Python, and remove the
installed image while running from it.

All installation tests use temporary HOME/XDG paths and private buses. They do
not enable extensions, alter the user's bar, or restart desktop processes.
Internal `--install`, `--remove`, `--files-only` and `--smoke-ui` options exist
for this isolated tooling; users normally use the graphical interface.

## Limitations and remaining acceptance

The current artifact is **unsigned development software**. A checksum alone
does not authenticate its publisher. Production signing/attestations and a
trusted update channel remain separate work; Linux is still excluded from the
production Release workflow and Windows solution.

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
validate this replacement. Real-account support remains unimplemented.
