// QuadViews Gaze Mirror 2.0 - core: the ring's motion - everything between "the eye points there" and the numbers
// the shader draws with. Positions are pixels of the (uncropped) eye image; sizes are fractions of its height.
//   - smoothing (One-Euro or simple), hold through blinks, fade in and out;
//   - world anchoring: when the head turns, everything remembered moves with the scene;
//   - the ghost tail, the bubble/solid trail, the heatmap's spots, the dwell feedback.
#pragma once

#include "math.h"
#include "settings.h"

namespace gaze_mirror {

    constexpr int MaxTrail = 32;

    // Exactly the shader's constant buffer.
    struct RingConstants {
        float center[2];
        float aspect[2];
        float radius;
        float thickness;
        float feather;
        float fillOpacity;
        float color[4];
        float style;
        float shadowOpacity;
        float trailCount;
        float heatGain;
        float glowWidth;
        float tailOpacity;
        float premultiply;
        float solidity;
        float glowStrength;
        float padding[3];
        float trail[MaxTrail][4];
    };

    class RingState {
      public:
        // Once per frame. haveTarget: the eye tracker gave a position (target, pixels of the eye image) this frame.
        // view: the image the target is in, with the head orientation it was rendered under (for the anchoring).
        void update(const Settings& settings, const EyeView& view, bool haveTarget, const float target[2], float dtMs);

        // Something is worth drawing this frame.
        bool visible(const Settings& settings) const;
        // The constants for the whole eye image (the renderer maps them into the crop).
        void constants(const Settings& settings, const EyeView& view, RingConstants& out) const;

        bool hasPosition() const {
            return _hasPosition;
        }
        // The smoothed position, pixels.
        const float* position() const {
            return _pos;
        }
        void reset();

      private:
        struct TrailSample {
            float x, y;
            float ageMs;
        };
        struct HeatSpot {
            float x, y, heat;
        };

        float _pos[2] = {0, 0};
        float _velocity[2] = {0, 0};
        float _rawPrev[2] = {0, 0};
        float _tailPos[2] = {0, 0};
        float _dwellAnchor[2] = {0, 0};
        bool _hasPosition = false;
        bool _hasRawPrev = false;
        bool _hasPrevOrientation = false;
        Quat _prevOrientation{0, 0, 0, 1};
        float _dwellMs = 0.f;
        float _dwell = 0.f;
        float _lostMs = 0.f;
        float _alpha = 0.f;
        std::deque<TrailSample> _trail;
        std::deque<HeatSpot> _heat;
    };

} // namespace gaze_mirror
