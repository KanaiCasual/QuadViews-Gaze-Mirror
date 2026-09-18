# Pure-Python port of the `ghost` branch of the gaze pixel shader, to eyeball a look offline before installing.
# Usage: python ghost_preview.py out.png
# Rows = parameter sets, columns = backgrounds (black, dark cockpit, bright terrain, sky) + one moving pose.
import math, struct, zlib, sys

PX_PER_H = 2258.0   # canvas pixels per "image height" unit: 8192 px mirror x 0.2756 OBS source scale (measured 2026-09-18)
CELL = 440

ZOOM = 2.0          # magnify the render so edges can be judged; 1.0 = exactly the OBS canvas
BASE = dict(radius=0.042, tail_opacity=0.7, color=(40, 170, 245))
SETS = [
    ("rejected: thin but solid + crisp", dict(BASE, thickness=0.0011, feather=0.0009, glow=0.0016, glow_strength=0.3, opacity=0.8, solidity=0.8)),
    ("rejected: soft but not thin",      dict(BASE, thickness=0.0020, feather=0.0020, glow=0.0030, glow_strength=0.5, opacity=0.55, solidity=0.35)),
    ("C: thin + translucent + skirt",    dict(BASE, thickness=0.0010, feather=0.0010, glow=0.0028, glow_strength=0.4, opacity=0.5, solidity=0.4)),
]

def saturate(x): return min(max(x, 0.0), 1.0)
def smoothstep(a, b, x):
    t = saturate((x - a) / (b - a)); return t * t * (3 - 2 * t)
def lerp(a, b, t): return a + (b - a) * t
def srgb_to_lin(c): return (c / 255.0) ** 2.2

def sd_tapered_capsule(px, py, bx, by, ra, rb):
    h = bx * bx + by * by
    qx = abs((px * by - py * bx) / h); qy = (px * bx + py * by) / h
    b = ra - rb; cx, cy = math.sqrt(max(h - b * b, 0.0)), b
    k = cx * qy - cy * qx; m = cx * qx + cy * qy; n = qx * qx + qy * qy
    if k < 0: return math.sqrt(h * n) - ra
    if k > cx: return math.sqrt(h * (n + 1 - 2 * qy)) - rb
    return m - ra

def ghost(p, px, py, tipx, tipy):
    R, TH, FE, GL = p["radius"], p["thickness"], p["feather"], max(p["glow"], 1e-4)
    tip_r = R * 0.03
    tail_len = math.hypot(tipx, tipy)
    dirx, diry = (tipx / tail_len, tipy / tail_len) if tail_len > 1e-5 else (1.0, 0.0)
    a = px * dirx + py * diry; b = -px * diry + py * dirx
    deform = saturate(tail_len / (2.5 * R)); squash = 1 - 0.12 * deform
    qx = a / (1 + 0.35 * deform) if a > 0 else a / (1 - 0.08 * deform); qy = b / squash
    d_head = math.hypot(qx, qy) - R
    d_drop, along, stretch = d_head, 0.0, 0.0
    head_r = R * squash
    if tail_len > head_r - tip_r + 0.0005:
        d_tail = sd_tapered_capsule(px, py, tipx, tipy, head_r, tip_r)
        k = R * 0.35; h = saturate(0.5 + 0.5 * (d_tail - d_head) / k)
        d_drop = lerp(d_tail, d_head, h) - k * h * (1 - h)
        along = saturate((a - R) / max(tail_len - R, 1e-4)); stretch = saturate((tail_len - R) / R)
    facing = -a / max(math.hypot(px, py), 1e-5)
    dissolve = saturate((tail_len - 0.5 * R) / (1.5 * R))
    stroke = lerp(1.0, smoothstep(-0.8, 0.45, facing), dissolve)
    half = TH * 0.5
    soft = max(min(FE, TH) * 0.5, 0.00002)   # fade straddles the edge: softness never changes the apparent width
    ring = (1 - smoothstep(half - soft, half + soft, abs(d_head))) * stroke
    ring_glow = math.exp(-abs(d_head) / GL) * stroke
    outside_head = smoothstep(-half, half, d_head)
    behind = smoothstep(-0.35, 0.45, -facing)
    tail_fade = (1 - along) ** 0.8 * stretch * outside_head * behind
    tail_soft = max(GL, 0.006)
    tail_body = (1 - smoothstep(-tail_soft, tail_soft * 0.5, d_drop)) * tail_fade * p["tail_opacity"]
    tail_glow = math.exp(-max(d_drop, 0.0) / tail_soft) * tail_fade
    return max(max(ring, tail_body) * p["opacity"], max(ring_glow * p["glow_strength"], tail_glow * 0.35) * p["opacity"])

def background(kind, x, y):
    if kind == 0: return (0.0, 0.0, 0.0)
    if kind == 1:
        n = 0.5 + 0.5 * math.sin(x * 0.35) * math.sin(y * 0.27)
        return (0.010 + 0.03 * n, 0.012 + 0.03 * n, 0.016 + 0.035 * n)
    if kind == 2:
        n = 0.5 + 0.5 * math.sin(x * 0.21 + math.sin(y * 0.13) * 3.0) * math.sin(y * 0.17 + x * 0.05)
        return (0.30 + 0.30 * n, 0.22 + 0.22 * n, 0.13 + 0.15 * n)
    return (0.13, 0.30, 0.70)

COLS = [(2, (0.0, 0.0)), (3, (0.0, 0.0)), (1, (0.0, 0.0))]
W, H = CELL * len(COLS), CELL * len(SETS)
rows = []
for y in range(H):
    row = bytearray([0])
    params = SETS[min(y // CELL, len(SETS) - 1)][1]
    col = tuple(srgb_to_lin(c) for c in params["color"])
    for x in range(W):
        kind, tip = COLS[min(x // CELL, len(COLS) - 1)]
        cx = (x % CELL) - CELL * (0.5 if tip == (0.0, 0.0) else 0.62); cy = (y % CELL) - CELL * (0.5 if tip == (0.0, 0.0) else 0.38)
        alpha = ghost(params, cx / (PX_PER_H * ZOOM), cy / (PX_PER_H * ZOOM), tip[0], tip[1])
        bg = background(kind, x, y)
        for c in range(3):
            lin = col[c] * alpha * (1 - bg[c]) + bg[c] * (1 - alpha * params["solidity"])
            row.append(int(saturate(lin) ** (1 / 2.2) * 255))
    rows.append(bytes(row))

def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)) + \
      chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")
open(sys.argv[1], "wb").write(png)
print("ok", W, H)
