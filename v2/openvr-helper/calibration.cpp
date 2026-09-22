#ifndef NOMINMAX
#define NOMINMAX // windows.h (through the core's headers) must not turn std::min into a macro.
#endif
#include "calibration.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <string>
#include <vector>

#include <openvr.h>

#include "../core/log.h"
#include "../core/pipeline.h"
#include "vrchat_osc.h"

namespace gaze_mirror {

    namespace {

        constexpr float Pi = 3.14159265f;
        constexpr float TargetDistance = 2.f; // Metres in front of the head.
        constexpr int SettleMs = 800;         // The eyes travel and settle before sampling starts...
        constexpr int SampleMs = 1400;        // ...and this long is sampled.

        struct Target {
            float yawDeg, pitchDeg; // Right and up positive.
        };
        // Centre first, then outwards along each axis in turn, alternating sides so the eyes never travel far at once:
        // sideways in four steps out to 35 degrees, up in three steps to 22 and down in four to 30 (people look further
        // down than up). Sixteen looks, about 35 seconds; the curve gets a point at every one of them.
        constexpr Target Targets[] = {{0, 0},  {8, 0},   {-8, 0},  {16, 0},  {-16, 0}, {25, 0},  {-25, 0}, {35, 0},
                                      {-35, 0}, {0, 7},   {0, -7},  {0, 14},  {0, -14}, {0, 22},  {0, -22}, {0, -30}};

        float Median(std::vector<float> values) {
            if (values.empty()) return 0.f;
            std::sort(values.begin(), values.end());
            return values[values.size() / 2];
        }

        // The target: a white ring with a cyan dot, on nothing.
        std::vector<uint8_t> TargetPixels(int size) {
            std::vector<uint8_t> pixels(size_t(size) * size * 4, 0);
            const float c = (size - 1) / 2.f;
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    const float r = std::sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0 centre .. 1 edge
                    float alpha = 0.f;
                    uint8_t red = 255, green = 255, blue = 255;
                    if (r < 0.22f) {
                        alpha = 1.f;
                        red = 40; green = 200; blue = 255;
                    } else if (r > 0.78f && r < 0.98f) {
                        alpha = std::min(1.f, std::min(r - 0.78f, 0.98f - r) * 12.f);
                    }
                    uint8_t* p = &pixels[(size_t(y) * size + x) * 4];
                    p[0] = red; p[1] = green; p[2] = blue; p[3] = uint8_t(alpha * 255.f);
                }
            }
            return pixels;
        }

        std::string MapText(const std::vector<std::pair<float, float>>& map) {
            std::string text;
            char item[64];
            for (const auto& [raw, tangent] : map) {
                snprintf(item, sizeof(item), "%s%.4f:%.4f", text.empty() ? "" : ",", raw, tangent);
                text += item;
            }
            return text;
        }

        std::string Now() {
            const std::time_t t = std::time(nullptr);
            std::tm local{};
            localtime_s(&local, &t);
            char text[32];
            std::strftime(text, sizeof(text), "%Y-%m-%d %H:%M", &local);
            return text;
        }

    } // namespace

    GazeCalibration::~GazeCalibration() {
        if (_thread.joinable()) _thread.join();
    }

    void GazeCalibration::start(VrchatOscLink& link, Pipeline& pipeline) {
        if (_running) return;
        if (_thread.joinable()) _thread.join();
        _running = true;
        _thread = std::thread([this, &link, &pipeline] { run(&link, &pipeline); });
    }

    void GazeCalibration::run(VrchatOscLink* link, Pipeline* pipeline) {
        Log("calibration: starting");
        // Whatever happens, the request is taken back, so it does not run again on the next settings reload.
        pipeline->mutableSettings().vrchatCalibrate = false;
        const auto fail = [&](const char* why) {
            Log("calibration: failed - %s", why);
            pipeline->persist({{"vrchat_calibrate", "0"}, {"vrchat_calibrated", std::string("failed: ") + why}});
            _running = false;
        };

        vr::IVROverlay* overlay = vr::VROverlay();
        if (!overlay) return fail("SteamVR's overlay interface is not available to this program");
        vr::VROverlayHandle_t handle = vr::k_ulOverlayHandleInvalid;
        vr::EVROverlayError error = overlay->CreateOverlay("kanaicasual.gazemirror.calibration", "Gaze Mirror calibration", &handle);
        if (error != vr::VROverlayError_None) {
            char text[96];
            snprintf(text, sizeof(text), "the target overlay could not be made (%d)", int(error));
            return fail(text);
        }
        const int size = 64;
        std::vector<uint8_t> pixels = TargetPixels(size);
        overlay->SetOverlayRaw(handle, pixels.data(), size, size, 4);
        overlay->SetOverlayWidthInMeters(handle, 0.10f);

        std::vector<std::pair<float, float>> xs, ys; // (value, tangent)
        int missing = 0;
        int step = 0;
        for (const Target& target : Targets) {
            // The app shows which target is up (one small settings write per target; nothing runs on a timer).
            char progress[32];
            snprintf(progress, sizeof(progress), "running %d/%d", ++step, int(std::size(Targets)));
            pipeline->persist({{"vrchat_calibrated", progress}});
            const float tanYaw = std::tan(target.yawDeg * Pi / 180.f), tanPitch = std::tan(target.pitchDeg * Pi / 180.f);
            // Head-locked: x right, y up, z towards the viewer (so in front is -z).
            vr::HmdMatrix34_t m = {{{1, 0, 0, TargetDistance * tanYaw}, {0, 1, 0, TargetDistance * tanPitch}, {0, 0, 1, -TargetDistance}}};
            overlay->SetOverlayTransformTrackedDeviceRelative(handle, vr::k_unTrackedDeviceIndex_Hmd, &m);
            overlay->ShowOverlay(handle);
            std::this_thread::sleep_for(std::chrono::milliseconds(SettleMs));
            std::vector<float> vx, vy;
            const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(SampleMs);
            while (std::chrono::steady_clock::now() < until) {
                const VrchatOscLink::EyeSample sample = link->sample();
                if (sample.valid) {
                    vx.push_back(sample.x);
                    vy.push_back(sample.y);
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            if (vx.size() < 20) {
                missing++;
                Log("calibration: target %+.0f/%+.0f deg - no eye values arrived", target.yawDeg, target.pitchDeg);
                continue;
            }
            const float mx = Median(vx), my = Median(vy);
            Log("calibration: target %+.0f/%+.0f deg -> value (%+.3f, %+.3f) from %zu samples", target.yawDeg, target.pitchDeg, mx, my, vx.size());
            if (target.pitchDeg == 0.f) xs.emplace_back(mx, tanYaw);
            if (target.yawDeg == 0.f) ys.emplace_back(my, tanPitch);
        }
        overlay->HideOverlay(handle);
        overlay->DestroyOverlay(handle);

        if (xs.size() < 3 || ys.size() < 3) return fail(missing > 0 ? "no eye values arrived from VRChat (is OSC on, and the avatar one with eye tracking?)" : "too few targets");
        const auto byValue = [](const std::pair<float, float>& a, const std::pair<float, float>& b) { return a.first < b.first; };
        std::sort(xs.begin(), xs.end(), byValue);
        std::sort(ys.begin(), ys.end(), byValue);
        // The curve must go one way: a value that reads higher must mean further along. Where it does not, the tracker
        // could not tell two targets apart; those are dropped rather than folded into the curve.
        const auto monotonic = [](std::vector<std::pair<float, float>>& map) {
            std::vector<std::pair<float, float>> kept;
            for (const auto& point : map) {
                if (!kept.empty() && point.second <= kept.back().second) continue;
                kept.push_back(point);
            }
            map = kept;
        };
        monotonic(xs);
        monotonic(ys);
        if (xs.size() < 2 || ys.size() < 2) return fail("the values did not change with the direction of the look");

        const std::string mapX = MapText(xs), mapY = MapText(ys), when = Now();
        Log("calibration: done - x %s; y %s", mapX.c_str(), mapY.c_str());
        Settings& s = pipeline->mutableSettings();
        s.vrchatMapX = xs;
        s.vrchatMapY = ys;
        pipeline->persist({{"vrchat_calibrate", "0"}, {"vrchat_map_x", mapX}, {"vrchat_map_y", mapY}, {"vrchat_calibrated", when}});
        _running = false;
    }

} // namespace gaze_mirror
