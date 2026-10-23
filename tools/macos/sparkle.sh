#!/bin/bash
# Source from the repository root to acquire the checksum-pinned binary dependency.
set -euo pipefail
SPARKLE_ROOT="$(python3 - <<'PY'
import json
from pathlib import Path
lock = json.loads(Path("packaging/macos/sparkle.json").read_text())
print(Path("artifacts/sparkle", lock["version"]).resolve())
PY
)"
mkdir -p "$SPARKLE_ROOT"
python3 - "$SPARKLE_ROOT" <<'PY'
import hashlib
import json
from pathlib import Path
import subprocess
import sys
lock = json.loads(Path("packaging/macos/sparkle.json").read_text())
root = Path(sys.argv[1])
archive = root / "distribution.tar.xz"
if not archive.is_file() or hashlib.sha256(archive.read_bytes()).hexdigest() != lock["sha256"]:
    subprocess.run(["curl", "--fail", "--location", "--silent", "--show-error",
                    "--proto", "=https", "--proto-redir", "=https",
                    lock["url"], "--output", str(archive)], check=True)
if hashlib.sha256(archive.read_bytes()).hexdigest() != lock["sha256"]:
    raise SystemExit("Sparkle distribution checksum mismatch.")
subprocess.run(["tar", "-xJf", str(archive), "-C", str(root),
                "./Sparkle.framework", "./bin", "./LICENSE"], check=True)
subprocess.run(["codesign", "--verify", "--deep", "--strict", str(root / "Sparkle.framework")], check=True)
PY
export SPARKLE_ROOT
