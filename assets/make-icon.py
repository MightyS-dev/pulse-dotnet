#!/usr/bin/env python3
"""Generates assets/icon.png (128x128): a white pulse line on a solid rounded square.
Pure standard library, no external assets. Run: python3 assets/make-icon.py"""
import math, struct, zlib, os

SIZE, SS = 128, 4                    # output size, supersampling factor
BG = (15, 76, 129)                   # deep blue
FG = (255, 255, 255)
RADIUS, MARGIN, STROKE = 26.0, 4.0, 6.0
# pulse polyline in a 128 space
PTS = [(22, 66), (46, 66), (56, 40), (70, 96), (80, 66), (106, 66)]

def in_round_rect(x, y):
    lo, hi = MARGIN, SIZE - MARGIN
    if x < lo or x > hi or y < lo or y > hi: return False
    cx = min(max(x, lo + RADIUS), hi - RADIUS); cy = min(max(y, lo + RADIUS), hi - RADIUS)
    return math.hypot(x - cx, y - cy) <= RADIUS

def seg_dist(px, py, a, b):
    ax, ay = a; bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))

def on_line(x, y):
    return min(seg_dist(x, y, PTS[i], PTS[i + 1]) for i in range(len(PTS) - 1)) <= STROKE / 2

rows = []
for j in range(SIZE):
    row = bytearray([0])
    for i in range(SIZE):
        bg = fg = 0
        for sj in range(SS):
            for si in range(SS):
                x, y = i + (si + .5) / SS, j + (sj + .5) / SS
                if in_round_rect(x, y):
                    bg += 1
                    if on_line(x, y): fg += 1
        n = SS * SS
        a = bg / n
        mix = fg / bg if bg else 0
        c = [round(BG[k] * (1 - mix) + FG[k] * mix) for k in range(3)]
        row += bytes(c + [round(255 * a)])
    rows.append(bytes(row))

def chunk(t, d):
    c = struct.pack(">I", len(d)) + t + d
    return c + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)

png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)) \
    + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon.png")
open(out, "wb").write(png)
print("wrote", out, len(png), "bytes")
