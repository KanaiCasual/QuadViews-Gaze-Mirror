# Changelog

## 2.0.0 - in development (1.9.x builds)

- Everything that makes the mirror picture is this project's own code. No modified third-party layers any more.
- OpenXR games and SteamVR games: the gaze mirror layer mirrors OpenXR games, a small SteamVR helper mirrors games that use SteamVR directly (OpenVR). Same crop, same ring, same OBS source.
- Gaze comes from the standard interfaces (OpenXR eye gaze, SteamVR eye tracking), so any eye-tracked headset that feeds them works. Quad-Views-Foveated is no longer needed for the ring.
- One draw per frame: crop, scale, steadying and ring in a single pass. Picture size cap (3840 by default) and picture rate cap on the Mirror page.
- New "Gaze Mirror" OBS source, with no settings of its own. The old "OpenXR Mirror Capture" plugin that 1.x shipped is removed on upgrade.
- Crop profiles: save framings, tie one to a game and it is applied when that game starts.
- The eye is chosen once on the Mirror page, for OBS and the mirror window alike. Right eye by default.
- Quad-Views-Foveated is optional: the official 1.1.3 installer is shipped and offered on the installer's last page and on the Status page.
- New look: a rail of pages on the left, cards, dark only. Look is now called Ring.

## 1.2.2 - 2026-09-20

- Steady the picture: the crop box holds still while the head moves.
- Capture the crop picture from inside the headset with a key.
- Follow travel markers.

## 1.2.1 - 2026-09-19

- Mirror window: capture the mirror without the OBS plugin (OBS Window Capture, Discord, ...).

## 1.1.0 - 2026-09-19

- Crop tool: a shape-locked box on the mirror image; OBS gets only that box.
- Gaze-following crop, box locking and drag rules.
- Right eye mirrored with the right eye's field of view; the crop picture shows the whole eye image.

## 1.0.0 - 2026-09-19

- First release: gaze ring on the OBS mirror, ring presets and slots, Quad Views page, layers tool with order check.
