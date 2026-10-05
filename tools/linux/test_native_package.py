import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "packaging/linux"))
spec = importlib.util.spec_from_file_location("native_installer", ROOT / "packaging/linux/install.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerTests(unittest.TestCase):
    def test_desktop_payloads_and_launcher_are_owned_and_removed(self):
        with tempfile.TemporaryDirectory(prefix="ghcp multi ") as directory:
            root = Path(directory)
            payload = root / "payload"
            for part in ("helper", "kde", "hyprland"):
                (payload / part).mkdir(parents=True)
            (payload / "helper/GHCPSpendTray.Linux").write_text("synthetic")
            (payload / "kde/metadata.json").write_text("{}")
            (payload / "hyprland/shell.qml").write_text("Scope {}")
            data = root / "data"
            installer.install(payload, data, True, "kde")
            installer.install(payload, data, True, "hyprland")
            self.assertTrue((data / "plasma/plasmoids/io.github.ghcpspendtray.demo/metadata.json").exists())
            import json
            config = json.loads((data / "ghcp-spend-tray-hyprland/waybar-module.json").read_text())
            self.assertNotIn("exclusive", config)
            self.assertIn("panel.py", config["custom/ghcp-spend-tray"]["on-click"])
            self.assertTrue((data / "applications/ghcp-spend-tray-hyprland.desktop").exists())
            installer.uninstall(data, True)
            self.assertFalse((data / "ghcp-spend-tray-hyprland").exists())
            self.assertFalse((data / "plasma/plasmoids/io.github.ghcpspendtray.demo").exists())

    def test_install_update_uninstall_in_paths_with_spaces(self):
        with tempfile.TemporaryDirectory(prefix="ghcp native ") as directory:
            root = Path(directory)
            data = root / "data with spaces"
            payload = root / "payload"
            (payload / "helper").mkdir(parents=True)
            (payload / "helper/GHCPSpendTray.Linux").write_text("#!/bin/sh\nexit 0\n")
            (payload / "extension").mkdir()
            (payload / "extension/metadata.json").write_text("{}")
            installer.install(payload, data, True)
            binary = data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux"
            subprocess.run([str(binary)], check=True)
            service = data / "dbus-1/services" / (installer.BUS + ".service")
            self.assertIn(str(binary), service.read_text())
            (payload / "extension/metadata.json").write_text('{"version": 2}')
            installer.install(payload, data, True)
            self.assertEqual((data / "gnome-shell/extensions" / installer.UUID / "metadata.json").read_text(),
                             '{"version": 2}')
            unrelated = data / "preserve-me"
            unrelated.write_text("unrelated")
            installer.uninstall(data, True)
            self.assertFalse(binary.exists())
            self.assertFalse(service.exists())
            self.assertTrue(unrelated.exists())

    def test_refuses_unowned_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unowned"
            path.mkdir()
            with self.assertRaisesRegex(RuntimeError, "not owned"):
                installer.owned(path, True)
            link = Path(directory) / "link"
            link.symlink_to(path)
            with self.assertRaisesRegex(RuntimeError, "symlink"):
                installer.owned(link, True)

    def test_failed_upgrade_restores_previous_components(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = root / "payload"
            (payload / "helper").mkdir(parents=True)
            (payload / "helper/GHCPSpendTray.Linux").write_text("old helper")
            (payload / "extension").mkdir()
            (payload / "extension/metadata.json").write_text("old metadata")
            data = root / "data"
            installer.install(payload, data, True)
            service = data / "dbus-1/services" / (installer.BUS + ".service")
            previous_service = service.read_text()
            (payload / "helper/GHCPSpendTray.Linux").write_text("new helper")
            (payload / "extension/metadata.json").write_text("new metadata")
            rename = Path.rename

            def fail_extension(path, target):
                if path.name == "extension" and path.parent.name.startswith(".ghcp-install-"):
                    raise OSError("injected replacement failure")
                return rename(path, target)

            with patch.object(Path, "rename", fail_extension):
                with self.assertRaisesRegex(OSError, "injected"):
                    installer.install(payload, data, True)
            self.assertEqual((data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux").read_text(), "old helper")
            self.assertEqual((data / "gnome-shell/extensions" / installer.UUID / "metadata.json").read_text(), "old metadata")
            self.assertEqual(service.read_text(), previous_service)


if __name__ == "__main__":
    unittest.main()
