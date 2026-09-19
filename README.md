# QuadViews Gaze Mirror

Foveated rendering (quad views) plus an OBS mirror that shows your viewers where you look: a ring - in the style of Tobii
Ghost - drawn on your **OBS mirror only**, never in the headset. One installer replaces both Quad-Views-Foveated and
the OpenXR OBS Mirror layer, and its settings app also covers the quad views settings (focus size, resolutions, presets)
and the OpenXR API layer list (on/off, order, order check).

*Called "OpenXR Gaze Overlay" up to version 0.9.0.*

> **Unofficial community build.** This project contains modified builds of
> [Quad-Views-Foveated](https://github.com/mbucchia/Quad-Views-Foveated) by Matthieu Bucchianeri and
> [OpenXR-Layer-OBSMirror](https://github.com/Jabbah/OpenXR-Layer-OBSMirror) by Jabbah. They did not build, test or
> endorse it. Please do not ask them for support with it.

**Status: released.** Get the latest installer (`QuadViews-Gaze-Mirror-<version>.msi`) from the repository's Releases
page; the release notes say what changed.

## How it works

Two OpenXR API layers cooperate through a small shared-memory block:

1. **Quad-Views-Foveated** already knows where your eyes point (that is how it places the sharp region). Our patch makes
   it also *publish* that: the gaze direction, and each eye's position and orientation.
2. **OpenXR-Layer-OBSMirror** copies the frames your headset receives into a texture that OBS reads. Our patch projects the
   gaze into the eye being mirrored and draws the ring onto that copy - so it exists in OBS and nowhere else. It also
   fixes a texture leak on swapchain destruction.

The ring deforms into a teardrop as your eyes move, its tail is anchored to the world (so looking around by turning your
head stretches it too), it is smoothed with a One-Euro filter, and it tightens when you hold your gaze on something.

A small **settings app** edits the ring's look and behaviour live, and has a calibration mode that shows a marker inside the headset so you can centre the ring on what you look at.

Its **Quad Views** tab edits Quad-Views-Foveated's own `settings.cfg` with the same sliders and value logic as TallyMouse's
QuadViews Companion, and shows how many pixels per frame the settings make the game render - the layer's own arithmetic,
using the headset resolution from its log. One deliberate difference: values are also written to the common part of the
file, because headset sections such as `[Pimax]` only apply when the OpenXR runtime's name contains that word (a Pimax
driven through SteamVR matches none of them, and silently gets the built-in 35 % focus size).

Its **Mirror** tab has a crop tool that replaces typing crop percentages into the OBS source: a box locked to a shape (16:9, 9:16, 1:1, ...) is
dragged and scaled on a picture of the whole mirror image, and the mirror layer then hands OBS only that box - so the
OBS source *is* the box, and every copy after the layer's compositing moves fewer pixels. The picture is made by the
running game only when the app asks for one. The box can also **follow your gaze up and down** - like a camera
operator, it glides just far enough to keep what you look at in frame, since a 16:9 box only covers about half the height
of the eye image.

The **mirror window** (`MirrorWindow.exe`, opened from the app's Mirror tab) is a second way out for the same picture: an
ordinary window that anything able to capture a window can use - OBS Window Capture, Discord, ... - with no OBS plugin
involved. It normally fills a monitor *behind* the game and never comes to the front; the layer signals it after each
finished frame, and while it is open the OBS plugin can be handed a blank picture so that a forgotten OBS source costs
nothing. Its output size (the monitor's, a preset such as 1920 x 1080, or any typed size) and its picture rate (at most
60 or 30 a second) are chosen in the app - it is the dearer of the two ways out, and both settings make it cheaper.

**Nothing polls.** The layers never check the settings file while a game runs: the file is read once at start, and the
settings app bumps a counter in shared memory when you change something, which the layer compares each frame (a memory
read, no system call). With the app closed, nothing is ever re-read. The app itself listens for file-change
notifications rather than checking on a timer.

Requirements: a headset with eye tracking that works with Quad-Views-Foveated, a D3D11 game that supports quad views
(e.g. DCS World), and OBS Studio.

## Repository layout

| Path | What |
|---|---|
| `Quad-Views-Foveated/`, `OpenXR-Layer-OBSMirror/` | Upstream sources as git submodules, pinned to the commits the patches apply to |
| `patches/` | **Everything we changed upstream**, as two patch files |
| `GazeOverlayApp/` | The settings app (WPF, .NET 10, dark theme). Never elevates, never installs anything |
| `MirrorWindow/` | The capturable mirror window (native Win32 + Direct3D 11, one source file) |
| `Installer/` | WiX 5 project for the MSI. Purely declarative - no custom code runs during setup |
| `tools/` | Patch scripts, the logo and installer-artwork generators, an offline ring preview, an MSI inspector, a clip reviewer |
| `gaze.cfg` | A sample of the layer's settings file |
| `PUBLISHING-PLAN.md` | Notes and decisions for going public |

## Building

Needs Visual Studio 2022 (C++ workload), the .NET 10 SDK and Python 3 (upstream's build scripts use it).

```powershell
git clone --recurse-submodules <this repo>
.\tools\Apply-Patches.ps1        # once, on a fresh clone
.\Build-Layers.ps1               # both OpenXR layers
.\Build-Installer.ps1            # settings app + dist\QuadViews-Gaze-Mirror-<version>.msi
```

`Build-Installer.ps1` needs the unmodified OBS plugin (`win-openxr.dll` and its data files) from an upstream
OpenXR-Layer-OBSMirror release; see `GazeOverlayApp\Collect-Payload.ps1` for where it looks.

After changing the upstream sources, run `.\tools\Update-Patches.ps1` to refresh `patches/`.

## Versions and updates

The app checks this repository's GitHub releases when it opens and shows a banner when a newer one exists. It only links
to the release page; it never downloads or installs anything. Betas are GitHub *pre-releases* and are only announced to
people who ticked "Also tell me about beta versions".

**Every release - beta or not - must have its own `x.y.z` number** (tag `v1.0.0`, `v1.0.1-beta`, ...). Windows Installer
only upgrades to a higher number, and the update check compares the numbers, so a beta and its final release cannot share
one.

## Things to know

- **Nothing is code-signed.** Windows SmartScreen warns about the downloaded installer, and games with anti-cheat may refuse
  to load the layers.
- The installer refuses to run while the *original* Quad-Views-Foveated or OBSMirror layer is installed: two copies of a
  layer cannot be active together. Uninstall the originals first. Your Quad-Views-Foveated settings in
  `%LocalAppData%\Quad-Views-Foveated` are kept, and tools that edit them keep working.
- Running the installer when the product is already installed (or choosing **Modify** in Windows' installed-apps list)
  shows one page with **Repair** and **Uninstall**. Repair puts the layer files back and re-enables both layers in the
  right order. Uninstall leaves your settings files and the OBS plugin. To update, just run the newer installer.
- A first install offers a desktop shortcut (ticked by default); the choice is remembered for later versions.
  Silent install without it: `msiexec /i QuadViews-Gaze-Mirror-x.y.z.msi /qn DESKTOPSHORTCUT=0`.
- Setup is a plain MSI on purpose. An earlier self-installing `.exe` was quarantined by antivirus heuristics half-way
  through an install.

## Licence

MIT - see [LICENSE](LICENSE). The upstream projects keep their own (MIT) licences and copyrights; their licence and
third-party notice files are installed next to the built layers.
