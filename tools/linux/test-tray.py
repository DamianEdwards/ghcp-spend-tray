#!/usr/bin/env python3
"""Exercise native tray protocol and popup routing on a private bus with synthetic data."""
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    helper = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="ghcp-tray-") as directory:
        root = Path(directory)
        runtime = root / "runtime"
        runtime.mkdir(mode=0o700)
        launcher = root / "data/ghcp-spend-tray-desktop/ghcp-spend-tray-setup"
        launcher.parent.mkdir(parents=True)
        launcher.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\" >> " + shlex.quote(str(root / "activation")) + "\n")
        launcher.chmod(0o700)
        env = dict(os.environ, HOME=str(root), XDG_DATA_HOME=str(root / "data"),
                   XDG_CONFIG_HOME=str(root / "config"), XDG_STATE_HOME=str(root / "state"),
                   XDG_CACHE_HOME=str(root / "cache"), XDG_RUNTIME_DIR=str(runtime))
        env.pop("DBUS_SESSION_BUS_ADDRESS", None)
        subprocess.run(["dbus-run-session", "--config-file=" + str(ROOT / "tools/linux/session-bus.conf"),
                        "--", "gjs", "-m", str(ROOT / "tools/linux/test-tray.js"), str(helper), str(root)],
                       env=env, check=True, timeout=45)


if __name__ == "__main__":
    main()
