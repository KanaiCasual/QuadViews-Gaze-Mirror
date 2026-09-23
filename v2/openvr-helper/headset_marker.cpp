#ifndef NOMINMAX
#define NOMINMAX // windows.h (through the core's headers) must not turn std::min into a macro.
#endif
#include "headset_marker.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

#include "../core/log.h"

namespace gaze_mirror {

    namespace {

        constexpr float Distance = 2.f; // Metres out along the eye's line of sight.
        constexpr int Size = 64;

        // A plain ring in the given colour: the ring spans 78..98 % of the half-size, like the calibration target.
        std::vector<uint8_t> RingPixels(const float color[3]) {
            std::vector<uint8_t> pixels(size_t(Size) * Size * 4, 0);
            const float c = (Size - 1) / 2.f;
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    const float r = std::sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float alpha = 0.f;
                    if (r > 0.78f && r < 0.98f) alpha = std::min(1.f, std::min(r - 0.78f, 0.98f - r) * 12.f);
                    uint8_t* p = &pixels[(size_t(y) * Size + x) * 4];
                    for (int i = 0; i < 3; i++) p[i] = uint8_t(std::clamp(color[i], 0.f, 1.f) * 255.f + 0.5f);
                    p[3] = uint8_t(alpha * 255.f);
                }
            }
            return pixels;
        }

    } // namespace

    HeadsetMarker::~HeadsetMarker() {
        hide();
        if (_handle != vr::k_ulOverlayHandleInvalid && vr::VROverlay()) vr::VROverlay()->DestroyOverlay(_handle);
    }

    bool HeadsetMarker::ensure(const float color[3]) {
        vr::IVROverlay* overlay = vr::VROverlay();
        if (!overlay) return false;
        if (_handle == vr::k_ulOverlayHandleInvalid) {
            const vr::EVROverlayError error = overlay->CreateOverlay("kanaicasual.gazemirror.marker", "Gaze Mirror calibration marker", &_handle);
            if (error != vr::VROverlayError_None) {
                _handle = vr::k_ulOverlayHandleInvalid;
                if (_logProblem++ == 0) Log("marker: the overlay could not be made (%d)", int(error));
                return false;
            }
            Log("marker: shown in the headset while the app's calibration switch is on");
        }
        if (color[0] != _color[0] || color[1] != _color[1] || color[2] != _color[2]) {
            const std::vector<uint8_t> pixels = RingPixels(color);
            overlay->SetOverlayRaw(_handle, const_cast<uint8_t*>(pixels.data()), Size, Size, 4);
            for (int i = 0; i < 3; i++) _color[i] = color[i];
        }
        return true;
    }

    void HeadsetMarker::update(const FrameOutput& output, const FrameInput& input, int eye) {
        const EyeView& view = input.views[eye];
        if (!output.marker || !view.valid || !ensure(output.markerColor)) {
            hide();
            return;
        }
        // The ring's spot in the eye's image (NDC, up positive) -> a direction in that eye's view, then into head space.
        const float tanX = view.fov.left + (output.markerNdc[eye][0] + 1.f) * 0.5f * (view.fov.right - view.fov.left);
        const float tanY = view.fov.down + (output.markerNdc[eye][1] + 1.f) * 0.5f * (view.fov.up - view.fov.down);
        const EyePose& pose = input.eyeInHead[eye];
        Vec3 direction = Rotate(Normalize(pose.orientation), Vec3{tanX, tanY, -1.f});
        const float length = std::sqrt(direction.x * direction.x + direction.y * direction.y + direction.z * direction.z);
        direction = {direction.x / length, direction.y / length, direction.z / length};
        const Vec3 at{pose.position.x + direction.x * Distance, pose.position.y + direction.y * Distance, pose.position.z + direction.z * Distance};
        // The ring's radius, as the fraction of the image height it is, becomes an angle: the overlay spans the ring
        // (which sits at 88 % of the overlay's half-width) at that size two metres out.
        const float tanRadius = output.markerRadius * (view.fov.up - view.fov.down);
        const float width = std::clamp(2.f * Distance * tanRadius / 0.88f, 0.02f, 1.5f);
        vr::IVROverlay* overlay = vr::VROverlay();
        // Head-locked, facing the head (x right, y up, z towards the viewer).
        const vr::HmdMatrix34_t m = {{{1, 0, 0, at.x}, {0, 1, 0, at.y}, {0, 0, 1, at.z}}};
        overlay->SetOverlayTransformTrackedDeviceRelative(_handle, vr::k_unTrackedDeviceIndex_Hmd, &m);
        overlay->SetOverlayWidthInMeters(_handle, width);
        if (!_shown) {
            overlay->ShowOverlay(_handle);
            _shown = true;
        }
    }

    void HeadsetMarker::hide() {
        if (_shown && vr::VROverlay()) vr::VROverlay()->HideOverlay(_handle);
        _shown = false;
    }

} // namespace gaze_mirror
