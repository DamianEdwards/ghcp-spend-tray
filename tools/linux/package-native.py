#!/usr/bin/env python3
"""Build one AppImage with graphical setup, its runtime, and all native frontends."""

import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import sys
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts/linux"


def download_tool(name, architecture):
    specification = json.loads((ROOT / "tools/linux/appimage-tools.json").read_text())[architecture][name]
    cache = OUTPUT / "appimage-tools"
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / f"{name}-{architecture}-{specification['sha256'][:12]}"
    if not target.exists():
        with urllib.request.urlopen(specification["url"], timeout=60) as response, target.open("wb") as output:
            shutil.copyfileobj(response, output)
    with target.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != specification["sha256"]:
        target.unlink()
        raise RuntimeError(f"{name} checksum mismatch. Review upstream and update the explicit pin; refusing to execute.")
    target.chmod(0o755)
    return target


def stage_extension(destination):
    shutil.copytree(ROOT / "src/GHCPSpendTray.Linux.Gnome", destination,
                    ignore=shutil.ignore_patterns("gschemas.compiled", "__pycache__"))
    icons = destination / "icons"
    icons.mkdir()
    shutil.copyfile(ROOT / "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo-small.svg", icons / "logo.svg")
    subprocess.run(["glib-compile-schemas", "--strict", str(destination / "schemas")], check=True)


def stage_qml(destination, frontend):
    shutil.copytree(ROOT / f"src/GHCPSpendTray.Linux.{frontend}", destination,
                    ignore=shutil.ignore_patterns("__pycache__"))
    ui = destination / "contents/ui" if frontend == "Kde" else destination
    shutil.copytree(ROOT / "src/GHCPSpendTray.Linux.Qml", ui / "components")
    # QML's classic JS imports cannot consume ES module exports.
    parser = (ROOT / "src/GHCPSpendTray.Linux.Gnome/snapshot.js").read_text()
    (ui / "snapshot.js").write_text(".pragma library\n" + parser.replace("export function ", "function "))


def stage_notices(destination):
    destination.mkdir()
    (destination / "SOURCES.txt").write_text(
        "GHCPSpendTray graphical setup uses unmodified, dynamically linked Qt for Python.\n"
        "Qt/PySide/Shiboken 6.10.3: https://code.qt.io/cgit/pyside/pyside-setup.git/tag/?h=v6.10.3\n"
        "Qt sources: https://download.qt.io/archive/qt/6.10/6.10.3/submodules/\n"
        "Qt and its bindings are used under LGPLv3; corresponding license texts accompany this file.\n"
        "The extracted usr/_internal directory contains the replaceable shared libraries.\n"
        "PyInstaller source/bootloader exception: https://github.com/pyinstaller/pyinstaller/tree/v6.16.0\n"
        f"CPython {platform.python_version()}: https://www.python.org/downloads/source/\n"
        "AppImage runtime source: https://github.com/AppImage/type2-runtime\n"
        "Build-tool and runtime hashes are pinned in tools/linux/appimage-tools.json.\n")
    for name in ("LGPL-3.0-only", "GPL-3.0-only", "BSD-3-Clause", "Apache-2.0", "Qt-GPL-exception-1.0"):
        url = f"https://raw.githubusercontent.com/pyside/pyside-setup/v6.10.3/LICENSES/{name}.txt"
        with urllib.request.urlopen(url, timeout=30) as response:
            (destination / f"Qt-{name}.txt").write_bytes(response.read())
    with urllib.request.urlopen(
            f"https://raw.githubusercontent.com/python/cpython/v{platform.python_version()}/LICENSE",
            timeout=30) as response:
        (destination / "Python-LICENSE.txt").write_bytes(response.read())
    distribution = importlib.metadata.distribution("PyInstaller")
    for path in distribution.files:
        if str(path).endswith("licenses/COPYING.txt"):
            shutil.copyfile(distribution.locate_file(path), destination / "PyInstaller-COPYING.txt")


def main():
    architecture = platform.machine()
    rid = {"x86_64": "linux-x64", "aarch64": "linux-arm64"}.get(architecture)
    if platform.system() != "Linux" or rid is None:
        raise RuntimeError("Native packaging requires x86-64 or ARM64 Linux.")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    filename = f"GHCPSpendTray-linux-demo-{architecture}.AppImage"
    with tempfile.TemporaryDirectory(prefix="native-package-", dir=OUTPUT) as directory:
        work = Path(directory)
        appdir = work / "GHCPSpendTray.AppDir"
        payload = appdir / "payload"
        payload.mkdir(parents=True)
        helper = payload / "helper"
        subprocess.run(["dotnet", "publish", "src/GHCPSpendTray.Linux", "-c", "Release",
                        "-r", rid, "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true",
                        "-o", str(helper), "--nologo"], cwd=ROOT, check=True)
        for path in list(helper.glob("*.pdb")) + list(helper.glob("*.dbg")):
            path.unlink()
        shutil.copyfile(ROOT / "LICENSE", helper / "LICENSE")
        stage_extension(payload / "extension")
        stage_qml(payload / "kde", "Kde")
        stage_qml(payload / "hyprland", "Hyprland")
        (payload / "architecture").write_text(architecture)
        subprocess.run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
                        "--name", "ghcp-spend-tray-setup", "--distpath", str(work / "dist"),
                        "--workpath", str(work / "build"), "--specpath", str(work),
                        "--paths", str(ROOT / "src/GHCPSpendTray.Linux.Hyprland"),
                        "--hidden-import", "panel",
                        "--add-data", str(ROOT / "packaging/linux/check.svg") + ":ui",
                        str(ROOT / "packaging/linux/setup.py")], cwd=ROOT, check=True)
        shutil.move(work / "dist/ghcp-spend-tray-setup", appdir / "usr")
        stage_notices(appdir / "usr/licenses")
        shutil.copyfile(ROOT / "packaging/linux/AppRun", appdir / "AppRun")
        (appdir / "AppRun").chmod(0o755)
        shutil.copyfile(ROOT / "packaging/linux/ghcp-spend-tray.desktop", appdir / "ghcp-spend-tray.desktop")
        shutil.copyfile(ROOT / "src/GHCPSpendTray.App/Assets/ghcpspendtray-logo.svg", appdir / "ghcp-spend-tray.svg")
        shutil.copyfile(ROOT / "LICENSE", appdir / "LICENSE")
        tool = download_tool("appimagetool", architecture)
        runtime = download_tool("runtime", architecture)
        image = work / filename
        subprocess.run([str(tool), "--appimage-extract-and-run", "--runtime-file", str(runtime),
                        "--no-appstream", str(appdir), str(image)],
                       env=dict(os.environ, ARCH=architecture), check=True, timeout=300)
        image.chmod(0o755)
        image.replace(OUTPUT / filename)
    with (OUTPUT / filename).open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    (OUTPUT / (filename + ".sha256")).write_text(f"{digest}  {filename}\n")
    print(f"Created {OUTPUT / filename}")


if __name__ == "__main__":
    main()
