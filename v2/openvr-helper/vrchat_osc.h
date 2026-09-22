// VR Gaze Mirror 2.0 - the OpenVR helper: eye gaze from VRChat over OSC.
//
// Headsets whose software feeds neither SteamVR's eye tracking nor OpenXR's eye-gaze extension often still feed
// VRChat - through VRCFaceTracking, EyeTrackVR, Babble, ALVR and the like - as the avatar's eye parameters. VRChat
// sends those parameters out over OSC to every OSCQuery service it finds on the machine. So the helper is one: it
// advertises itself over mDNS, answers VRChat's two HTTP questions, receives the OSC messages, and writes the eye
// values into the shared block the mirror reads (protocol/gaze_mirror_protocol.h, ExternalGaze). Nothing is sent to
// VRChat and nothing is configured in it: OSC only has to be switched on there, which it is for every face-tracking user.
//
// Three small threads, all of them waiting on their sockets - none looks at anything on a timer:
//   mDNS      224.0.0.251:5353 - answers "_oscjson._tcp.local" / "_osc._udp.local" queries with our service.
//   HTTP      127.0.0.1:<port> - "?HOST_INFO" (where our OSC port is) and "/" (a tree with /avatar, so VRChat sends
//             /avatar/change and every /avatar/parameters/*).
//   OSC       127.0.0.1:<port> - the parameters; the eye ones go into the block, the rest is ignored.
#pragma once

#include <cstdint>
#include <string>
#include <thread>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <windows.h>

#include "../protocol/gaze_mirror_protocol.h"

namespace gaze_mirror {

    class VrchatOscLink {
      public:
        ~VrchatOscLink();

        // Opens the sockets and starts the threads. False = nothing works without them (no Winsock, no ports).
        bool start();
        void stop();

        bool started() const {
            return _running;
        }

      private:
        void runMdns();
        void runHttp();
        void runOsc();
        void answerQuery(const uint8_t* packet, int length, const sockaddr_in& from);
        void announce();
        void handleOscPacket(const uint8_t* data, int length);
        void handleOscMessage(const uint8_t* data, int length);
        void publish();

        std::string _instance;  // "GazeMirror-1234": the service instance name VRChat shows.
        uint16_t _httpPort = 0;
        uint16_t _oscPort = 0;
        SOCKET _mdns = INVALID_SOCKET;
        SOCKET _http = INVALID_SOCKET;
        SOCKET _osc = INVALID_SOCKET;
        std::thread _mdnsThread, _httpThread, _oscThread;
        volatile bool _running = false;
        bool _winsock = false;

        // The block.
        HANDLE _mapping = nullptr;
        volatile ExternalGaze* _block = nullptr;
        LONGLONG _sequence = 0;

        // The eye values as they arrive (VRCFT's v2 parameters, or the older ones); -1..1, x right, y up.
        struct Eyes {
            float leftX = 0, leftY = 0, rightX = 0, rightY = 0;
            float leftOpen = 1, rightOpen = 1;
            bool haveX = false, haveY = false;
        } _eyes;
        int _logged = 0;
        int _hostInfoAsked = 0;
    };

} // namespace gaze_mirror
