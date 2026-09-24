"""Generate GHCPSpendTray's transparent logo, opaque badge PNG, and scalable SVG."""
import pathlib
import struct
import zlib

SIZE = 512
SAMPLES = 4
GREEN = (12, 121, 89)
IVORY = (238, 255, 244)
CIRCLE = (256, 256, 236)
BARS = ((118, 272, 66, 108), (223, 195, 66, 185), (328, 118, 66, 262))
RADIUS = 9
ASSETS = pathlib.Path(__file__).resolve().parents[1] / "src" / "GHCPSpendTray.App" / "Assets"


def in_bar(x, y, bar):
    left, top, width, height = bar
    if not (left <= x <= left + width and top <= y <= top + height):
        return False
    cx = min(max(x, left + RADIUS), left + width - RADIUS)
    cy = min(max(y, top + RADIUS), top + height - RADIUS)
    return (x - cx) ** 2 + (y - cy) ** 2 <= RADIUS ** 2


def chunk(kind, payload):
    return (struct.pack(">I", len(payload)) + kind + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF))


def write_png(path, opaque=False, size=SIZE):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            covered = ink = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    px = (x + (sx + .5) / SAMPLES) * SIZE / size
                    py = (y + (sy + .5) / SAMPLES) * SIZE / size
                    if opaque or (px - CIRCLE[0]) ** 2 + (py - CIRCLE[1]) ** 2 <= CIRCLE[2] ** 2:
                        covered += 1
                        ink += any(in_bar(px, py, bar) for bar in BARS)
            if covered:
                rows.extend(round((GREEN[c] * (covered - ink) + IVORY[c] * ink) / covered)
                            for c in range(3))
                rows.append(round(255 * covered / (SAMPLES * SAMPLES)))
            else:
                rows.extend((0, 0, 0, 0))
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
                     + chunk(b"sRGB", b"\0") + chunk(b"IDAT", zlib.compress(rows, 9))
                     + chunk(b"IEND", b""))


def write_svg(path):
    bars = "\n".join(
        f'    <rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{RADIUS}"/>'
        for x, y, w, h in BARS)
    path.write_text(
        '<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">\n'
        '  <title>GHCPSpendTray</title>\n'
        '  <desc>Three ivory consumption bars rising within an emerald coin.</desc>\n'
        '  <circle cx="256" cy="256" r="236" fill="#0c7959"/>\n'
        '  <g fill="#eefff4">\n' + bars + '\n  </g>\n</svg>\n', encoding="utf-8")


if __name__ == "__main__":
    ASSETS.mkdir(parents=True, exist_ok=True)
    write_png(ASSETS / "ghcpspendtray-logo.png")
    write_png(ASSETS / "ghcpspendtray-badge.png", opaque=True)
    write_svg(ASSETS / "ghcpspendtray-logo.svg")
    for name, size in (("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50)):
        write_png(ASSETS / f"{name}.png", size=size)
    print(f"Created {SIZE}x{SIZE} transparent logo, opaque badge, and SVG in {ASSETS}")
