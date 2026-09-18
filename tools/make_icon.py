# Draws the OpenXR Gaze Overlay logo from code (no image libraries needed) and writes:
#   <out_dir>\icon.ico         multi-size Windows icon (16..128 as 32-bit bitmaps, 256 as PNG)
#   <out_dir>\logo-256.png     the 256 px version
#   <out_dir>\logo-concepts.png  three concepts side by side, to choose from
# Usage: python make_icon.py <out_dir> [concept A|B|C]
#
# Every size is drawn on its own (with a heavier stroke at small sizes) rather than scaled down, so 16 px stays legible.
import math, struct, sys, zlib

def clamp(x, a=0.0, b=1.0): return a if x < a else b if x > b else x
def mix(a, b, t): return a + (b - a) * t
def smooth(a, b, x):
    t = clamp((x - a) / (b - a)); return t * t * (3 - 2 * t)
def hexcol(h):
    # Hex colours are sRGB; all blending below happens in linear light and is converted back on output.
    return tuple((int(h[i:i + 2], 16) / 255) ** 2.2 for i in (1, 3, 5))

BG_TOP, BG_BOTTOM = hexcol('#1C2432'), hexcol('#0D1015')
CYAN, BLUE, WHITE = hexcol('#38DCFF'), hexcol('#2F78FF'), hexcol('#D8F8FF')

def sd_round_box(x, y, half, r):
    qx, qy = abs(x) - half + r, abs(y) - half + r
    return math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r

def sd_tapered_capsule(px, py, bx, by, ra, rb):
    h = bx * bx + by * by
    qx = abs((px * by - py * bx) / h); qy = (px * bx + py * by) / h
    b = ra - rb; cx, cy = math.sqrt(max(h - b * b, 0.0)), b
    k = cx * qy - cy * qx; m = cx * qx + cy * qy; n = qx * qx + qy * qy
    if k < 0: return math.sqrt(h * n) - ra
    if k > cx: return math.sqrt(h * (n + 1 - 2 * qy)) - rb
    return m - ra

def over(dst, src, a):
    return tuple(mix(d, s, a) for d, s in zip(dst, src))

def shade(concept, x, y, size):
    """x, y in -1..1 (y down). Returns (r, g, b, a) straight alpha."""
    small = size <= 32
    aa = 2.2 / size                                   # roughly one pixel, in these units
    # Rounded dark tile.
    d_bg = sd_round_box(x, y, 0.96, 0.44)
    alpha = 1 - smooth(-aa, aa, d_bg)
    if alpha <= 0: return (0, 0, 0, 0)
    col = tuple(mix(t, b, (y + 1) / 2) for t, b in zip(BG_TOP, BG_BOTTOM))
    accent = tuple(mix(c, b, clamp((x - y) * 0.5 + 0.5)) for b, c in zip(CYAN, BLUE))   # blue lower-left -> cyan upper-right

    if concept == 'A':       # the overlay itself: ring with a teardrop tail, pupil dot in the middle
        cx, cy, R = 0.16, -0.14, 0.43
        stroke = 0.17 if small else 0.125
        px, py = x - cx, y - cy
        tipx, tipy = -0.80 - cx, 0.78 - cy
        d_ring = abs(math.hypot(px, py) - R) - stroke / 2
        d_disc = math.hypot(px, py) - (R + stroke / 2)
        d_tail = sd_tapered_capsule(px, py, tipx, tipy, R * 0.97, 0.03)
        tail_len = math.hypot(tipx, tipy)
        along = clamp(((px * tipx + py * tipy) / tail_len - R) / (tail_len - R))
        if not small:
            # Glow hugs the ring on both sides, and the tail only outside the ring: the inside stays clear, like the overlay.
            glow_ring = math.exp(-max(d_ring, 0) / 0.07)
            glow_tail = math.exp(-max(d_tail, 0) / 0.09) * smooth(-aa, aa, d_disc)
            col = over(col, accent, max(glow_ring, glow_tail) * 0.22)
        tail_a = (1 - smooth(-aa, aa, d_tail)) * smooth(-aa, aa, d_disc) * mix(0.85, 0.12, along ** 0.8)
        col = over(col, accent, tail_a)
        col = over(col, accent, 1 - smooth(-aa, aa, d_ring))
        col = over(col, WHITE, 1 - smooth(-aa, aa, math.hypot(px, py) - (0.15 if small else 0.12)))
    elif concept == 'B':     # calibration brackets around a ring
        R, stroke = 0.40, (0.16 if small else 0.11)
        d_ring = abs(math.hypot(x, y) - R) - stroke / 2
        col = over(col, accent, 1 - smooth(-aa, aa, d_ring))
        col = over(col, WHITE, 1 - smooth(-aa, aa, math.hypot(x, y) - 0.11))
        arm, off, w = 0.30, 0.70, (0.13 if small else 0.09)
        rounding = w * 0.3
        def bar(px, py, x0, x1, y0, y1):
            # Rounded rectangle spanning x0..x1, y0..y1.
            cx, cy, hx, hy = (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2 - rounding, (y1 - y0) / 2 - rounding
            dx, dy = abs(px - cx) - hx, abs(py - cy) - hy
            return math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0) - rounding
        for sx in (-1, 1):
            for sy in (-1, 1):
                qx, qy = (x * sx - off), (y * sy - off)       # corner-local: towards the centre is negative
                # Both arms run through the corner block itself, so the outer corner is solid rather than notched.
                d_h = bar(qx, qy, -arm, w / 2, -w / 2, w / 2)
                d_v = bar(qx, qy, -w / 2, w / 2, -arm, w / 2)
                col = over(col, accent, (1 - smooth(-aa, aa, min(d_h, d_v))) * 0.9)
    else:                    # an eye: almond outline with the ring as its iris
        R, stroke = 0.34, (0.15 if small else 0.10)
        # Almond = intersection of two big circles.
        off, big = 0.62, 1.02
        d_lens = max(math.hypot(x, y - off) - big, math.hypot(x, y + off) - big)
        d_outline = abs(d_lens) - stroke * 0.42
        col = over(col, WHITE, (1 - smooth(-aa, aa, d_outline)) * 0.9)
        d_ring = abs(math.hypot(x, y) - R) - stroke / 2
        col = over(col, accent, 1 - smooth(-aa, aa, d_ring))
        col = over(col, WHITE, 1 - smooth(-aa, aa, math.hypot(x, y) - 0.10))
    return (*col, alpha)

def render(concept, size, samples=4):
    px = []
    for j in range(size):
        row = []
        for i in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sj in range(samples):
                for si in range(samples):
                    x = ((i + (si + 0.5) / samples) / size) * 2 - 1
                    y = ((j + (sj + 0.5) / samples) / size) * 2 - 1
                    r, g, b, a = shade(concept, x, y, size)
                    acc[0] += r * a; acc[1] += g * a; acc[2] += b * a; acc[3] += a
            a = acc[3] / (samples * samples)
            if a > 0:
                rgb = [clamp(c / acc[3]) ** (1 / 2.2) for c in acc[:3]]     # linear -> sRGB
            else:
                rgb = [0, 0, 0]
            row.append((int(rgb[0] * 255 + 0.5), int(rgb[1] * 255 + 0.5), int(rgb[2] * 255 + 0.5), int(a * 255 + 0.5)))
        px.append(row)
    return px

def png_bytes(pixels):
    h, w = len(pixels), len(pixels[0])
    raw = b''.join(b'\x00' + bytes(c for p in row for c in p) for row in pixels)
    def chunk(tag, data): return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')

def dib_bytes(pixels):
    """32-bit bottom-up DIB + empty AND mask, the classic ICO image format."""
    h, w = len(pixels), len(pixels[0])
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = b''.join(bytes(c for (r, g, b, a) in row for c in (b, g, r, a)) for row in reversed(pixels))
    mask_row = b'\x00' * (((w + 31) // 32) * 4)
    return header + body + mask_row * h

def write_ico(path, images):
    entries, blobs, offset = [], [], 6 + 16 * len(images)
    for size, data in images:
        entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        blobs.append(data); offset += len(data)
    open(path, 'wb').write(struct.pack('<HHH', 0, 1, len(images)) + b''.join(entries) + b''.join(blobs))

out_dir = sys.argv[1]
concept = sys.argv[2] if len(sys.argv) > 2 else 'A'

images = []
for size in (16, 20, 24, 32, 40, 48, 64, 128, 256):
    pixels = render(concept, size)
    images.append((size, png_bytes(pixels) if size == 256 else dib_bytes(pixels)))
    if size == 256:
        open(out_dir + '\\logo-256.png', 'wb').write(png_bytes(pixels))
write_ico(out_dir + '\\icon.ico', images)

# Concept sheet: each concept at 256, 48, 32 and 16 px on a mid-grey strip, the way it will really be seen.
sheet_w, sheet_h = 3 * 400, 300
sheet = [[(58, 61, 68, 255)] * sheet_w for _ in range(sheet_h)]
def blit(pixels, ox, oy):
    for j, row in enumerate(pixels):
        for i, (r, g, b, a) in enumerate(row):
            br, bg, bb, _ = sheet[oy + j][ox + i]
            sheet[oy + j][ox + i] = ((r * a + br * (255 - a)) // 255, (g * a + bg * (255 - a)) // 255, (b * a + bb * (255 - a)) // 255, 255)
for n, c in enumerate('ABC'):
    blit(render(c, 256), n * 400 + 16, 22)
    blit(render(c, 48), n * 400 + 292, 22)
    blit(render(c, 32), n * 400 + 292, 90)
    blit(render(c, 16), n * 400 + 292, 142)
open(out_dir + '\\logo-concepts.png', 'wb').write(png_bytes(sheet))
print('ok')
