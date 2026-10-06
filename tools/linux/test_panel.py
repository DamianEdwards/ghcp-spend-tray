import contextlib
import fcntl
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("hyprland_panel", ROOT / "src/GHCPSpendTray.Linux.Hyprland/panel.py")
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class PanelTests(unittest.TestCase):
    def test_native_activation_opens_selected_account_without_toggling(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, XDG_RUNTIME_DIR=directory), \
                    patch.object(panel.sys, "argv", ["panel.py", "open", "--account", "github.com:1", "--settings"]), \
                    patch.object(panel.subprocess, "Popen"), \
                    patch.object(panel.subprocess, "run", return_value=Mock(returncode=0)) as run:
                panel.main()
            self.assertEqual(run.call_args.args[0][-3:], ["open", "github.com:1", "true"])
            self.assertEqual(run.call_args_list[0].args[0][-1], "ready")

    def test_status_rejects_invalid_data_and_reports_unavailable(self):
        for value in ({}, [], {"version": 2, "demo": True, "indicator": 0, "consumption": "$0"}):
            with patch.object(panel.subprocess, "run", return_value=Mock(stdout=json.dumps(value))):
                with self.assertRaisesRegex(RuntimeError, "Unsupported"):
                    panel.snapshot(Path("/synthetic-helper"))
        output = io.StringIO()
        errors = io.StringIO()
        with patch.object(panel.sys, "argv", ["panel.py", "status"]), \
                patch.object(panel, "snapshot", side_effect=RuntimeError("offline")), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            panel.main()
        self.assertEqual(json.loads(output.getvalue())["text"], "?")
        self.assertIn("offline", errors.getvalue())

    def test_existing_supervisor_prevents_duplicate_popup(self):
        with tempfile.TemporaryDirectory() as directory:
            with (Path(directory) / "ghcp-spend-tray-demo-panel.lock").open("w") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                with patch.dict(os.environ, XDG_RUNTIME_DIR=directory, WAYLAND_DISPLAY="synthetic"), \
                        patch.object(panel.sys, "argv", ["panel.py", "start"]), \
                        patch.object(panel.subprocess, "Popen") as spawn:
                    panel.main()
                spawn.assert_not_called()

    def test_start_owns_only_quickshell_and_releases_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            process = Mock()
            process.wait.return_value = 0
            process.poll.return_value = 0
            with patch.dict(os.environ, XDG_RUNTIME_DIR=directory, WAYLAND_DISPLAY="synthetic"), \
                    patch.object(panel.sys, "argv", ["panel.py", "start"]), \
                    patch.object(panel.signal, "signal"), \
                    patch.object(panel.subprocess, "Popen", return_value=process) as spawn:
                panel.main()
            self.assertEqual(spawn.call_args.args[0][0], "quickshell")
            self.assertEqual(spawn.call_count, 1)
            with (Path(directory) / "ghcp-spend-tray-demo-panel.lock").open("r") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_click_waits_for_ready_before_toggling(self):
        with tempfile.TemporaryDirectory() as directory:
            process = Mock()
            process.poll.return_value = None
            with patch.dict(os.environ, XDG_RUNTIME_DIR=directory), \
                    patch.object(panel.sys, "argv", ["panel.py", "toggle"]), \
                    patch.object(panel.subprocess, "Popen", return_value=process), \
                    patch.object(panel.subprocess, "run", return_value=Mock(returncode=0)) as run:
                panel.main()
            self.assertEqual([call.args[0][-1] for call in run.call_args_list], ["ready", "toggle"])
