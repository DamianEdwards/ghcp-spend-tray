#!/usr/bin/env python3
"""Transactional per-user installation shared by graphical setup and isolated tests."""

import json
from pathlib import Path
import os
import shlex
import shutil
import subprocess
import sys
import tempfile

import waybar

UUID = "ghcp-spend-tray-demo@ghcpspendtray"
BUS = "io.github.ghcpspendtray.LinuxDemo"
PACKAGE = "ghcp-spend-tray-native-demo-v1"
MARKER = ".ghcp-package"


def host_environment():
    environment = dict(os.environ)
    if getattr(sys, "frozen", False):
        original = environment.pop("LD_LIBRARY_PATH_ORIG", None)
        if original is not None:
            environment["LD_LIBRARY_PATH"] = original
        else:
            environment.pop("LD_LIBRARY_PATH", None)
        for key in ("QT_PLUGIN_PATH", "QT_QPA_PLATFORM_PLUGIN_PATH", "QML2_IMPORT_PATH"):
            environment.pop(key, None)
    return environment


def owned(path, directory):
    if path.is_symlink():
        raise RuntimeError(f"Refusing to replace a symlink: {path}")
    if path.exists():
        marker = path / MARKER if directory else path
        if not marker.is_file() or not marker.read_text().startswith(PACKAGE if directory else f"# {PACKAGE}\n"):
            raise RuntimeError(f"Refusing to modify an installation not owned by this installer: {path}")


def desktop_command(*arguments):
    result = subprocess.run(arguments, capture_output=True, text=True, env=host_environment(), timeout=15)
    if result.returncode:
        print(f"Desktop integration: {result.stderr.strip() or result.stdout.strip()}", file=sys.stderr)
    return result.returncode == 0


def stop_helper():
    if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
        print("No desktop bus. The helper will stop when the old desktop session ends.")
        return
    result = subprocess.run(["gdbus", "call", "--session", "--dest", "org.freedesktop.DBus",
                             "--object-path", "/org/freedesktop/DBus", "--method",
                             "org.freedesktop.DBus.NameHasOwner", BUS],
                            check=True, capture_output=True, text=True, timeout=10, env=host_environment())
    if "true" in result.stdout:
        subprocess.run(["gdbus", "call", "--session", "--dest", BUS,
                        "--object-path", "/io/github/ghcpspendtray/LinuxDemo",
                        "--method", BUS + "2.Quit"], check=True, capture_output=True, timeout=10, env=host_environment())


def desktop_quote(value):
    argument = str(value).replace("\\", "\\\\").replace('"', '\\"').replace("`", "\\`").replace("$", "\\$").replace("%", "%%")
    return '"' + argument.replace("\\", "\\\\") + '"'


def desktop_value(value):
    return str(value).replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")


def install(payload, data, files_only, desktop="gnome", waybar_config=None, waybar_bar=None,
            appdir=None, appimage=None):
    helper = data / "ghcp-spend-tray-demo"
    extension = {"gnome": data / "gnome-shell/extensions" / UUID,
                 "kde": data / "plasma/plasmoids/io.github.ghcpspendtray.demo",
                 "hyprland": data / "ghcp-spend-tray-hyprland"}[desktop]
    service = data / "dbus-1/services" / (BUS + ".service")
    targets = [(helper, True), (extension, True), (service, False)]
    launcher = data / "applications/ghcp-spend-tray-hyprland.desktop"
    if desktop == "hyprland":
        targets.append((launcher, False))
    runtime = data / "ghcp-spend-tray-desktop"
    setup_launcher = data / "applications/ghcp-spend-tray.desktop"
    if appdir is not None:
        if appimage is None or not appimage.is_file():
            raise RuntimeError("Open the downloaded AppImage to install or update a stable copy.")
        targets.extend([(runtime, True), (setup_launcher, False)])
    for target, directory in targets:
        owned(target, directory)
    if not (payload / "helper/ghcp-spend-tray.svg").is_file():
        raise RuntimeError("The package is missing its application icon.")
    icon = desktop_value(helper / "ghcp-spend-tray.svg")
    integration = None
    observed_config = None
    if desktop == "hyprland" and not files_only:
        config_home = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
        state_path = extension / "waybar-integration.json"
        previous = json.loads(state_path.read_text()) if state_path.exists() else None
        if previous and waybar_config is None:
            waybar_config = Path(previous["path"])
            waybar_bar = previous["bar"]
        if waybar_config is None:
            waybar_config = next((path for path in (config_home / "waybar/config", config_home / "waybar/config.jsonc")
                                  if path.is_file()), None)
        if waybar_config is None:
            raise RuntimeError("No existing Waybar config found. Pass --waybar-config PATH.")
        waybar_config = Path(waybar_config).absolute()
        observed_config = waybar_config.read_text()
        integration = waybar.prepare_tray(waybar_config, waybar_bar, previous)
        targets.append((waybar_config, False))
    if not files_only:
        panel = data / "ghcp-spend-tray-hyprland/panel.py"
        if panel.exists():
            subprocess.run(panel_command(panel.parent, "stop"), check=True, timeout=15, env=host_environment())
        if desktop == "gnome" and extension.exists():
            desktop_command("gnome-extensions", "disable", UUID)
        stop_helper()
    data.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".ghcp-install-", dir=data) as directory:
        stage = Path(directory)
        helper_stage = stage / "helper"
        shutil.copytree(payload / "helper", helper_stage)
        (helper_stage / "GHCPSpendTray.Linux").chmod(0o755)
        (helper_stage / MARKER).write_text(PACKAGE)
        extension_stage = stage / "extension"
        shutil.copytree(payload / {"gnome": "extension", "kde": "kde", "hyprland": "hyprland"}[desktop], extension_stage)
        (extension_stage / MARKER).write_text(PACKAGE)
        service_stage = stage / "service"
        service_stage.write_text(
            f"# {PACKAGE}\n[D-BUS Service]\nName={BUS}\n"
            f"Exec={shlex.quote(str(helper / 'GHCPSpendTray.Linux'))}" +
            (" --status-notifier" if desktop == "hyprland" else "") + "\n")
        sources = [helper_stage, extension_stage, service_stage]
        if desktop == "hyprland":
            (extension_stage / "waybar-module.json").write_text(json.dumps({
                "modules-right": ["tray"]
            }, indent=2))
            launcher_stage = stage / "launcher"
            command = ([str(runtime / "ghcp-spend-tray-setup"), "--panel", "toggle"] if appdir else
                       panel_command(extension, "toggle"))
            launcher_stage.write_text(
                f"# {PACKAGE}\n[Desktop Entry]\nType=Application\nName=GHCPSpendTray (Hyprland)\n"
                f"Exec={' '.join(desktop_quote(part) for part in command)}\nIcon={icon}\nTerminal=false\nCategories=Utility;\n")
            sources.append(launcher_stage)
        if appdir is not None:
            runtime_stage = stage / "runtime"
            shutil.copytree(appdir / "usr", runtime_stage)
            shutil.copyfile(appimage, runtime_stage / "GHCPSpendTray.AppImage")
            (runtime_stage / "GHCPSpendTray.AppImage").chmod(0o755)
            (runtime_stage / MARKER).write_text(PACKAGE)
            setup_stage = stage / "setup-launcher"
            setup_stage.write_text(
                f"# {PACKAGE}\n[Desktop Entry]\nType=Application\nName=GHCPSpendTray Setup\n"
                f"Exec={desktop_quote(runtime / 'GHCPSpendTray.AppImage')} --appimage-extract-and-run\n"
                f"Icon={icon}\nTerminal=false\nCategories=Utility;\n")
            sources.extend([runtime_stage, setup_stage])
        if desktop == "hyprland":
            if integration:
                (extension_stage / "waybar-integration.json").write_text(json.dumps(integration))
                config_stage = stage / "waybar-config"
                config_stage.write_text(integration["installed"])
                config_stage.chmod(waybar_config.stat().st_mode & 0o777)
                sources.append(config_stage)
            elif (extension / "waybar-integration.json").exists():
                shutil.copyfile(extension / "waybar-integration.json", extension_stage / "waybar-integration.json")
            if (extension_stage / "waybar-integration.json").exists():
                (extension_stage / "waybar-integration.json").chmod(0o600)
        applied = []
        try:
            for index, (source, (target, directory)) in enumerate(zip(
                    sources, targets)):
                target.parent.mkdir(parents=True, exist_ok=True)
                backup = stage / f"backup-{index}"
                if integration and target == waybar_config and target.read_text() != observed_config:
                    raise RuntimeError("Waybar config changed during installation; retry after saving your edits.")
                existed = target.exists()
                if existed:
                    target.rename(backup)
                applied.append((target, backup if existed else None, directory))
                source.rename(target)
        except (OSError, RuntimeError):
            for target, backup, directory in reversed(applied):
                if target.exists():
                    if directory:
                        shutil.rmtree(target)
                    else:
                        target.unlink()
                if backup is not None:
                    backup.rename(target)
            raise
    print(f"Installed native {desktop} integration and shared helper under {data}")
    if files_only:
        print("Files only: no desktop settings or running processes were changed.")
        return
    if desktop == "gnome":
        desktop_command("gnome-extensions", "enable", UUID)
        print("Log out of your desktop and back in to load the installed GNOME extension.")
        print("Then enable 'GHCPSpendTray' in Extensions. The helper starts automatically.")
        print("Re-enabling the extension does not reload its JavaScript. Your saved accounts are unchanged.")
    elif desktop == "kde":
        print("Add 'GHCPSpendTray' from Plasma's Add Widgets menu. The helper starts automatically.")
        print("Log out/in after updates so Plasma loads the new QML.")
    else:
        subprocess.run(["gdbus", "call", "--session", "--dest", BUS,
                        "--object-path", "/io/github/ghcpspendtray/LinuxDemo",
                        "--method", BUS + "4.GetSnapshot"], check=True, capture_output=True,
                       timeout=30, env=host_environment())
        print("Integrated into your existing Waybar. Reload it using your usual configuration/session controls.")
        print("Native icons use Waybar's tray. Open the GHCPSpendTray (Hyprland) launcher if monitoring is stopped.")
        print("Enable 'Start monitoring at login' in General settings if desired. No Waybar is started or restarted.")


def panel_command(extension, action=None):
    runtime = extension.parent / "ghcp-spend-tray-desktop/ghcp-spend-tray-setup"
    command = ([str(runtime), "--panel"] if runtime.is_file()
               else ["python3", str(extension / "panel.py")])
    return [*command, action] if action else command


def uninstall(data, files_only):
    targets = [(data / "ghcp-spend-tray-demo", True),
               (data / "gnome-shell/extensions" / UUID, True),
               (data / "plasma/plasmoids/io.github.ghcpspendtray.demo", True),
               (data / "ghcp-spend-tray-hyprland", True),
               (data / "applications/ghcp-spend-tray-hyprland.desktop", False),
               (data / "ghcp-spend-tray-desktop", True),
               (data / "applications/ghcp-spend-tray.desktop", False),
               (data / "dbus-1/services" / (BUS + ".service"), False)]
    for target, directory in targets:
        owned(target, directory)
    config_home = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    startup = config_home / "autostart/ghcp-spend-tray.desktop"
    if startup.exists() or startup.is_symlink():
        executable = desktop_quote(data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux")
        contents = ["# GHCPSpendTray startup v1\n[Desktop Entry]\nType=Application\nName=GHCPSpendTray\n" +
                    f"Exec={executable}{suffix}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n"
                    for suffix in ("", " --status-notifier")]
        if startup.is_symlink() or startup.parent.is_symlink() or startup.read_text() not in contents:
            raise RuntimeError("The startup entry is unowned or modified; resolve it before uninstalling.")
        targets.append((startup, False))
    state_path = data / "ghcp-spend-tray-hyprland/waybar-integration.json"
    config = None
    if state_path.exists():
        if files_only:
            raise RuntimeError("Use desktop-aware uninstall to reverse the installed Waybar configuration edits.")
        state = json.loads(state_path.read_text())
        config = Path(state["path"])
        if config.is_symlink():
            raise RuntimeError("Waybar config became a symlink; refusing to overwrite it.")
        original = config.read_text()
        restored = waybar.remove(original, state)
    if not files_only:
        panel = data / "ghcp-spend-tray-hyprland/panel.py"
        if panel.exists():
            subprocess.run(panel_command(panel.parent, "stop"), check=True, timeout=15, env=host_environment())
        if (data / "gnome-shell/extensions" / UUID).exists():
            desktop_command("gnome-extensions", "disable", UUID)
        stop_helper()
    if config:
        with tempfile.NamedTemporaryFile(mode="w", dir=config.parent, delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(restored)
        try:
            temporary.chmod(config.stat().st_mode & 0o777)
            if config.read_text() != original:
                raise RuntimeError("Waybar config changed during removal; retry after saving your edits.")
            temporary.replace(config)
        finally:
            temporary.unlink(missing_ok=True)
    for target, directory in targets:
        if target.exists():
            if directory:
                shutil.rmtree(target)
            else:
                target.unlink()
    print("Removed this package's desktop surfaces, helper, and activation service. Desktop preferences were preserved.")
    if config:
        print("Restored your Waybar configuration; reload Waybar using your usual session controls.")
