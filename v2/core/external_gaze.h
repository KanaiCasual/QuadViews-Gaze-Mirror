// VR Gaze Mirror 2.0 - core: eye gaze handed in from outside (VRChat's OSC output, received by the helper), see
// protocol/gaze_mirror_protocol.h (ExternalGaze). Read only when a frame needs it; opening the block is retried now
// and then, never on a timer.
#pragma once

#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include "../protocol/gaze_mirror_protocol.h"

namespace gaze_mirror {

    struct ExternalGazeSample {
        bool valid = false;   // Fresh and complete.
        float left[2] = {0, 0};
        float right[2] = {0, 0};
        float leftOpenness = 1.f;
        float rightOpenness = 1.f;
        char writer[32] = {};
    };

    class ExternalGazeReader {
      public:
        ~ExternalGazeReader();

        // The latest sample if a writer is alive and wrote within ExternalGazeFreshMs; otherwise valid = false.
        ExternalGazeSample read();

        // Whether the block exists at all (a module has run since boot) - for logging once.
        bool present() const {
            return _block != nullptr;
        }

      private:
        bool open();
        void close();

        HANDLE _mapping = nullptr;
        volatile ExternalGaze* _block = nullptr;
        int _retryIn = 0;
    };

} // namespace gaze_mirror
