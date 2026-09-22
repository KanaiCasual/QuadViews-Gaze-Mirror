# Draws the installer's dark pages. Windows Installer cannot be themed: its text, tick boxes and buttons are native
# controls in system colours. So every page is one full-size picture with the text drawn INTO it, and only the tick
# boxes (with empty labels), the buttons and the progress bar are native controls placed on top (see Installer\GazeUI.wxs).
#
# Pages are 370 x 270 dialog units. They are drawn at 2.667 px per unit (987 x 720) so they still look sharp when
# Windows scales them for a high-DPI screen, and saved as 256-colour bitmaps to keep the MSI small.
#
# Usage: python tools\make_installer_pages.py <version>      (Build-Installer.ps1 runs this; needs Pillow)
import os, sys
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'Installer', 'Assets', 'pages')
FONTS = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts')
S = 987 / 370          # pixels per dialog unit
W, H = 987, 720

BG, STRIP, LINE = (15, 17, 21), (11, 12, 14), (38, 43, 51)
TEXT, BODY, MUTED, ACCENT, GREEN = (232, 234, 238), (201, 206, 214), (139, 147, 161), (40, 170, 245), (63, 185, 80)
CARD, CARD_LINE = (23, 26, 32), (38, 43, 51)

def font(size, bold=False):
    return ImageFont.truetype(os.path.join(FONTS, 'segoeuib.ttf' if bold else 'segoeui.ttf'), size)

F_TITLE, F_BODY, F_SMALL, F_BRAND, F_LABEL = font(40, True), font(25), font(22), font(34, True), font(25)

def px(v): return int(round(v * S))

def wrap(draw, text, f, width):
    words, lines, line = text.split(' '), [], ''
    for word in words:
        trial = (line + ' ' + word).strip()
        if draw.textlength(trial, font=f) <= width or not line:
            line = trial
        else:
            lines.append(line); line = word
    if line: lines.append(line)
    return lines

def paragraphs(draw, x, y, texts, f=F_BODY, colour=BODY, width=px(212), gap=10, leading=None):
    """Draws wrapped paragraphs downwards; returns the y below the last one (in px)."""
    leading = leading or int(f.size * 1.32)
    for text in texts:
        for line in wrap(draw, text, f, width):
            draw.text((x, y), line, font=f, fill=colour)
            y += leading
        y += gap
    return y

def page(version, title):
    img = Image.new('RGB', (W, H), BG)
    d = ImageDraw.Draw(img)
    # The left strip: logo, name, version, what is inside.
    d.rectangle([0, 0, px(125), H], fill=STRIP)
    d.line([px(125), 0, px(125), H], fill=LINE, width=2)
    logo = Image.open(os.path.join(ROOT, 'GazeOverlayApp', 'Assets', 'logo-256.png')).convert('RGBA').resize((128, 128), Image.LANCZOS)
    img.paste(logo, (px(20), px(18)), logo)
    d.text((px(20), px(72)), 'QuadViews', font=F_BRAND, fill=TEXT)
    d.text((px(20), px(86)), 'Gaze Mirror', font=F_BRAND, fill=TEXT)
    d.text((px(20), px(104)), 'Version ' + version, font=F_SMALL, fill=MUTED)
    y = px(128)
    for item in ('Gaze mirror layer', 'SteamVR helper', 'OBS plugin', 'Mirror window and app'):
        d.ellipse([px(20), y + 9, px(20) + 10, y + 19], fill=ACCENT)
        d.text((px(20) + 22, y), item, font=F_SMALL, fill=MUTED)
        y += 34
    # The page's title.
    d.text((px(138), px(12)), title, font=F_TITLE, fill=TEXT)
    return img, d

def label(d, x, y, text, colour=BODY):
    """A tick box's label: the native box sits at (x, y) dialog units, 10 units square; the words go to its right."""
    d.text((px(x + 13), px(y) - 4), text, font=F_LABEL, fill=colour)

def save(img, name):
    os.makedirs(OUT, exist_ok=True)
    img.convert('P', palette=Image.ADAPTIVE, colors=256).save(os.path.join(OUT, name + '.bmp'))

def main(version):
    X = px(138)

    img, d = page(version, 'Install')
    paragraphs(d, X, px(36), [
        'A mirror of your VR view for OBS and window capture, with a ring where you look. The ring is never visible in the headset.',
        'Everything installed is this project’s own code (MIT licence): the gaze mirror layer, the SteamVR helper, the “Gaze Mirror” OBS plugin, the mirror window and the settings app.',
        'Quad-Views-Foveated is optional and unmodified; its official installer is offered on the last page.',
        'Upgrading from 1.x removes the modified layers and the old OBS plugin. Not code-signed: Windows may warn.',
    ], f=F_SMALL, gap=6)
    label(d, 138, 196, 'I accept the MIT licence (Print shows the full text)')
    label(d, 138, 214, 'Put a shortcut to the settings app on the desktop')
    save(img, 'install')

    img, d = page(version, 'Repair or uninstall')
    paragraphs(d, X, px(36), ['QuadViews Gaze Mirror ' + version + ' is installed on this PC.'], colour=MUTED)
    paragraphs(d, px(226), px(60), ['Puts the files back and registers the gaze mirror layer again. Your settings are not touched.'], f=F_SMALL, width=px(130), gap=0)
    paragraphs(d, px(226), px(116), ['Removes the layer, the SteamVR helper, the OBS plugin, the mirror window and the app. Your settings stay in your profile. Quad-Views-Foveated is separate and stays.'], f=F_SMALL, width=px(130), gap=0)
    paragraphs(d, X, px(196), ['Updating? Run the newer installer; it replaces this version by itself. Close the game and OBS first.'], f=F_SMALL, colour=MUTED, gap=0)
    save(img, 'maintenance')

    for name, title, text in (('verify-repair', 'Ready to repair', 'Press Repair to put every file and registration back. Nothing else changes.'),
                              ('verify-remove', 'Ready to uninstall', 'Press Uninstall to remove QuadViews Gaze Mirror from this PC. Your settings files stay in your profile.')):
        img, d = page(version, title)
        paragraphs(d, X, px(36), [text])
        save(img, name)

    img, d = page(version, 'Please wait')
    paragraphs(d, X, px(36), ['Windows Installer is doing the work. This takes a moment.'], colour=MUTED)
    save(img, 'progress')

    done = ['QuadViews Gaze Mirror is installed.',
            'Open it from the Start menu to set up the ring. In OBS, add a \u201cGaze Mirror\u201d source to your scene. If the Status page reports the layer order, press Fix order.']
    img, d = page(version, 'Done')
    paragraphs(d, X, px(36), done, gap=8)
    save(img, 'exit-install')

    img, d = page(version, 'Done')
    y = paragraphs(d, X, px(36), done, gap=8)
    # The offer: a card with the tick box inside it (the native box goes at 146, 152).
    top = px(112)
    d.rounded_rectangle([X, top, px(356), px(184)], radius=10, fill=CARD, outline=CARD_LINE, width=2)
    d.text((px(146), top + 12), 'Quad-Views-Foveated is not installed', font=font(25, True), fill=TEXT)
    paragraphs(d, px(146), top + 48, ['Optional: sharper centre, faster edges, on eye-tracked headsets. Official 1.1.3 installer, bundled.'], f=F_SMALL, colour=MUTED, width=px(200), gap=0)
    label(d, 146, 164, 'Install Quad-Views-Foveated 1.1.3 now')
    save(img, 'exit-install-offer')

    img, d = page(version, 'Done')
    paragraphs(d, X, px(36), ['QuadViews Gaze Mirror is repaired: its files and registration are back. Your settings were not touched.'])
    save(img, 'exit-repair')

    img, d = page(version, 'Done')
    paragraphs(d, X, px(36), ['QuadViews Gaze Mirror is uninstalled. Your settings files stayed in your profile.',
                              'SteamVR still lists the helper under Settings > Startup / Shutdown; untick it there once.'], gap=8)
    save(img, 'exit-remove')

    img, d = page(version, 'Setup was cancelled')
    paragraphs(d, X, px(36), ['Nothing was changed. Run the installer again whenever you like.'])
    save(img, 'cancel')

    img, d = page(version, 'Setup did not finish')
    paragraphs(d, X, px(36), ['Nothing was changed. Close the game and OBS and run the installer again. If it keeps failing, open an issue on GitHub with what the message said.'])
    save(img, 'fatal')
    print('ok', OUT)

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '0.0.0')
