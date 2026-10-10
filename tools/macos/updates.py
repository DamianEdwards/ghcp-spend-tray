#!/usr/bin/env python3
"""Configure Sparkle and generate authenticated, platform-scoped update feeds."""

import argparse
import base64
import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET

from release import pages, version

SPARKLE = "http://www.andymatuschak.org/xml-namespaces/sparkle"
ET.register_namespace("sparkle", SPARKLE)
DEFAULT_FEED = "https://damianedwards.github.io/ghcp-spend-tray/appcast.xml"
FEED_NAME = "appcast.xml"


def public_key(value):
    try:
        decoded = base64.b64decode(value, validate=True)
    except (ValueError, TypeError) as error:
        raise ValueError("Configure SPARKLE_PUBLIC_ED_KEY as a base64 Ed25519 public key.") from error
    if len(decoded) != 32:
        raise ValueError("SPARKLE_PUBLIC_ED_KEY must contain a 32-byte Ed25519 public key.")
    return value


def feed_url(value):
    parsed = urlsplit(value)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or
            parsed.query or parsed.fragment or not parsed.path.endswith("/appcast.xml")):
        raise ValueError("MACOS_UPDATE_FEED_URL must be an HTTPS appcast.xml URL without credentials, query or fragment.")
    return value


def configure(path, channel):
    if channel not in ("Development", "Preview", "Stable"):
        raise ValueError("Invalid macOS release channel.")
    plist = plistlib.loads(path.read_bytes())
    plist["SUFeedURL"] = feed_url(os.environ.get("MACOS_UPDATE_FEED_URL") or plist["SUFeedURL"])
    key = os.environ.get("SPARKLE_PUBLIC_ED_KEY", "")
    if channel != "Development" or key:
        plist["SUPublicEDKey"] = public_key(key)
    else:
        plist.pop("SUPublicEDKey", None)
    for name in ("SUEnableAutomaticChecks",):
        plist.pop(name, None)  # Sparkle's permission flow owns the user's preference.
    plist.update(SUAutomaticallyUpdate=False, SUEnableSystemProfiling=False,
                 SUVerifyUpdateBeforeExtraction=True, SURequireSignedFeed=True)
    path.write_bytes(plistlib.dumps(plist, sort_keys=False))


def inspect_bundle(path):
    plist = plistlib.loads((path / "Contents/Info.plist").read_bytes())
    framework = path / "Contents/Frameworks/Sparkle.framework"
    channel = plist.get("GHCPReleaseChannel")
    if channel not in ("Development", "Stable", "Preview"):
        raise ValueError("Invalid macOS release channel.")
    if channel != "Development":
        if "GHCPUpdateRehearsal" in plist:
            raise ValueError("A rehearsal bundle cannot pass production package validation.")
        public_key(plist.get("SUPublicEDKey", ""))
        if not framework.is_dir():
            raise ValueError("Production updates require the embedded Sparkle framework.")
    feed_url(plist.get("SUFeedURL", ""))
    if (plist.get("SURequireSignedFeed") is not True or plist.get("SUVerifyUpdateBeforeExtraction") is not True or
            plist.get("SUEnableSystemProfiling") is not False or plist.get("SUAutomaticallyUpdate") is not False or
            "SUEnableAutomaticChecks" in plist):
        raise ValueError("Sparkle must require signed feeds/archives, disable profiling and preserve update consent.")
    if framework.is_dir():
        for relative in ("Sparkle", "Autoupdate", "Updater.app/Contents/MacOS/Updater",
                         "XPCServices/Downloader.xpc/Contents/MacOS/Downloader",
                         "XPCServices/Installer.xpc/Contents/MacOS/Installer"):
            binary = framework / relative
            if not binary.is_file() or not os.access(binary, os.X_OK):
                raise ValueError(f"Missing or non-executable Sparkle component: {relative}")


def tools():
    root = Path(os.environ["SPARKLE_ROOT"])
    return root / "bin"


def validate_keys():
    public_key(os.environ.get("SPARKLE_PUBLIC_ED_KEY", ""))
    if not os.environ.get("SPARKLE_PRIVATE_ED_KEY", "").strip():
        raise ValueError("Configure SPARKLE_PRIVATE_ED_KEY in the production environment.")
    binary = Path("artifacts/sparkle/update-crypto")
    binary.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["xcrun", "swiftc", "-parse-as-library", "-swift-version", "6", "-warnings-as-errors",
                    "tools/macos/update-crypto.swift", "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True)


def signing_command(tool, *arguments):
    subprocess.run([str(tools() / tool), "--ed-key-file", "-", *map(str, arguments)],
                   input=os.environ["SPARKLE_PRIVATE_ED_KEY"].strip() + "\n", text=True, check=True)


def repository():
    slug = os.environ["GITHUB_REPOSITORY"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", slug):
        raise ValueError("Invalid GitHub repository.")
    return slug


def download_url(slug, value):
    return f"https://github.com/{slug}/releases/download/macos-v{value}/GHCPSpendTray-macOS-{value}.dmg"


def validate_item(data, slug, value, size, preview=False):
    root = ET.fromstring(data)
    items = root.findall("./channel/item")
    if root.tag != "rss" or len(items) != 1:
        raise ValueError("A release appcast must contain exactly one update.")
    item = items[0]
    enclosure = item.find("enclosure")
    if (item.findtext(f"{{{SPARKLE}}}version") != value or enclosure is None or
            enclosure.get("url") != download_url(slug, value) or enclosure.get("length") != str(size) or
            item.findtext(f"{{{SPARKLE}}}minimumSystemVersion") not in ("15.0", "15.0.0") or
            item.findtext(f"{{{SPARKLE}}}hardwareRequirements") != "arm64" or
            item.findtext(f"{{{SPARKLE}}}channel") != ("preview" if preview else None)):
        raise ValueError("The appcast must match the release version, DMG, channel and platform requirements.")
    try:
        signature = base64.b64decode(enclosure.get(f"{{{SPARKLE}}}edSignature", ""), validate=True)
    except ValueError as error:
        raise ValueError("Invalid update archive signature.") from error
    if len(signature) != 64:
        raise ValueError("The update archive is missing its Ed25519 signature.")
    return item


def generate(dmg, value):
    version(value)
    validate_keys()
    slug = repository()
    preview = os.environ["PRERELEASE"] == "true"
    with tempfile.TemporaryDirectory(prefix="ghcp-appcast-") as directory:
        root = Path(directory)
        shutil.copyfile(dmg, root / dmg.name)
        release_url = f"https://github.com/{slug}/releases/tag/macos-v{value}"
        (root / f"{dmg.stem}.md").write_text(f"See the [GitHub release]({release_url}) for changes.\n")
        arguments = ["--download-url-prefix", download_url(slug, value).rsplit("/", 1)[0] + "/",
                     "--link", release_url, "--full-release-notes-url", release_url,
                     "--embed-release-notes", "--maximum-deltas", "0"]
        if preview:
            arguments += ["--channel", "preview"]
        signing_command("generate_appcast", *arguments, root)
        feed = root / FEED_NAME
        signing_command("sign_update", "--verify", feed)
        item = validate_item(feed.read_bytes(), slug, value, dmg.stat().st_size, preview)
        subprocess.run(["artifacts/sparkle/update-crypto", str(dmg),
                        item.find("enclosure").get(f"{{{SPARKLE}}}edSignature")], check=True)
        shutil.copyfile(feed, dmg.parent / FEED_NAME)


def asset_bytes(slug, asset):
    if type(asset.get("id")) is not int or asset["id"] <= 0:
        raise ValueError("Invalid release asset ID.")
    return subprocess.check_output(["gh", "api", f"repos/{slug}/releases/assets/{asset['id']}",
                                    "-H", "Accept: application/octet-stream"])


def stable_releases(releases):
    result = []
    for release in releases:
        match = re.fullmatch(r"macos-v(\d+\.\d+\.\d+)", release.get("tag_name", ""))
        if not match:
            continue
        if type(release.get("draft")) is not bool or type(release.get("prerelease")) is not bool:
            raise ValueError("Invalid GitHub release draft/preview flags.")
        if not release["draft"] and not release["prerelease"]:
            version(match[1])
            result.append((match[1], release))
    return sorted(result, key=lambda pair: version(pair[0]), reverse=True)


def assemble(releases, slug, load_asset, verify_feed):
    root = ET.Element("rss", version="2.0")
    channel = ET.SubElement(root, "channel")
    ET.SubElement(channel, "title").text = "GHCPSpendTray for macOS"
    ET.SubElement(channel, "link").text = f"https://github.com/{slug}/releases"
    ET.SubElement(channel, "description").text = "Stable Apple-silicon macOS updates"
    count = 0
    for value, release in stable_releases(releases):
        assets = release["assets"]
        feeds = [asset for asset in assets if asset["name"] == FEED_NAME]
        if not feeds:
            if count:
                continue  # Releases predating Sparkle remain manual downloads.
            raise ValueError("The latest stable macOS release has no signed appcast; feed publication is blocked.")
        metadata = [asset for asset in assets if asset["name"] == "release-macos.json"]
        dmgs = [asset for asset in assets if asset["name"] == f"GHCPSpendTray-macOS-{value}.dmg"]
        if len(feeds) != 1 or len(metadata) != 1 or len(dmgs) != 1:
            raise ValueError("The release must have unique appcast, metadata and DMG assets.")
        provenance = json.loads(load_asset(metadata[0]))
        if (provenance.get("platform") != "macos" or provenance.get("version") != value or
                provenance.get("signed") is not True or provenance.get("notarized") is not True or
                provenance.get("architectures") != ["arm64"] or provenance.get("minimumOS") != "15.0" or
                provenance.get("bundleIdentifier") != "com.damianedwards.GHCPSpendTray"):
            raise ValueError("The feed may only advertise signed, notarized Apple-silicon macOS releases.")
        data = load_asset(feeds[0])
        verify_feed(data)
        item = validate_item(data, slug, value, dmgs[0]["size"])
        if item.find("pubDate") is None:
            date = datetime.fromisoformat(release["published_at"].replace("Z", "+00:00"))
            ET.SubElement(item, "pubDate").text = date.astimezone(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S +0000")
        channel.append(copy.deepcopy(item))
        count += 1
    if not count:
        raise ValueError("No published stable macOS updates are available.")
    ET.indent(root)
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def build_feed(destination):
    validate_keys()
    slug = repository()
    configured = feed_url(os.environ.get("MACOS_UPDATE_FEED_URL") or DEFAULT_FEED)
    site = json.loads(subprocess.check_output(["gh", "api", f"repos/{slug}/pages"], text=True))
    html_url = site.get("html_url")
    if (site.get("build_type") != "workflow" or not isinstance(html_url, str) or
            configured != html_url.rstrip("/") + "/" + FEED_NAME):
        raise ValueError("Enable GitHub Pages with GitHub Actions and configure MACOS_UPDATE_FEED_URL to its appcast.xml URL.")
    with tempfile.TemporaryDirectory(prefix="ghcp-feed-") as directory:
        verified = Path(directory, FEED_NAME)

        def verify_feed(data):
            verified.write_bytes(data)
            signing_command("sign_update", "--verify", verified)

        data = assemble(list(pages(f"repos/{slug}/releases")), slug, lambda asset: asset_bytes(slug, asset), verify_feed)
    destination.mkdir(parents=True, exist_ok=True)
    feed = destination / FEED_NAME
    feed.write_bytes(data)
    signing_command("sign_update", feed)
    signing_command("sign_update", "--verify", feed)
    (destination / "index.html").write_text(
        '<!doctype html><meta charset="utf-8"><title>GHCPSpendTray updates</title>'
        f'<a href="https://github.com/{slug}/releases">Download GHCPSpendTray</a>\n')


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["configure", "inspect", "generate", "feed", "keys"])
    parser.add_argument("path", type=Path, nargs="?")
    parser.add_argument("value", nargs="?")
    args = parser.parse_args()
    if args.operation != "keys" and args.path is None:
        parser.error("Supply a path.")
    try:
        if args.operation == "configure":
            configure(args.path, args.value)
        elif args.operation == "inspect":
            inspect_bundle(args.path)
        elif args.operation == "keys":
            validate_keys()
        elif args.operation == "generate":
            generate(args.path, args.value)
        else:
            build_feed(args.path)
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError, ET.ParseError) as error:
        parser.exit(1, f"macOS update publication failed: {error}\n")
