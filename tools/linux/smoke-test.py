#!/usr/bin/env python3
"""Install only into temporary directories; activate the helper on a private service-enabled bus."""

import os
import json
import hashlib
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
UUID = "ghcp-spend-tray-demo@ghcpspendtray"


def main():
    original_image = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="ghcp native smoke ") as directory:
        root = Path(directory)
        data = root / "data"
        runtime = root / "runtime"
        runtime.mkdir(mode=0o700)
        downloaded = root / "Downloads/GHCPSpendTray.AppImage"
        downloaded.parent.mkdir()
        shutil.copyfile(original_image, downloaded)
        downloaded.chmod(0o755)
        installer = [str(downloaded), "--appimage-extract-and-run"]
        environment = dict(os.environ, HOME=str(root), XDG_DATA_HOME=str(data),
                           XDG_CONFIG_HOME=str(root / "config"), XDG_CACHE_HOME=str(root / "cache"),
                           XDG_STATE_HOME=str(root / "state"),
                           DBUS_SYSTEM_BUS_ADDRESS="unix:path=" + str(root / "no-system-bus"),
                           XDG_RUNTIME_DIR=str(runtime), GSETTINGS_BACKEND="memory")
        environment.pop("DBUS_SESSION_BUS_ADDRESS", None)
        subprocess.run(["dbus-run-session", "--config-file=" + str(ROOT / "tools/linux/session-bus.conf"),
                        "--", *installer, "--smoke-ui"],
                       env=dict(environment, QT_QPA_PLATFORM="offscreen"),
                       check=True, timeout=60)
        for _ in range(2):
            subprocess.run([*installer, "--install", "--files-only"], env=environment, check=True, timeout=60)
        for desktop in ("kde", "hyprland"):
            subprocess.run([*installer, "--install", "--desktop", desktop, "--files-only"], env=environment, check=True, timeout=60)
        installed = data / "ghcp-spend-tray-desktop/GHCPSpendTray.AppImage"
        with installed.open("rb") as stream:
            installed_digest = hashlib.file_digest(stream, "sha256").hexdigest()
        with downloaded.open("rb") as stream:
            if installed_digest != hashlib.file_digest(stream, "sha256").hexdigest():
                raise RuntimeError("Stable installed AppImage is not byte-identical to the download.")
        downloaded.unlink()
        icon = data / "ghcp-spend-tray-demo/ghcp-spend-tray.svg"
        if icon.read_bytes() != (ROOT / "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.svg").read_bytes():
            raise RuntimeError("Installed application icon does not match the shared branding asset.")
        for name in ("ghcp-spend-tray.desktop", "ghcp-spend-tray-hyprland.desktop"):
            if f"Icon={icon}\n" not in (data / "applications" / name).read_text():
                raise RuntimeError("Installed launcher is missing its persistent application icon.")
            subprocess.run(["gjs", "-c", """
                const Gio = imports.gi.Gio;
                const entry = Gio.DesktopAppInfo.new_from_filename(ARGV[0]);
                const icon = entry?.get_icon();
                if (!(icon instanceof Gio.FileIcon) || icon.get_file().get_path() !== ARGV[1] ||
                    !icon.get_file().query_exists(null))
                    throw new Error('Desktop launcher must resolve the installed application icon.');
                """, str(data / "applications" / name), str(icon)], env=environment, check=True, timeout=15)
        installer = [str(installed), "--appimage-extract-and-run"]
        subprocess.run([*installer, "--install", "--desktop", "hyprland", "--files-only"],
                       env=environment, check=True, timeout=60)
        if not (data / "plasma/plasmoids/io.github.ghcpspendtray.demo/contents/ui/components/UsageView.qml").is_file():
            raise RuntimeError("KDE package is missing its usage component.")
        if not (data / "ghcp-spend-tray-hyprland/components/UsageView.qml").is_file():
            raise RuntimeError("Hyprland package is missing its usage component.")
        extension = data / "gnome-shell/extensions" / UUID
        subprocess.run(["gsettings", "--schemadir", str(extension / "schemas"), "get",
                        "org.gnome.shell.extensions.ghcp-spend-tray-demo", "indicator-style"],
                       env=environment, check=True)
        config = root / "bus.conf"
        config.write_text(
            '<busconfig><type>session</type><listen>unix:tmpdir=/tmp</listen><auth>EXTERNAL</auth>'
            f'<servicedir>{escape(str(data / "dbus-1/services"))}</servicedir>'
            '<policy context="default"><allow send_destination="*" eavesdrop="true"/>'
            '<allow eavesdrop="true"/><allow own="*"/></policy></busconfig>')
        service = data / "dbus-1/services/io.github.ghcpspendtray.LinuxDemo.service"
        real_service = service.read_text()
        # Demo mode is opt-in only in this private, temporary installation.
        service.write_text(real_service.rstrip() + " --demo\n")
        subprocess.run(["dbus-run-session", f"--config-file={config}", "--", "gjs", "-m",
                        str(ROOT / "tools/linux/test-bus.js"), str(extension)],
                       env=environment, check=True, timeout=40)
        command = subprocess.run(["dbus-run-session", f"--config-file={config}", "--",
                                  str(data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux"),
                                  "--command", "GetSnapshot"], env=environment, check=True,
                                 capture_output=True, text=True, timeout=40)
        value = json.loads(command.stdout)
        if value["version"] != 4 or value["demo"] is not True or value["consumption"] != "$42.75":
            raise RuntimeError("Installed Native AOT command adapter returned an unexpected snapshot.")
        command = subprocess.run(["dbus-run-session", f"--config-file={config}", "--",
                                  str(data / "ghcp-spend-tray-desktop/ghcp-spend-tray-setup"),
                                  "--panel", "status"], env=environment, check=True,
                                 capture_output=True, text=True, timeout=40)
        if json.loads(command.stdout)["class"] != "demo":
            raise RuntimeError("The frozen Waybar adapter did not return synthetic usage.")
        service.write_text(real_service)
        command = subprocess.run(["dbus-run-session", f"--config-file={config}", "--",
                                  str(data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux"),
                                  "--command", "GetSnapshot"], env=environment, check=True,
                                 capture_output=True, text=True, timeout=40)
        value = json.loads(command.stdout)
        if value["demo"] or value["accounts"] or value["consumption"] != "Unavailable":
            raise RuntimeError("Default activation must use the real, empty shared backend.")
        module = json.loads((data / "ghcp-spend-tray-hyprland/waybar-module.json").read_text())
        if module != {"modules-right": ["tray"]}:
            raise RuntimeError("Hyprland must use the native tray, not a custom status module.")
        subprocess.run(["python3", str(ROOT / "tools/linux/test-tray.py"),
                        str(data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux")], check=True, timeout=60)
        subprocess.run([*installer, "--remove", "--files-only"], env=environment, check=True, timeout=60)
        if (extension.exists() or (data / "ghcp-spend-tray-demo").exists() or
                (data / "ghcp-spend-tray-hyprland").exists() or
                (data / "plasma/plasmoids/io.github.ghcpspendtray.demo").exists() or
                (data / "ghcp-spend-tray-desktop").exists() or
                (data / "applications/ghcp-spend-tray.desktop").exists() or
                any((data / "dbus-1/services").iterdir())):
            raise RuntimeError("Uninstall left owned components behind.")
        print("PASS: AppImage GUI, all frontends, update, deleted download, stable-copy reinstall, "
              "Native AOT activation, frozen Waybar adapter, GJS signals and self-removal")


if __name__ == "__main__":
    main()
