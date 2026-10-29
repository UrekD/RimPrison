#!/usr/bin/env python3
"""Draw the layout preview the builder writes to the game log (plans layout/showcase, or RIMPRISON_LAYOUT=1) as a PNG.
Usage: layout-png.py <game.log> <out.png> [pixels-per-cell]   (stdlib + numpy only)"""
import re, struct, sys, zlib
import numpy as np

FLOOR = {'s': (92, 74, 52), 'c': (128, 128, 128), 'p': (150, 150, 158), 'h': (225, 232, 238), 'r': (120, 60, 70),
         't': (170, 160, 140), 'w': (150, 112, 70), 'g': (118, 108, 92), 'o': (100, 100, 100), ' ': (60, 90, 50)}
GLYPH = {'#': (25, 25, 28), 'a': (230, 200, 40), 'i': (90, 200, 90), 'S': (220, 60, 60), 'L': (150, 30, 200), 'f': (60, 60, 60),
         'b': (70, 110, 220), 'H': (240, 240, 255), 't': (60, 60, 120), 'w': (80, 200, 230), 'o': (255, 0, 0), 'X': (255, 120, 0),
         'm': (255, 255, 255), 'l': (255, 255, 150), 'h': (255, 150, 90), 'c': (170, 120, 60), 'T': (130, 90, 40), 'S2': (200, 80, 80),
         'C': (200, 160, 60), 'B': (110, 70, 40), 'D': (160, 110, 60), 'd': (190, 150, 90), 'W': (90, 90, 100), 'P': (140, 140, 170),
         'p': (80, 150, 150), 'v': (60, 160, 60), 'x': (200, 100, 200)}

def main(log, out, scale=8):
    rows, zones, size = {}, {}, None
    for line in open(log, errors="replace"):
        i = line.find("[RimPrison:layout]")
        if i < 0: continue
        t = line[i + 19:].rstrip("\n")
        if t.startswith("size "):
            f = t.split(); size = (int(f[1]), int(f[2]), int(f[4]))
        elif t.startswith("zone "):
            f = t.split(); zones[f[1]] = (f[2], tuple(int(float(v) * 255) for v in f[3:6]))
        elif t.startswith("z="):
            z = int(t[2:5]); rows[z] = t[6:]
    if not size or not rows: sys.exit("no layout in log")
    W, H, margin = size
    img = np.zeros((H * scale, W * scale, 3), np.uint8)
    for z, row in rows.items():
        y0 = (H - 1 - z) * scale
        for x in range(W):
            g, zn, fl = row[3 * x], row[3 * x + 1], row[3 * x + 2]
            col = np.array(FLOOR.get(fl, FLOOR['o']), float)
            if zn in zones: col = col * 0.55 + np.array(zones[zn][1], float) * 0.45
            cell = np.empty((scale, scale, 3), float); cell[:] = col
            if g != ' ':
                gc = np.array(GLYPH.get(g, GLYPH['x']), float)
                if g in '#': cell[:] = gc
                elif g in 'aiSL': cell[:] = gc
                else:
                    m = max(1, scale // 5); cell[m:scale - m, m:scale - m] = gc
            cell[:, 0] *= 0.85; cell[0, :] *= 0.85
            img[y0:y0 + scale, x * scale:(x + 1) * scale] = cell.astype(np.uint8)
    # a line where the compound ends (the facility's own margin below it is outside)
    y = (H - margin) * scale
    if 0 < y < H * scale: img[y - 1:y, :] = (255, 0, 255)
    raw = b"".join(b"\x00" + img[r].tobytes() for r in range(img.shape[0]))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", img.shape[1], img.shape[0], 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")
    open(out, "wb").write(png)
    print(f"{out}: {W}x{H} cells, {len(zones)} zones")
    for k, (n, c) in sorted(zones.items()): print(" ", k, n, c)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 8)
