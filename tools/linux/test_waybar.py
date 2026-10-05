import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from test_native_package import installer

waybar = installer.waybar


class WaybarTests(unittest.TestCase):
    def test_jsonc_comments_includes_update_and_exact_restoration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "base.jsonc").write_text('{"modules-right": ["clock"], "height": 32}')
            config = root / "config.jsonc"
            original = ('{\n// retain comments\n"include": ' + json.dumps(str(root / "base.jsonc")) +
                        ',\n"url": "https://example.test/a/*b*/",\n}')
            config.write_text(original)
            state = waybar.prepare(config, None, {"exec": "example"})
            self.assertIn("// retain comments", state["installed"])
            self.assertIn('"include": ' + json.dumps(str(root / "base.jsonc")), state["installed"])
            value = waybar.Document(state["installed"]).bar(None)["value"]
            self.assertEqual(value["modules-right"], ["clock", waybar.MODULE])
            self.assertEqual(value["url"], "https://example.test/a/*b*/")
            self.assertEqual(waybar.remove(state["installed"], state), original)
            config.write_text(state["installed"])
            updated = waybar.prepare(config, None, {"exec": "updated"}, state)
            self.assertEqual(waybar.remove(updated["installed"], updated), original)
            self.assertEqual((root / "base.jsonc").read_text(), '{"modules-right": ["clock"], "height": 32}')

    def test_uninstall_preserves_later_user_edits_and_restores_inheritance(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config"
            config.write_text('{"height": 30}')
            state = waybar.prepare(config, None, {"exec": "example"})
            edited = waybar.set_field(state["installed"], None, "height", 36)
            result = waybar.Document(waybar.remove(edited, state)).root["value"]
            self.assertEqual(result, {"height": 36})
            edited = waybar.set_field(edited, None, "modules-right", [waybar.MODULE, "clock"])
            result = waybar.Document(waybar.remove(edited, state)).root["value"]
            self.assertEqual(result, {"height": 36, "modules-right": ["clock"]})

    def test_existing_module_collision_and_modified_owned_module_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config"
            config.write_text(json.dumps({waybar.MODULE: {"exec": "unowned"}}))
            with self.assertRaisesRegex(ValueError, "already exists"):
                waybar.prepare(config, None, {})
            config.write_text("{}")
            state = waybar.prepare(config, None, {"exec": "ours"})
            edited = waybar.set_field(state["installed"], None, waybar.MODULE, {"exec": "modified"})
            with self.assertRaisesRegex(ValueError, "edited"):
                waybar.remove(edited, state)

    def test_multiple_bars_require_explicit_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config"
            config.write_text('[{"name":"first"}, {"name":"second",}]')
            with self.assertRaisesRegex(ValueError, "Multiple"):
                waybar.prepare(config, None, {})
            for index in (-1, 2):
                with self.assertRaisesRegex(ValueError, "range"):
                    waybar.prepare(config, index, {})
            state = waybar.prepare(config, 1, {})
            result = waybar.Document(state["installed"]).root["value"]
            self.assertNotIn(waybar.MODULE, result[0])
            self.assertIn(waybar.MODULE, result[1])

    def test_invalid_documents_and_include_cycles(self):
        for source in ('', '{', '{"a":1,"a":2}', '[,]', '{} false', '{"a": [}'):
            with self.subTest(source=source), self.assertRaises(ValueError):
                waybar.Document(source)
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "config"
            config.write_text(json.dumps({"include": str(config)}))
            with self.assertRaisesRegex(ValueError, "Cyclic"):
                waybar.prepare(config, None, {})
            config.write_text('{"include":"relative.jsonc"}')
            with self.assertRaisesRegex(ValueError, "launch directory"):
                waybar.prepare(config, None, {})

    def test_field_removal_at_each_position_remains_valid(self):
        for source in ('{"a":1,"b":2,"c":3}', '{"a":1,/*x*/"b":2,"c":3,}', '{"a":1}'):
            for key in waybar.Document(source).root["value"]:
                result = waybar.Document(waybar.remove_field(source, None, key)).root["value"]
                self.assertNotIn(key, result)

    def test_installer_updates_and_reverses_configuration_transactionally(self):
        with tempfile.TemporaryDirectory(prefix="ghcp waybar ") as directory:
            root = Path(directory)
            payload = root / "payload"
            (payload / "helper").mkdir(parents=True)
            (payload / "helper/GHCPSpendTray.Linux").write_text("synthetic")
            (payload / "hyprland").mkdir()
            (payload / "hyprland/panel.py").write_text("# synthetic test fixture")
            data = root / "data"
            config = root / "config.jsonc"
            original = '{/* my bar */"modules-right":["clock"],}'
            config.write_text(original)
            with patch.object(installer, "stop_helper"), patch.object(installer.subprocess, "run"):
                installer.install(payload, data, False, "hyprland", config)
                installer.install(payload, data, False, "hyprland", config)
                state_path = data / "ghcp-spend-tray-hyprland/waybar-integration.json"
                self.assertEqual(state_path.stat().st_mode & 0o777, 0o600)
                modules = waybar.Document(config.read_text()).root["value"]["modules-right"]
                self.assertEqual(modules.count(waybar.MODULE), 1)
                with self.assertRaisesRegex(RuntimeError, "desktop-aware"):
                    installer.uninstall(data, True)
                installer.uninstall(data, False)
            self.assertEqual(config.read_text(), original)

    def test_failed_config_replacement_rolls_back_package_and_config(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = root / "payload"
            (payload / "helper").mkdir(parents=True)
            (payload / "helper/GHCPSpendTray.Linux").write_text("synthetic")
            (payload / "hyprland").mkdir()
            data = root / "data"
            config = root / "config"
            config.write_text("{}")
            rename = Path.rename

            def fail_config(path, target):
                if path.name == "waybar-config":
                    raise OSError("injected config replacement failure")
                return rename(path, target)

            with patch.object(installer, "stop_helper"), patch.object(Path, "rename", fail_config):
                with self.assertRaisesRegex(OSError, "injected"):
                    installer.install(payload, data, False, "hyprland", config)
            self.assertEqual(config.read_text(), "{}")
            self.assertFalse((data / "ghcp-spend-tray-hyprland").exists())
