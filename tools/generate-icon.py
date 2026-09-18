"""Generate the original GHSpend rising-bars/coin icon using only the standard library."""
import pathlib
import struct

root = pathlib.Path(__file__).resolve().parents[1]
images = []
sizes = (16, 32, 48, 64)
for size in sizes:
    pixels = bytearray()
    for y in reversed(range(size)):
        for x in range(size):
            u, v = (x + .5) / size, (y + .5) / size
            inside = (u - .5) ** 2 + (v - .5) ** 2 < .46 ** 2
            bar = any(lo < u < hi and top < v < .74 for lo, hi, top in
                      ((.23, .36, .53), (.435, .565, .38), (.64, .77, .23)))
            r, g, b, a = (238, 255, 244, 255) if inside and bar else (
                (12, 121, 89, 255) if inside else (0, 0, 0, 0))
            pixels += bytes((b, g, r, a))
    mask = bytes(((size + 31) // 32) * 4 * size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    images.append(header + pixels + mask)
offset = 6 + 16 * len(images)
output = bytearray(struct.pack("<HHH", 0, 1, len(images)))
for size, data in zip(sizes, images):
    output += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
output += b"".join(images)
destination = root / "src" / "GHSpend.App" / "Assets" / "ghspend.ico"
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_bytes(output)
