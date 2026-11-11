#!/usr/bin/env python3
"""Exercise libsecret only on private buses with a temporary synthetic keyring."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def stop_keyring(daemon):
    if daemon is not None and daemon.poll() is None:
        daemon.terminate()
        try:
            daemon.wait(timeout=10)
        except subprocess.TimeoutExpired:
            daemon.kill()
            daemon.wait(timeout=5)


def start_keyring(control):
    daemon = subprocess.Popen(
        ["gnome-keyring-daemon", "--foreground", "--unlock", "--components=secrets",
         f"--control-directory={control}"],
        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL)
    try:
        daemon.stdin.write(b"synthetic-keyring-password")
        daemon.stdin.close()
        for _ in range(100):
            owner = subprocess.run(
                ["gdbus", "call", "--session", "--dest", "org.freedesktop.DBus",
                 "--object-path", "/org/freedesktop/DBus", "--method",
                 "org.freedesktop.DBus.NameHasOwner", "org.freedesktop.secrets"],
                check=True, capture_output=True, text=True, timeout=5)
            if "true" in owner.stdout:
                return daemon
            if daemon.poll() is not None:
                raise RuntimeError("Synthetic keyring exited before becoming ready.")
            time.sleep(0.05)
        raise RuntimeError("Synthetic keyring did not become ready.")
    except BaseException:
        stop_keyring(daemon)
        raise


def run_inside(command, mode):
    daemon = None
    try:
        if mode == "--keyring":
            control = Path(os.environ["XDG_RUNTIME_DIR"]) / "keyring"
            control.mkdir(mode=0o700)
            daemon = start_keyring(control)
        subprocess.run([*command, mode], check=True, timeout=180)
        if mode == "--keyring":
            subprocess.run([*command, "--keyring-seed"], check=True, timeout=30)
            subprocess.run(["gdbus", "call", "--session", "--dest", "org.freedesktop.secrets",
                            "--object-path", "/org/freedesktop/secrets",
                            "--method", "org.freedesktop.Secret.Service.Lock",
                            "[objectpath '/org/freedesktop/secrets/collection/login']"],
                           check=True, capture_output=True, timeout=10)
            subprocess.run([*command, "--keyring-locked"], check=True, timeout=30)
            stop_keyring(daemon)
            daemon = start_keyring(control)
            subprocess.run([*command, "--keyring-cleanup"], check=True, timeout=30)
    finally:
        stop_keyring(daemon)


def main():
    if sys.argv[1] == "--inside":
        run_inside(sys.argv[3:], sys.argv[2])
        return
    for mode in ("--no-keyring", "--keyring"):
        with tempfile.TemporaryDirectory(prefix="ghcp-keyring-") as directory:
            root = Path(directory)
            runtime = root / "runtime"
            runtime.mkdir(mode=0o700)
            config = root / "bus.conf"
            config.write_text(
                '<busconfig><type>session</type><listen>unix:tmpdir=/tmp</listen><auth>EXTERNAL</auth>'
                '<policy context="default"><allow send_destination="*"/><allow receive_sender="*"/>'
                '<allow own="*"/></policy></busconfig>')
            env = dict(os.environ, HOME=str(root), XDG_DATA_HOME=str(root / "data"),
                       XDG_CONFIG_HOME=str(root / "config"), XDG_CACHE_HOME=str(root / "cache"),
                       XDG_STATE_HOME=str(root / "state"), XDG_RUNTIME_DIR=str(runtime),
                       GHCP_ISOLATED_KEYRING="1")
            for key in ("DBUS_SESSION_BUS_ADDRESS", "GNOME_KEYRING_CONTROL", "DISPLAY", "WAYLAND_DISPLAY"):
                env.pop(key, None)
            subprocess.run(["dbus-run-session", f"--config-file={config}", "--", sys.executable,
                            str(Path(__file__).resolve()), "--inside", mode, *sys.argv[1:]],
                           env=env, check=True, timeout=200)


if __name__ == "__main__":
    main()
