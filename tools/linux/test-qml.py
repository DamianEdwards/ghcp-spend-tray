#!/usr/bin/env python3
"""Exercise packaged QML components and parser without connecting to a desktop."""

import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def tool(name):
    override = os.environ.get("QT_TOOLS_DIR")
    candidate = Path(override) / name if override else None
    if candidate and candidate.is_file():
        return str(candidate)
    found = shutil.which(name)
    if found:
        return found
    for directory in ("/usr/lib/qt6/bin", "/usr/lib64/qt6/bin"):
        candidate = Path(directory) / name
        if candidate.is_file():
            return str(candidate)
    raise RuntimeError(f"Missing Qt validation tool: {name}. Install Qt 6 declarative development tools.")


def main():
    spec = importlib.util.spec_from_file_location("package_native", ROOT / "tools/linux/package-native.py")
    package = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(package)
    with tempfile.TemporaryDirectory(prefix="ghcp-qml-") as directory:
        root = Path(directory)
        package.stage_qml(root / "hyprland", "Hyprland")
        package.stage_qml(root / "kde", "Kde")
        for source in (*sorted((root / "kde").rglob("*.qml")), *sorted((root / "hyprland").rglob("*.qml"))):
            subprocess.run([tool("qmlformat"), str(source)], check=True, stdout=subprocess.DEVNULL)
        shutil.copyfile(ROOT / "tools/linux/tst_usage.qml", root / "hyprland/tst_usage.qml")
        runtime = root / "runtime"
        runtime.mkdir(mode=0o700)
        environment = dict(os.environ, QT_QPA_PLATFORM="offscreen", QT_QUICK_BACKEND="software",
                           XDG_RUNTIME_DIR=str(runtime), HOME=str(root),
                           XDG_CONFIG_HOME=str(root / "config"), XDG_CACHE_HOME=str(root / "cache"))
        environment.pop("DBUS_SESSION_BUS_ADDRESS", None)
        subprocess.run([tool("qmltestrunner"), "-input", str(root / "hyprland/tst_usage.qml")],
                       env=environment, check=True, timeout=60)


if __name__ == "__main__":
    main()
