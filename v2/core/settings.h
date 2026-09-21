// QuadViews Gaze Mirror 2.0 - core: settings.
//
// The same file and the same "something changed" signal the settings app already uses, so the app drives 2.0 as it
// is: the file is read once at start and again only when the app bumps its counter in shared memory - compared once
// per frame, a memory read. Every key of 1.x means the same thing here.
#pragma once

#include "log.h"

namespace gaze_mirror {

    enum class Style { Ring = 0, Glow = 1, Dot = 2, Spotlight = 3, Bubble = 4, Solid = 5, Heatmap = 6, Ghost = 7 };

    struct Settings {
        // Ring.
        bool enabled = true;
        Style style = Style::Ghost;
        bool screenBlend = true;
        float solidity = 0.4f;
        float radius = 0.042f; // Sizes: fractions of the (uncropped) eye image height.
        float thickness = 0.0010f;
        float feather = 0.0010f;
        float color[3] = {40 / 255.f, 170 / 255.f, 245 / 255.f};
        float opacity = 0.5f;
        float fillOpacity = 0.f;
        float glowWidth = 0.0028f;
        float glowStrength = 0.4f;
        float tailOpacity = 0.7f;
        float tailMax = 0.14f;
        float trailMs = 150.f;
        float blobTrailMs = 450.f;
        float heatMs = 2000.f;
        float heatCoolMs = 700.f;
        bool worldAnchored = true;
        float shadowOpacity = 0.3f;
        // Motion.
        bool adaptiveFilter = true;
        float filterMinCutoff = 1.5f;
        float filterBeta = 8.f;
        float smoothingMs = 60.f;
        float holdMs = 300.f;
        float fadeMs = 150.f;
        float dwellMs = 500.f;
        float dwellShrink = 0.25f;
        // Placement.
        float focusDistance = 0.f; // Metres; 0 = far away.
        float offsetX = 0.f;       // Trim, fractions of the image height (+X right, +Y up).
        float offsetY = 0.f;
        bool headsetMarker = false;
        // Crop.
        bool cropEnabled = false;
        float cropAspect = 16.f / 9.f; // 0 = free (cropWidth counts).
        float cropCenterX = 0.5f;
        float cropCenterY = 0.5f;
        float cropHeight = 0.5f;
        float cropWidth = 1.f;
        bool cropFollowVertical = false;
        float cropFollowDeadzone = 0.4f;
        float cropFollowReach = 1.f;
        float cropFollowGlideMs = 500.f;
        bool cropFollowReturn = true;
        float cropFollowHomeMs = 1200.f;
        bool stabilize = false;
        float stabilizeMinCutoff = 1.f;
        float stabilizeBeta = 8.f;
        float stabilizeRoom = 0.02f;
        // New in 2.0.
        int eye = 1;                // mirror_eye=left|right. The right eye unless told otherwise.
        uint32_t outputMaxSide = 3840; // output_max_side: the published picture's longest side is never more.
        float outputFps = 0.f;      // output_fps: 0 = every frame the game makes.
    };

    class SettingsSource {
      public:
        ~SettingsSource();

        // Reads the file now and connects to the app's signal.
        void open();

        // Once per frame. True when the settings were read again.
        bool refreshIfSignalled();

        const Settings& get() const {
            return _settings;
        }
        Settings& mutableSettings() {
            return _settings;
        }

        // Writes these keys back into the file, leaving every other line exactly as it is (the calibration nudge).
        void persist(const std::vector<std::pair<std::string, std::string>>& values);

        // The app's block: also carries the crop tool's picture request and the capture key.
        struct Signal {
            uint32_t magic;
            uint32_t version;
            volatile LONG generation;
            volatile LONG snapshotRequest;
            volatile LONG captureKey;
        };
        Signal* signal() const {
            return _signal;
        }

      private:
        void read();

        Settings _settings;
        HANDLE _mapping = nullptr;
        Signal* _signal = nullptr;
        LONG _seenGeneration = 0;
    };

    std::wstring SettingsFilePath();

} // namespace gaze_mirror
