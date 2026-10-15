#!/usr/bin/env python3
"""Render the real GNOME preferences with synthetic IPC on an isolated GTK display."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    output = ROOT / "artifacts/linux/preferences"
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ghcp-prefs-") as directory:
        root = Path(directory)
        runtime = root / "runtime"
        runtime.mkdir(mode=0o700)
        environment = dict(os.environ, HOME=str(root), XDG_RUNTIME_DIR=str(runtime),
                           XDG_CONFIG_HOME=str(root / "config"), XDG_CACHE_HOME=str(root / "cache"),
                           XDG_DATA_HOME=str(root / "data"), XDG_STATE_HOME=str(root / "state"),
                           GDK_BACKEND="x11", GSK_RENDERER="cairo",
                           GSETTINGS_BACKEND="memory", GTK_A11Y="none", GIO_USE_VFS="local")
        for name in ("DBUS_SESSION_BUS_ADDRESS", "DISPLAY", "WAYLAND_DISPLAY", "GTK_THEME", "GDK_SCALE", "GDK_DPI_SCALE"):
            environment.pop(name, None)
        source = (ROOT / "src/GHCPSpendTray.Linux.Gnome/prefs.js").read_text()
        host_import = "import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';"
        client_import = "import {DemoClient} from './client.js';"
        if source.count(host_import) != 1 or source.count(client_import) != 1:
            raise RuntimeError("Update the preferences test's explicit host/client seams.")
        (root / "prefs.js").write_text(source.replace(host_import,
            "import {ExtensionPreferences, DemoClient} from './prefs-fixture.js';").replace(client_import, ""))
        shutil.copyfile(ROOT / "src/GHCPSpendTray.Linux.Gnome/snapshot.js", root / "snapshot.js")
        for name in ("test-prefs.js", "prefs-fixture.js"):
            shutil.copyfile(ROOT / "tools/linux" / name, root / name)
        for width, font in ((620, 11), (360, 11), (620, 16), (360, 16)):
            subprocess.run(["xvfb-run", "--auto-servernum", "--server-args=-screen 0 1280x1200x24 -nolisten tcp",
                            "timeout", "--kill-after=5s", "30s",
                            "dbus-run-session", "--config-file=" + str(ROOT / "tools/linux/session-bus.conf"),
                            "--", "gjs", "-m", str(root / "test-prefs.js"),
                            str(width), str(font), str(output)],
                           env=environment, check=True, timeout=45)


if __name__ == "__main__":
    main()
