// VR Gaze Mirror 2.0 - the OpenVR helper: calibration of the gaze that comes from VRChat.
//
// The eye values VRChat's avatar carries are whatever the eye-tracking software makes of the cameras - not angles, and
// not in proportion to them either. So the helper measures the relation once: it shows a small target in the headset
// (an OpenVR overlay, head-locked, two metres away) at nine known directions, the user looks at each for two seconds,
// and the values that arrive are recorded. From the five targets along each axis a curve "value -> tangent of the
// angle" is fitted and written into the settings file (vrchat_map_x / vrchat_map_y), which the core reads from then on.
//
// Started only from the settings app (vrchat_calibrate=1 in the file), never by a key: the target is the one thing this
// program ever shows inside the headset, and it goes away by itself.
#pragma once

#include <atomic>
#include <thread>

namespace gaze_mirror {

    class Pipeline;
    class VrchatOscLink;

    class GazeCalibration {
      public:
        ~GazeCalibration();

        // Runs the whole procedure on its own thread; the result lands in the settings file through `pipeline`.
        void start(VrchatOscLink& link, Pipeline& pipeline);

        bool running() const {
            return _running;
        }

      private:
        void run(VrchatOscLink* link, Pipeline* pipeline);

        std::thread _thread;
        std::atomic<bool> _running{false};
    };

} // namespace gaze_mirror
