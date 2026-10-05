import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from test_native_package import installer

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("graphical_setup", ROOT / "packaging/linux/setup.py")
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class AppImageSetupTests(unittest.TestCase):
    def fixture(self, root):
        appdir = root / "AppDir"
        (appdir / "usr").mkdir(parents=True)
        (appdir / "usr/ghcp-spend-tray-setup").write_text("synthetic runtime")
        payload = appdir / "payload"
        for name in ("helper", "extension", "kde", "hyprland"):
            (payload / name).mkdir(parents=True)
        (payload / "helper/GHCPSpendTray.Linux").write_text("synthetic helper")
        image = root / "download.AppImage"
        image.write_bytes(b"synthetic image")
        return appdir, image

    def test_detects_desktop_without_guessing_unknown_sessions(self):
        for value, expected in (("ubuntu:GNOME", "gnome"), ("KDE", "kde"),
                                ("Hyprland", "hyprland"), ("sway", None)):
            with self.subTest(value=value), patch.dict(os.environ, XDG_CURRENT_DESKTOP=value):
                self.assertEqual(setup.detected_desktop(), expected)

    def test_bundled_runtime_stable_copy_and_launcher_are_owned(self):
        with tempfile.TemporaryDirectory(prefix="ghcp appimage ") as directory:
            root = Path(directory)
            appdir, image = self.fixture(root)
            data = root / "data"
            installer.install(appdir / "payload", data, True, "hyprland", appdir=appdir, appimage=image)
            runtime = data / "ghcp-spend-tray-desktop"
            self.assertEqual((runtime / "GHCPSpendTray.AppImage").read_bytes(), image.read_bytes())
            self.assertEqual((runtime / installer.MARKER).read_text(), installer.PACKAGE)
            module = json.loads((data / "ghcp-spend-tray-hyprland/waybar-module.json").read_text())
            self.assertNotIn("python3", module["custom/ghcp-spend-tray"]["exec"])
            self.assertIn("--panel", module["custom/ghcp-spend-tray"]["exec"])
            launcher = (data / "applications/ghcp-spend-tray.desktop").read_text()
            self.assertIn("--appimage-extract-and-run", launcher)
            self.assertNotIn(str(image), launcher)
            image.unlink()
            installer.uninstall(data, True)
            self.assertFalse(runtime.exists())
            self.assertFalse((data / "applications/ghcp-spend-tray.desktop").exists())

    def test_failed_runtime_replacement_restores_old_image_and_helper(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            appdir, image = self.fixture(root)
            data = root / "data"
            installer.install(appdir / "payload", data, True, appdir=appdir, appimage=image)
            image.write_bytes(b"new synthetic image")
            (appdir / "payload/helper/GHCPSpendTray.Linux").write_text("new helper")
            rename = Path.rename

            def fail_runtime(path, target):
                if path.name == "runtime":
                    raise OSError("injected runtime replacement failure")
                return rename(path, target)

            with patch.object(Path, "rename", fail_runtime), self.assertRaisesRegex(OSError, "injected"):
                installer.install(appdir / "payload", data, True, appdir=appdir, appimage=image)
            self.assertEqual((data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux").read_text(), "synthetic helper")
            self.assertEqual((data / "ghcp-spend-tray-desktop/GHCPSpendTray.AppImage").read_bytes(), b"synthetic image")

    def test_bundle_requires_source_image_and_refuses_unowned_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            appdir, image = self.fixture(root)
            data = root / "data"
            with self.assertRaisesRegex(RuntimeError, "downloaded AppImage"):
                installer.install(appdir / "payload", data, True, appdir=appdir)
            (data / "ghcp-spend-tray-desktop").mkdir(parents=True)
            with self.assertRaisesRegex(RuntimeError, "not owned"):
                installer.install(appdir / "payload", data, True, appdir=appdir, appimage=image)

    def test_external_programs_do_not_inherit_bundled_qt_libraries(self):
        with patch.object(installer.sys, "frozen", True, create=True), \
                patch.dict(os.environ, LD_LIBRARY_PATH="/bundle", LD_LIBRARY_PATH_ORIG="/host",
                           QT_PLUGIN_PATH="/bundle/plugins"):
            environment = installer.host_environment()
            self.assertEqual(environment["LD_LIBRARY_PATH"], "/host")
            self.assertNotIn("QT_PLUGIN_PATH", environment)
