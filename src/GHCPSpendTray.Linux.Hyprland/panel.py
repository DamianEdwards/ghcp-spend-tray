#!/usr/bin/env python3
"""Waybar status/click adapter and single-instance Quickshell lifecycle."""

import argparse
import fcntl
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


def paths():
    data = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))
    root = data / "ghcp-spend-tray-hyprland" if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
    return root, data / "ghcp-spend-tray-demo/GHCPSpendTray.Linux"


def own_command(root, action):
    if getattr(sys, "frozen", False):
        return [sys.executable, "--panel", action]
    return [sys.executable, str(root / "panel.py"), action]


def snapshot(helper):
    result = subprocess.run([str(helper), "--command", "GetSnapshot"], capture_output=True,
                            text=True, check=True, timeout=15)
    value = json.loads(result.stdout)
    if (len(result.stdout) > 1024 * 1024 or not isinstance(value, dict) or
            value.get("version") != 4 or not isinstance(value.get("demo"), bool) or
            any(not isinstance(value.get(key), str) or len(value[key]) > 1024
                for key in ("indicator", "consumption"))):
        raise RuntimeError("Unsupported helper snapshot.")
    return value


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["start", "stop", "toggle", "open", "status"])
    parser.add_argument("--account", default="")
    parser.add_argument("--settings", action="store_true")
    args = parser.parse_args()
    root, helper = paths()
    runtime = Path(os.environ.get("XDG_RUNTIME_DIR", f"/run/user/{os.getuid()}"))
    lock_path = runtime / "ghcp-spend-tray-demo-panel.lock"
    if args.action == "stop":
        if not lock_path.exists():
            return
        with lock_path.open("r") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return
            except BlockingIOError:
                pid = int(lock.read())
                proc = Path("/proc") / str(pid)
                expected = (str(Path(sys.executable).resolve()) if getattr(sys, "frozen", False)
                            else str(root / "panel.py")).encode()
                arguments = (proc / "cmdline").read_bytes().split(b"\0")
                if proc.stat().st_uid != os.getuid() or expected not in arguments or b"start" not in arguments:
                    raise RuntimeError("Panel PID does not belong to this installation.")
                os.kill(pid, signal.SIGTERM)
                for _ in range(100):
                    try:
                        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        return
                    except BlockingIOError:
                        time.sleep(0.1)
                raise RuntimeError("Panel did not stop in time.")
    if args.action == "status":
        try:
            value = snapshot(helper)
            print(json.dumps({"text": value["indicator"], "tooltip": "GHCPSpendTray - " + value["consumption"],
                              "class": "demo" if value["demo"] else "usage"}))
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            print(f"GHCPSpendTray panel: {error}", file=sys.stderr)
            print(json.dumps({"text": "?", "tooltip": "GHCPSpendTray unavailable", "class": "error"}))
        return
    if args.action in ("toggle", "open"):
        if not runtime.is_absolute() or not runtime.is_dir() or runtime.stat().st_uid != os.getuid():
            raise RuntimeError("A user-owned absolute XDG_RUNTIME_DIR is required.")
        log_path = runtime / "ghcp-spend-tray-demo-panel.log"
        with log_path.open("a") as log:
            starter = subprocess.Popen(own_command(root, "start"),
                                       stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
        try:
            for _ in range(50):
                ready = subprocess.run(["quickshell", "ipc", "-p", str(root / "shell.qml"), "call", "usage", "ready"],
                                       capture_output=True, text=True, timeout=2)
                if ready.returncode == 0:
                    break
                if starter.poll() not in (None, 0):
                    raise RuntimeError(f"Popup failed to start. See {log_path}")
                time.sleep(0.1)
            else:
                raise RuntimeError(f"Popup IPC did not become ready. See {log_path}")
        except (OSError, RuntimeError, subprocess.SubprocessError):
            if starter.poll() is None:
                starter.terminate()
                starter.wait(timeout=10)
            raise
        action = ["open", args.account, str(args.settings).lower()] if args.action == "open" else ["toggle"]
        subprocess.run(["quickshell", "ipc", "-p", str(root / "shell.qml"), "call", "usage", *action],
                       check=True, timeout=10)
        return
    if not os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("The Hyprland panel requires a Wayland session.")
    if not runtime.is_absolute() or not runtime.is_dir() or runtime.stat().st_uid != os.getuid():
        raise RuntimeError("A user-owned absolute XDG_RUNTIME_DIR is required.")
    lock = lock_path.open("a+")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        lock.close()
        return
    lock.seek(0)
    lock.truncate()
    lock.write(str(os.getpid()))
    lock.flush()
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    signal.signal(signal.SIGINT, lambda *_: sys.exit(0))
    quickshell = None
    try:
        quickshell = subprocess.Popen(["quickshell", "-p", str(root / "shell.qml")])
        if quickshell.wait() != 0:
            raise RuntimeError("Quickshell exited with an error.")
    finally:
        for process in (quickshell,):
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        lock.close()


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"GHCPSpendTray panel failed: {error}", file=sys.stderr)
        sys.exit(1)
