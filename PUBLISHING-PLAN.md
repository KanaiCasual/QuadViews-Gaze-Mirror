# Publishing plan — VR eye-gaze overlay for OBS

Notes captured 2026-09-18 for when we make this public. Nothing here has been built yet.

## Goal

One public GitHub repo with **one installer** that sets up the complete modified stack, so users do **not** need the
original Quad-Views-Foveated or OBSMirror installed first.

Decisions already made:

- **No pull request to OBSMirror upstream.** We ship our own fork.
- **Single repo, complete install** of our modified Quad-Views-Foveated + OBSMirror layer + the (unmodified) OBS plugin.

## What users need from us (and why stock doesn't work)

| Piece | Stock works? | Why |
|---|---|---|
| Quad-Views-Foveated layer | No | Stock never publishes the gaze. Ours writes it to shared memory `QuadViewsFoveated.EyeGaze` (13 lines in `xrLocateViews` + `eye_gaze_export.h`). |
| OBSMirror layer DLL | No | Stock doesn't read the gaze or draw. Ours adds `drawGazeOverlay()`, the `gaze.cfg` config, and the swapchain memory-leak fix. |
| OBS plugin (`win-openxr.dll`) | Yes | Unmodified. We only redistribute it. |

Either half alone fails gracefully (no ring, nothing breaks). Neither layer's original function was changed; nothing we
draw ever reaches the headset.

Built from: QVF upstream `main` (= 1.1.4; differences from the 1.1.3 release are upstream's, the Turbo-default change is
only in the shipped `settings.cfg`) and OBSMirror upstream `main` (a481a37, 2026-03-19).

## Licensing (checked in the repos — not legal advice)

- **Quad-Views-Foveated**: MIT, (c) Matthieu Bucchianeri. Modify + redistribute (incl. binaries) is allowed; must ship his
  copyright notice + the MIT text.
- **OBSMirror**: MIT (LICENSE still names Bucchianeri because it is built on his layer template). Same condition.
- **THIRD_PARTY** files in both repos (OpenXR SDK = Apache 2.0, fmt, FidelityFX CAS, OpenXR-MixedReality, ...) must ship
  too — put them in the install folder.
- **OBS plugin** links libobs (GPL). Keep the plugin's source in our repo whenever we distribute the DLL (a fork does
  this automatically).
- We cannot sign with their certificates and must not imply it is official/endorsed. README wording: "unofficial modified
  build of X by Y", link the originals, send support to our repo — not to mbucchia or Jabbah.

## How a complete install works

Each API layer = a DLL + JSON manifest (+ `settings.cfg` for QVF) in a folder, plus one registry value under
`HKLM\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit` (value name = full path of the JSON, DWORD 0 = enabled, 1 = disabled).
Both upstream repos ship `Install-Layer.ps1` scripts that do exactly this. The OBS plugin is `win-openxr.dll` + locale/preset
files copied into the OBS install folder (`obs-plugins\64bit` and `data\obs-plugins\win-openxr`); find OBS via its registry key.

## Things the installer must get right

1. **Existing original installs.** If stock QVF/OBSMirror is registered and ours is added next to it, both load and things
   break. Detect the originals and **disable** their registry entries (set to 1) rather than delete; our uninstaller
   re-enables them. *This is the part that needs real testing, on a machine/VM that has the originals installed.*
2. **Layer order.** QVF must sit above (closer to the game than) OBSMirror, because OBSMirror only understands the final
   2-view stereo image. We install both, so write the registry entries in the right order. **Verify first** which way the
   OpenXR loader enumerates that key (fredemmott's OpenXR API Layers tool lists game -> runtime; on the dev machine QVF is
   listed first).
3. **Keep the original internal layer names** (`XR_APILAYER_MBUCCHIA_quad_views_foveated`, `XR_APILAYER_NOVENDOR_OBSMirror`)
   so existing `%LOCALAPPDATA%\Quad-Views-Foveated\settings.cfg`, companion apps, and anything detecting the layer by name
   keep working.
4. **Unsigned binaries.** SmartScreen "unknown publisher" on the installer, and the anti-cheat warning on the layers. Only
   fix is a paid code-signing certificate. Be upfront in the README (DCS is fine).
5. **Gaze config.** Installer writes the default `gaze.cfg` to `%LOCALAPPDATA%\XR_APILAYER_NOVENDOR_OBSMirror_gaze.cfg`
   (works normally for end users; the redirect problem only affects Claude's packaged app). Don't overwrite an existing one.

## Update 2026-09-18 (late): installer is now an MSI

The self-installing exe described in the next section was abandoned the same evening: an antivirus quarantined it and
rolled back a real install, leaving no active OpenXR layers (recovered with `Repair-Layers.ps1`). Replacement:
`Build-Installer.ps1` builds `dist\OpenXR-Gaze-Overlay-<version>.msi` with WiX 5 - fully declarative, no custom actions of
ours, never edits other products' registry entries (it blocks and asks the user to uninstall the originals instead), OBS
plugin never overwritten/never removed. The settings app (`GazeOverlayApp\`) is now a plain non-elevated program the MSI
installs; it only writes the ring's cfg. `tools\Inspect-Msi.ps1` inspects a built MSI read-only.
**Not yet done:** installing the MSI anywhere (incl. confirming the layer order it produces), a clean-machine test, and -
free, once the repo is public - applying to SignPath Foundation for open-source code signing.

## Update 2026-09-18: the installer now exists (superseded - kept for history)

`GazeOverlayApp\` builds `dist\GazeOverlay.exe` - one self-contained exe that is the installer, the uninstaller and a
slider-based settings GUI with a live preview. It supersedes the "Inno Setup" idea below and already implements points 1-3
and 5 of "Things the installer must get right" (disable-not-delete, order, original layer names, config not overwritten).
Build: `GazeOverlayApp\Collect-Payload.ps1`, then `dotnet publish GazeOverlayApp\GazeOverlayApp.csproj -c Release -o dist`.
Headless checks: `GazeOverlay.exe --selftest <dir>` and `--screenshots <dir>`.
**Not yet done:** a real elevated install/uninstall run, and a test on a machine that has the stock originals installed.

## What to build

- **Repo layout**: one repo, both forks as subfolders (keep upstream LICENSE + THIRD_PARTY in each), README (what it is,
  what changed, unofficial), sample `gaze.cfg`, preview image.
- **Installer**: single `.exe` via Inno Setup (free; handles elevation, uninstall entry, registry). Installs both layers +
  OBS plugin, offers the default ghost config, disables conflicting originals, restores them on uninstall.
- **CI**: GitHub Actions on `windows-latest` building both DLLs + the installer on tagged releases (QVF already has a
  workflow to adapt). Build notes: NuGet restore needs `/p:RestorePackagesConfig=true`; building the QVF vcxproj directly
  needs `/p:SolutionDir=<root>\ /p:SolutionName=XR_APILAYER_MBUCCHIA_quad_views_foveated`; Python is needed for the
  dispatch generator.

## Optional later: drop the QVF fork entirely

A small separate "gaze export" layer installed **above** QVF could read the focus-view FOVs QVF returns from
`xrLocateViews` (their center = the gaze point) and publish the same shared-memory block. Users could then keep stock QVF,
and it would also work with native quad-views runtimes (Varjo, newer Pimax) where QVF isn't used at all.
Costs: a third layer to install; gaze slightly less exact at the far edges of the view (QVF clamps the focus region
there); can't distinguish "gaze invalid" from "looking at the center" as cleanly. Not built; roughly the effort of the QVF
patch plus the layer boilerplate both repos already contain.

## Before publishing — loose ends in the overlay itself

- Tail readability over bright terrain (see the 2026-09-18 clip review); shader tail tweaks were built but not yet
  confirmed in use.
- fxc warning X4000 in `shade()` (early returns) — believed harmless, tidy before release.
- Default `gaze.cfg` **and the DLL's built-in defaults** should reflect the final tuned look. As of 2026-09-18 the accepted
  ("workable for now") look is: radius 0.042, thickness 0.0010, feather 0.0010, glow 0.0028, glow_strength 0.4, opacity 0.5,
  solidity 0.4, color 40,170,245, tail_opacity 0.7. The built-in defaults still hold older, crisper values.
- Judge the teardrop tail's strength against the now-faint ring in motion (`tail_opacity`).
- Both-eyes mirror mode and the non-fast-copy (modified FOV) path have not been tested with the overlay.
