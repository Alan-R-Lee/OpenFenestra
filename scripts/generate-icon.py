"""Render our original geometric app mark to ICO using only the Python standard library."""
import pathlib
import struct
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[1]


def rounded(x, y, left, top, width, height, radius):
    if not (left <= x < left + width and top <= y < top + height):
        return False
    cx = min(max(x, left + radius), left + width - radius)
    cy = min(max(y, top + radius), top + height - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))


def png(size):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            sums = [0, 0, 0, 0]
            for sy in range(4):
                for sx in range(4):
                    px, py = (x + (sx + .5) / 4) * 256 / size, (y + (sy + .5) / 4) * 256 / size
                    color = (24, 62, 50, 255) if rounded(px, py, 0, 0, 256, 256, 64) else (0, 0, 0, 0)
                    for rect, fill in [((52, 52, 68, 68, 12), (218, 236, 204, 255)), ((52, 136, 68, 68, 12), (120, 185, 161, 255)), ((136, 52, 68, 152, 12), (218, 236, 204, 255))]:
                        if rounded(px, py, *rect):
                            color = fill
                    for i, value in enumerate(color):
                        sums[i] += value
            rows.extend(round(v / 16) for v in sums)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')


sizes = [16, 32, 48, 64, 128, 256]
images = [png(s) for s in sizes]
offset = 6 + 16 * len(images)
entries = bytearray()
for size, data in zip(sizes, images):
    entries.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
(ROOT / 'assets' / 'OpenFenestra.ico').write_bytes(struct.pack('<HHH', 0, 1, len(images)) + entries + b''.join(images))
