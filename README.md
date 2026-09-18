# OpenXR Gaze Overlay

Shows stream viewers where you are looking in VR: a ring - in the style of Tobii Ghost - drawn on your **OBS mirror only**.
It is never visible in the headset.

> **Unofficial community build.** This project contains modified builds of
> [Quad-Views-Foveated](https://github.com/mbucchia/Quad-Views-Foveated) by Matthieu Bucchianeri and
> [OpenXR-Layer-OBSMirror](https://github.com/Jabbah/OpenXR-Layer-OBSMirror) by Jabbah. They did not build, test or
> endorse it. Please do not ask them for support with it.

**Status: pre-release, not published yet.**

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
| `Installer/` | WiX 5 project for the MSI. Purely declarative - no custom code runs during setup |
| `tools/` | Patch scripts, the logo generator, an offline ring preview, an MSI inspector, a clip reviewer |
| `gaze.cfg` | A sample of the layer's settings file |
| `PUBLISHING-PLAN.md` | Notes and decisions for going public |

## Building

Needs Visual Studio 2022 (C++ workload), the .NET 10 SDK and Python 3 (upstream's build scripts use it).

```powershell
git clone --recurse-submodules <this repo>
.\tools\Apply-Patches.ps1        # once, on a fresh clone
.\Build-Layers.ps1               # both OpenXR layers
.\Build-Installer.ps1            # settings app + dist\OpenXR-Gaze-Overlay-<version>.msi
```

`Build-Installer.ps1` needs the unmodified OBS plugin (`win-openxr.dll` and its data files) from an upstream
OpenXR-Layer-OBSMirror release; see `GazeOverlayApp\Collect-Payload.ps1` for where it looks.

After changing the upstream sources, run `.\tools\Update-Patches.ps1` to refresh `patches/`.

## Versions and updates

The app checks this repository's GitHub releases when it opens and shows a banner when a newer one exists. It only links
to the release page; it never downloads or installs anything. Betas are GitHub *pre-releases* and are only announced to
people who ticked "Also tell me about beta versions".

**Every release - beta or not - must have its own `x.y.z` number** (tag `v0.4.0`, `v0.4.1-beta`, ...). Windows Installer
only upgrades to a higher number, and the update check compares the numbers, so a beta and its final release cannot share
one.

## Things to know

- **Nothing is code-signed.** Windows SmartScreen warns about the downloaded installer, and games with anti-cheat may refuse
  to load the layers.
- The installer refuses to run while the *original* Quad-Views-Foveated or OBSMirror layer is installed: two copies of a
  layer cannot be active together. Uninstall the originals first. Your Quad-Views-Foveated settings in
  `%LocalAppData%\Quad-Views-Foveated` are kept, and tools that edit them keep working.
- Setup is a plain MSI on purpose. An earlier self-installing `.exe` was quarantined by antivirus heuristics half-way
  through an install.

## Licence

MIT - see [LICENSE](LICENSE). The upstream projects keep their own (MIT) licences and copyrights; their licence and
third-party notice files are installed next to the built layers.
