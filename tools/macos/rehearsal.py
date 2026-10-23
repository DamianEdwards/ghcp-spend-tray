#!/usr/bin/env python3
"""Exercise real Sparkle app replacement locally; never create a release or deploy a feed."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import select
import subprocess
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET

from architecture import require_host

ROOT = Path(__file__).resolve().parents[2]
NAMESPACE = "http://www.andymatuschak.org/xml-namespaces/sparkle"
ET.register_namespace("sparkle", NAMESPACE)
VERSIONS = ("9000.0.1", "9000.0.2")
SCENARIOS = ("manual", "background-relaunch", "on-quit", "cancel-download", "cancel-install",
             "corrupt-archive", "wrong-signature", "interrupted-download")


def run(*arguments, env=None, input=None):
    subprocess.run(list(map(str, arguments)), cwd=ROOT, env=env, input=input, text=True, check=True)


def fingerprint(app):
    result = {}
    if not app.is_dir():
        return result
    for path in sorted(app.rglob("*")):
        name = str(path.relative_to(app))
        if path.is_symlink():
            result[name] = dict(link=os.readlink(path))
        elif path.is_file():
            result[name] = dict(sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                                executable=bool(path.stat().st_mode & 0o111))
    return result


def events(root):
    path = root / "events.jsonl"
    if not path.exists():
        return []
    data = path.read_bytes()
    # A writer may still be appending the final line.
    return [json.loads(line) for line in data[:data.rfind(b"\n") + 1].splitlines()]


def wait_for(predicate, *, timeout, description, process=None):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        if process is not None and process.poll() is not None:
            raise RuntimeError(f"App exited ({process.returncode}) before {description}.")
        time.sleep(0.05)
    raise RuntimeError(f"Timed out after {timeout}s awaiting {description}; see retained rehearsal logs.")


def terminate(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)


def sign(app, identity):
    options = ["--force", "--sign", identity, "--options", "runtime", "--timestamp"]
    run("codesign", *options, app / "Contents/Frameworks/GHCPSpendTray.MacBridge.dylib")
    run("bash", "tools/macos/sign-sparkle.sh", app, identity)
    run("codesign", *options, app)
    run("codesign", "--verify", "--deep", "--strict", app)
    signature = subprocess.run(["codesign", "--display", "--verbose=4", str(app)], capture_output=True, text=True, check=True)
    if "Authority=Developer ID Application:" not in signature.stderr or "runtime" not in signature.stderr:
        raise RuntimeError("The rehearsal must use Developer ID signing and hardened runtime.")


def configure(app, value, identifier, configuration, public_key, automatic):
    path = app / "Contents/Info.plist"
    info = plistlib.loads(path.read_bytes())
    info.update(CFBundleIdentifier=identifier, CFBundleVersion=value, CFBundleShortVersionString=value,
                GHCPReleaseChannel="Stable", GHCPUpdateRehearsal=str(configuration),
                SUFeedURL=json.loads(configuration.read_text())["feed"], SUPublicEDKey=public_key,
                SUEnableAutomaticChecks=automatic, SUAutomaticallyUpdate=automatic,
                SUEnableSystemProfiling=False, SURequireSignedFeed=True, SUVerifyUpdateBeforeExtraction=True)
    path.write_bytes(plistlib.dumps(info, sort_keys=False))


def archive(app, path, identity):
    run("hdiutil", "create", "-quiet", "-volname", "GHCPSpendTray Rehearsal", "-srcfolder", app,
        "-format", "UDZO", path)
    run("codesign", "--force", "--sign", identity, "--timestamp", path)


def sign_file(tool, path, seed, *, signature=False):
    arguments = [str(tool), "--ed-key-file", "-", *(['-p'] if signature else []), str(path)]
    result = subprocess.run(arguments, cwd=ROOT, input=seed + "\n", text=True, capture_output=True, check=True)
    if result.stderr:
        print(result.stderr, end="", flush=True)
    if signature:
        return result.stdout.strip()


def feed(path, *, archive, archive_url, value, seed, sign_tool):
    signature = sign_file(sign_tool, archive, seed, signature=True)
    root = ET.Element("rss", version="2.0")
    channel = ET.SubElement(root, "channel")
    ET.SubElement(channel, "title").text = "Isolated local update rehearsal"
    item = ET.SubElement(channel, "item")
    ET.SubElement(item, "title").text = value
    for name, text in (("version", value), ("shortVersionString", value),
                       ("minimumSystemVersion", "15.0"), ("hardwareRequirements", "arm64")):
        ET.SubElement(item, f"{{{NAMESPACE}}}{name}").text = text
    ET.SubElement(item, "enclosure", url=archive_url, length=str(archive.stat().st_size),
                  type="application/octet-stream", **{f"{{{NAMESPACE}}}edSignature": signature})
    path.write_bytes(ET.tostring(root, encoding="utf-8", xml_declaration=True))
    sign_file(sign_tool, path, seed)


def launch(app, log):
    return subprocess.Popen([str(app / "Contents/MacOS/GHCPSpendTray")], cwd=ROOT,
                            stdout=log, stderr=log, start_new_session=True)

def validate_rejection(name, records):
    required = {"cancel-download": "cancelled-download", "cancel-install": "cancelled-install",
                "corrupt-archive": "updater-error", "wrong-signature": "updater-error",
                "interrupted-download": "updater-error"}[name]
    if any(item["event"] == "failure" or item["marker"] == "rehearsal-B" for item in records):
        raise RuntimeError("The negative scenario failed or unexpectedly installed B.")
    if not any(item["event"] == required for item in records):
        raise RuntimeError("The expected rejection/cancellation callback did not occur.")
    if name in ("corrupt-archive", "wrong-signature"):
        errors = [item for item in records if item["event"] == "updater-error"]
        if not any(item["domain"] == "SUSparkleErrorDomain" and item["code"] == 4005 and
                   any(error["domain"] == "SUSparkleErrorDomain" and error["code"] in (3001, 3002)
                       for error in item["errors"]) for item in errors):
            raise RuntimeError("The bad archive was not rejected specifically for signature validation.")
        if any(item["event"] == "ready-to-install" for item in records):
            raise RuntimeError("The bad archive reached installation readiness.")
    if name == "interrupted-download" and not any(
        item["event"] == "updater-error" and item["domain"] == "SUSparkleErrorDomain" and item["code"] == 2001
        for item in records
    ):
        raise RuntimeError("The interrupted archive did not fail with a download error.")


def scenario(name, root, base_apps, base_url, identity, public_key, seed, wrong_seed, sign_tool):
    print(f"REHEARSAL: {name}", flush=True)
    root.mkdir()
    install = root / "installed/GHCPSpendTray.app"
    candidate = root / "candidate/GHCPSpendTray.app"
    install.parent.mkdir()
    candidate.parent.mkdir()
    identifier = "com.damianedwards.GHCPSpendTray.rehearsal." + uuid.uuid4().hex
    configuration = root / "rehearsal.json"
    config = dict(root=str(root), identifier=identifier, installedApp=str(install), scenario=name,
                  feed=f"{base_url}/{name}/appcast.xml", downgradeFeed=f"{base_url}/{name}/downgrade.xml")
    configuration.write_text(json.dumps(config))
    automatic = name in ("background-relaunch", "on-quit")
    for base, app, value in zip(base_apps, (install, candidate), VERSIONS):
        run("ditto", base, app)
        configure(app, value, identifier, configuration, public_key, automatic)
        sign(app, identity)
    original, expected = fingerprint(install), fingerprint(candidate)
    if original["Contents/MacOS/GHCPSpendTray"] == expected["Contents/MacOS/GHCPSpendTray"]:
        raise RuntimeError("The rehearsal requires distinct A/B executable bytes, not just different version labels.")
    archive_a = root / "old.dmg"
    archive_b = root / ("interrupted.dmg" if name == "interrupted-download" else "update.dmg")
    archive(install, archive_a, identity)
    archive(candidate, archive_b, identity)
    feed(root / "downgrade.xml", archive=archive_a, archive_url=f"{base_url}/{name}/old.dmg",
         value=VERSIONS[0], seed=seed, sign_tool=sign_tool)
    feed(root / "appcast.xml", archive=archive_b, archive_url=f"{base_url}/{name}/{archive_b.name}",
         value=VERSIONS[1], seed=wrong_seed if name == "wrong-signature" else seed, sign_tool=sign_tool)
    # Authenticate the feed with the expected key; only the archive signature is wrong.
    if name == "wrong-signature":
        sign_file(sign_tool, root / "appcast.xml", seed)
    if name == "corrupt-archive":
        with archive_b.open("ab") as file:
            file.write(b"synthetic-rehearsal-corruption")
    log_path = root / "app.log"
    process = None
    observed_pids = set()
    cleaned = False
    try:
        with log_path.open("ab", buffering=0) as log:
            process = launch(install, log)
            observed_pids.add(process.pid)
            success = name in ("manual", "background-relaunch", "on-quit")
            if success:
                if name == "on-quit":
                    wait_for(lambda: fingerprint(install) == expected, timeout=120,
                             description="Sparkle installation on quit")
                    process.wait(timeout=15)
                    process = launch(install, log)
                    observed_pids.add(process.pid)
                def completed():
                    records = events(root)
                    failures = [item for item in records if item["event"] == "failure"]
                    if failures:
                        raise RuntimeError(f"Rehearsal app failed: {failures[-1]['message']}")
                    return any(item["event"] == "not-found" and item["marker"] == "rehearsal-B" for item in records)
                wait_for(completed, timeout=120, description="B relaunch and downgrade rejection")
                records = events(root)
                started = [item for item in records if item["event"] == "started"]
                pid_a = next(item["pid"] for item in started if item["marker"] == "rehearsal-A")
                pid_b = next(item["pid"] for item in started if item["marker"] == "rehearsal-B")
                observed_pids.add(pid_b)
                if pid_a == pid_b or any(item["bundle"] != str(install) for item in started):
                    raise RuntimeError("Replacement did not relaunch a new process from the same installation path.")
                if fingerprint(install) != expected:
                    raise RuntimeError("Installed file bytes do not match the signed candidate bundle.")
                if not any(item["event"] == "preserved" for item in records):
                    raise RuntimeError("B did not confirm synthetic settings/history/Keychain preservation.")
                if not any(item["event"] == "not-found" and item["code"] == 1001 and item["reason"] == 2
                           and item["marker"] == "rehearsal-B" for item in records):
                    raise RuntimeError("B did not reject the older signed update as a downgrade.")
                if automatic and not any(item["event"] == "scheduled-on-quit" for item in records):
                    raise RuntimeError("Automatic download did not reach real install-on-quit scheduling.")
                if not any(item["event"] == "update-preferences" and item["marker"] == "rehearsal-B"
                           and item["checks"] == automatic and item["downloads"] == automatic for item in records):
                    raise RuntimeError("The installed B did not preserve the native update preferences.")
            else:
                process.wait(timeout=120)
                records = events(root)
                validate_rejection(name, records)
                if fingerprint(install) != original:
                    raise RuntimeError("A negative update changed the installed A bundle.")
            run("codesign", "--verify", "--deep", "--strict", install)
            config["scenario"] = "cleanup"
            configuration.write_text(json.dumps(config))
            if process.poll() is None:
                process.wait(timeout=15)
            process = launch(install, log)
            observed_pids.add(process.pid)
            process.wait(timeout=20)
            if process.returncode != 0 or not any(item["event"] == "cleaned" for item in events(root)):
                raise RuntimeError("Could not clean up the unique rehearsal defaults/Keychain item.")
            cleaned = True
        result = dict(scenario=name, result="passed", installedVersion=VERSIONS[1] if success else VERSIONS[0],
                      samePath=True, exactBundleBytes=True, signatureVerified=True,
                      evidence=str(root / "events.jsonl"))
        (root / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(f"PASS: {name}", flush=True)
        return result
    finally:
        if process is not None:
            terminate(process)
        # Relaunch is owned by Sparkle, not subprocess.Popen; only terminate recorded fixture PIDs.
        for record in events(root):
            if record["marker"] in ("rehearsal-A", "rehearsal-B"):
                observed_pids.add(record["pid"])
        for pid in observed_pids:
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                continue
            except PermissionError:
                raise RuntimeError("Cannot clean up a rehearsal process.")
            current = subprocess.run(["ps", "-p", str(pid), "-o", "comm="], capture_output=True, text=True)
            if current.returncode == 0 and current.stdout.strip() == str(install / "Contents/MacOS/GHCPSpendTray"):
                try:
                    os.kill(pid, 15)
                except ProcessLookupError:
                    pass
        if not cleaned:
            config["scenario"] = "cleanup"
            configuration.write_text(json.dumps(config))
            with log_path.open("ab", buffering=0) as log:
                cleanup = launch(install, log)
                try:
                    cleanup.wait(timeout=20)
                    if cleanup.returncode != 0 or not any(item["event"] == "cleaned" for item in events(root)):
                        raise RuntimeError("Could not clean up the failed rehearsal's synthetic Keychain/defaults.")
                finally:
                    terminate(cleanup)


def main():
    require_host()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--signing-identity", required=True, help="Usable Developer ID Application name or fingerprint.")
    parser.add_argument("--scenario", choices=SCENARIOS, action="append", help="Default: all scenarios.")
    args = parser.parse_args()
    if args.signing_identity == "-":
        parser.error("Developer ID signing is required to verify Keychain access across actual A/B code changes.")
    root = ROOT / "artifacts/macos-rehearsal" / uuid.uuid4().hex
    root.mkdir(parents=True)
    report = dict(root=str(root), signing="Developer ID",
                  notarized=False, publicRelease=False, scenarios=[])
    print(f"Evidence: {root}", flush=True)
    server = None
    env = dict(os.environ)
    for name in ("SPARKLE_PUBLIC_ED_KEY", "SPARKLE_PRIVATE_ED_KEY", "MACOS_UPDATE_FEED_URL", "GHCP_UPDATE_REHEARSAL"):
        env.pop(name, None)
    with tempfile.TemporaryDirectory(prefix="ghcp-rehearsal-keys-") as private:
        private = Path(private)
        try:
            lock = json.loads((ROOT / "packaging/macos/sparkle.json").read_text())
            sparkle = ROOT / "artifacts/sparkle" / lock["version"]
            base_apps = []
            for variant, value in zip(("A", "B"), VERSIONS):
                run("bash", "tools/macos/build.sh", value, "Development",
                    env=dict(env, GHCP_UPDATE_REHEARSAL=variant))
                destination = root / f"base-{variant}.app"
                run("ditto", ROOT / "artifacts/macos/GHCPSpendTray.app", destination)
                base_apps.append(destination)
            keys = root / "keys-tool"
            run("bash", "-c", 'source tools/macos/sdk.sh && xcrun swiftc -parse-as-library -swift-version 6 '
                '-warnings-as-errors tools/macos/rehearsal/Keys.swift -o "$1"', "rehearsal", keys)
            run(keys, private / "seed", private / "public")
            run(keys, private / "wrong-seed", private / "wrong-public")
            seed = (private / "seed").read_text()
            wrong_seed = (private / "wrong-seed").read_text()
            public_key = (private / "public").read_text()
            server_binary = root / "server"
            run("bash", "-c", 'source tools/macos/sdk.sh && xcrun swiftc -swift-version 6 -warnings-as-errors '
                'src/GHCPSpendTray.Mac/Models.swift tests/GHCPSpendTray.MacTests/CallbackWait.swift '
                'tests/GHCPSpendTray.MacTests/LoopbackAppcastServer.swift tools/macos/rehearsal/Server.swift -o "$1"',
                "rehearsal", server_binary)
            with (root / "server.log").open("wb") as server_log:
                server = subprocess.Popen([str(server_binary), str(root)], stdout=subprocess.PIPE, stderr=server_log)
                ready, _, _ = select.select([server.stdout], [], [], 15)
                if not ready:
                    raise RuntimeError("Native server did not signal readiness; see server.log.")
                base_url = server.stdout.readline().decode().strip().rstrip("/")
                if not base_url.startswith("http://127.0.0.1:"):
                    raise RuntimeError("The native server returned an invalid loopback endpoint.")
                for name in args.scenario or SCENARIOS:
                    report["scenarios"].append(scenario(name, root / name, base_apps, base_url, args.signing_identity,
                                                        public_key, seed, wrong_seed, sparkle / "bin/sign_update"))
            report["result"] = "passed"
        except Exception as error:
            report["result"] = "failed"
            report["failure"] = str(error)
            raise
        finally:
            if server is not None:
                terminate(server)
            try:
                run("bash", "tools/macos/build.sh", env=env)
                report["ordinaryBuildRestored"] = True
            except (OSError, subprocess.CalledProcessError) as error:
                report["result"] = "failed"
                report["restoreFailure"] = str(error)
                raise
            finally:
                (root / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: local rehearsal; evidence: {root / 'report.json'}", flush=True)


if __name__ == "__main__":
    main()
