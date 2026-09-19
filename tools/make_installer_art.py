# Draws the two pictures the installer's dialogs use, from the app logo (GazeOverlayApp\Assets\logo-256.png):
#   Installer\Assets\dialog.bmp  493x312  first/last page: dark strip with the logo on the left, white under the text
#   Installer\Assets\banner.bmp  493x58   top of the other pages: white, logo on the right
# Windows Installer draws its text in black on top of these, so the area under the text has to stay light.
# Usage: python tools\make_installer_art.py      (pure Python; the results are committed, so builds do not need this)
import os, struct, zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def read_png(path):
    """Only what make_icon.py writes: 8-bit RGBA, no interlace, filter 0 on every row."""
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    pos, idat, width, height = 8, b'', 0, 0
    while pos < len(data):
        length, tag = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if tag == b'IHDR':
            width, height, depth, colour = struct.unpack('>IIBB', body[:10])
            assert (depth, colour) == (8, 6)
        elif tag == b'IDAT':
            idat += body
        pos += 12 + length
    raw, stride, rows = zlib.decompress(idat), width * 4 + 1, []
    for y in range(height):
        line = raw[y * stride:(y + 1) * stride]
        assert line[0] == 0
        rows.append([tuple(line[1 + x * 4:5 + x * 4]) for x in range(width)])
    return rows

def shrink(pixels, size):
    """Box filter on premultiplied colour, so the soft edges stay clean."""
    source, out = len(pixels), []
    for j in range(size):
        row = []
        y0, y1 = j * source // size, max((j + 1) * source // size, j * source // size + 1)
        for i in range(size):
            x0, x1 = i * source // size, max((i + 1) * source // size, i * source // size + 1)
            r = g = b = a = 0
            for y in range(y0, y1):
                for x in range(x0, x1):
                    pr, pg, pb, pa = pixels[y][x]
                    r += pr * pa; g += pg * pa; b += pb * pa; a += pa
            count = (y1 - y0) * (x1 - x0)
            row.append((r // a, g // a, b // a, a // count) if a else (0, 0, 0, 0))
        out.append(row)
    return out

def blit(canvas, pixels, ox, oy):
    for j, row in enumerate(pixels):
        for i, (r, g, b, a) in enumerate(row):
            br, bg, bb = canvas[oy + j][ox + i]
            canvas[oy + j][ox + i] = ((r * a + br * (255 - a)) // 255, (g * a + bg * (255 - a)) // 255, (b * a + bb * (255 - a)) // 255)

def write_bmp(path, canvas):
    height, width = len(canvas), len(canvas[0])
    padding = (4 - width * 3 % 4) % 4
    body = b''.join(bytes(c for (r, g, b) in row for c in (b, g, r)) + b'\x00' * padding for row in reversed(canvas))
    header = struct.pack('<2sIHHI', b'BM', 54 + len(body), 0, 0, 54) + struct.pack('<IiiHHIIiiII', 40, width, height, 1, 24, 0, len(body), 2835, 2835, 0, 0)
    open(path, 'wb').write(header + body)

logo = read_png(os.path.join(ROOT, 'GazeOverlayApp', 'Assets', 'logo-256.png'))
out_dir = os.path.join(ROOT, 'Installer', 'Assets')
os.makedirs(out_dir, exist_ok=True)

# First/last page. The installer's text starts at x = 135 of 370 dialog units, i.e. about 180 of 493 pixels.
STRIP = 164
dialog = []
for y in range(312):
    shade = y / 311
    dark = (int(31 - 8 * shade), int(32 - 8 * shade), int(36 - 9 * shade))     # the app's window colour, fading to its panel colour
    dialog.append([dark if x < STRIP else (255, 255, 255) for x in range(493)])
for y in range(312):
    dialog[y][STRIP - 1] = (77, 178, 255)                                       # the app's accent as a hairline
blit(dialog, shrink(logo, 116), (STRIP - 116) // 2, 40)
write_bmp(os.path.join(out_dir, 'dialog.bmp'), dialog)

banner = [[(255, 255, 255)] * 493 for _ in range(58)]
blit(banner, shrink(logo, 46), 493 - 46 - 10, 6)
write_bmp(os.path.join(out_dir, 'banner.bmp'), banner)
print('ok')
