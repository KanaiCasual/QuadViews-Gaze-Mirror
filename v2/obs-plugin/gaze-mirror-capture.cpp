// VR Gaze Mirror 2.0 - the OBS source "Gaze Mirror".
//
// A reader of the 2.0 format (protocol/gaze_mirror_protocol.h): it takes a reader place in the shared block while the
// source is shown, sleeps on its event until a producer - the OpenXR layer inside a game, or the OpenVR helper next to
// a SteamVR game - says a frame is ready, and draws that frame's shared texture straight from the graphics card. No
// copy of the picture, no timer, nothing to set up: the eye, the crop, the ring all come from the settings app.

#include <obs-module.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "../protocol/gaze_mirror_protocol.h"

using namespace gaze_mirror;

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("gaze-mirror-capture", "en-US")

namespace {

    constexpr LONG ProducerAny = 0;

    struct Source {
        obs_source_t* source = nullptr;
        LONG wantedProducer = ProducerAny; // Property: Auto / OpenXR / SteamVR.

        HANDLE mapping = nullptr;
        Frames* frames = nullptr;
        int place = -1; // Our reader place, or -1 while hidden.
        HANDLE frameReady = nullptr;
        HANDLE stopWaiting = nullptr;
        HANDLE waiter = nullptr;

        volatile LONG seenGeneration = -1; // Textures were (re)made: reopen.
        gs_texture_t* textures[SlotCount] = {};
        LONG textureGeneration[SlotCount] = {};
        uint32_t width = 0, height = 0;
        LONGLONG shownFrame = -1;
        int logged = 0;
    };

    void Info(const char* format, ...) {
        char text[512];
        va_list arguments;
        va_start(arguments, format);
        vsnprintf(text, sizeof(text), format, arguments);
        va_end(arguments);
        blog(LOG_INFO, "[gaze-mirror] %s", text);
    }

    bool OpenBlock(Source& s) {
        if (s.frames) return true;
        s.mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Frames), FramesMappingName);
        if (s.mapping) s.frames = static_cast<Frames*>(MapViewOfFile(s.mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Frames)));
        if (!s.frames) {
            Info("the shared block could not be opened (%lu)", GetLastError());
            if (s.mapping) CloseHandle(s.mapping);
            s.mapping = nullptr;
            return false;
        }
        if (s.frames->magic == 0) { // First one here: stamp it, a producer fills in the rest.
            s.frames->magic = FramesMagic;
            s.frames->version = FramesVersion;
            s.frames->structSize = sizeof(Frames);
            s.frames->latestSlot = NoSlot;
        }
        if (s.frames->version != FramesVersion) {
            Info("a component with format version %u is running, this plugin speaks %u", s.frames->version, FramesVersion);
            UnmapViewOfFile(s.frames);
            CloseHandle(s.mapping);
            s.frames = nullptr;
            s.mapping = nullptr;
            return false;
        }
        return true;
    }

    // The thread that sleeps until a frame is signalled. All it does is keep our heartbeat fresh - the producer
    // only works for readers whose heartbeat moves - and note that there is something new. OBS draws on its own cadence.
    DWORD WINAPI Waiter(void* data) {
        Source& s = *static_cast<Source*>(data);
        HANDLE handles[2] = {s.stopWaiting, s.frameReady};
        while (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
            if (s.place >= 0 && s.frames) s.frames->readers[s.place].heartbeatMs = static_cast<LONGLONG>(GetTickCount64());
        }
        return 0;
    }

    void TakePlace(Source& s) {
        if (s.place >= 0 || !OpenBlock(s)) return;
        const LONG myPid = static_cast<LONG>(GetCurrentProcessId());
        for (uint32_t i = 0; i < ReaderCount && s.place < 0; i++) {
            if (InterlockedCompareExchange(&s.frames->readers[i].pid, myPid, 0) == 0) s.place = static_cast<int>(i);
        }
        if (s.place < 0) {
            Info("all reader places are taken");
            return;
        }
        Reader& me = s.frames->readers[s.place];
        me.readingSlot = NoSlot;
        me.heartbeatMs = static_cast<LONGLONG>(GetTickCount64());
        wchar_t name[64];
        swprintf_s(name, ReaderEventFormat, static_cast<unsigned>(s.place));
        s.frameReady = CreateEventW(nullptr, FALSE, FALSE, name);
        s.stopWaiting = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        s.waiter = CreateThread(nullptr, 0, Waiter, &s, 0, nullptr);
        s.seenGeneration = -1;
        if (HANDLE wake = CreateEventW(nullptr, FALSE, FALSE, ProducerWakeName)) { // A sleeping producer: there is a reader now.
            SetEvent(wake);
            CloseHandle(wake);
        }
        Info("reader place %d taken", s.place);
    }

    void LeavePlace(Source& s) {
        if (s.waiter) {
            SetEvent(s.stopWaiting);
            WaitForSingleObject(s.waiter, INFINITE);
            CloseHandle(s.waiter);
            s.waiter = nullptr;
        }
        if (s.stopWaiting) CloseHandle(s.stopWaiting);
        if (s.frameReady) CloseHandle(s.frameReady);
        s.stopWaiting = s.frameReady = nullptr;
        if (s.place >= 0 && s.frames) {
            s.frames->readers[s.place].readingSlot = NoSlot;
            InterlockedExchange(&s.frames->readers[s.place].pid, 0);
            Info("reader place %d left", s.place);
        }
        s.place = -1;
        obs_enter_graphics();
        for (auto& texture : s.textures) {
            gs_texture_destroy(texture);
            texture = nullptr;
        }
        obs_leave_graphics();
        s.width = s.height = 0;
    }

    // ---- OBS source callbacks ----------------------------------------------------------------------------------
    const char* GetName(void*) {
        return obs_module_text("SourceName");
    }

    void Update(void* data, obs_data_t* settings) {
        Source& s = *static_cast<Source*>(data);
        s.wantedProducer = static_cast<LONG>(obs_data_get_int(settings, "producer"));
    }

    void* Create(obs_data_t* settings, obs_source_t* source) {
        auto* s = new Source();
        s->source = source;
        Update(s, settings);
        return s;
    }

    void Destroy(void* data) {
        Source* s = static_cast<Source*>(data);
        LeavePlace(*s);
        if (s->frames) UnmapViewOfFile(s->frames);
        if (s->mapping) CloseHandle(s->mapping);
        delete s;
    }

    void Show(void* data) {
        TakePlace(*static_cast<Source*>(data));
    }
    void Hide(void* data) {
        LeavePlace(*static_cast<Source*>(data));
    }

    uint32_t GetWidth(void* data) {
        return static_cast<Source*>(data)->width;
    }
    uint32_t GetHeight(void* data) {
        return static_cast<Source*>(data)->height;
    }

    void Render(void* data, gs_effect_t*) {
        Source& s = *static_cast<Source*>(data);
        if (s.place < 0 || !s.frames) return;
        Frames& frames = *s.frames;
        if (frames.producerKind == ProducerNone || (s.wantedProducer != ProducerAny && frames.producerKind != s.wantedProducer)) {
            s.width = s.height = 0;
            return;
        }
        // Textures were (re)made: forget what we opened.
        const LONG generation = frames.generation;
        if (generation != s.seenGeneration) {
            s.seenGeneration = generation;
            for (auto& texture : s.textures) {
                gs_texture_destroy(texture);
                texture = nullptr;
            }
            s.width = frames.width;
            s.height = frames.height;
            if (s.logged < 20) {
                s.logged++;
                Info("%s from %s (%s), %u x %u", frames.producerKind == ProducerOpenXR ? "OpenXR game" : "SteamVR game", frames.producerProgram,
                     frames.producerApplication, frames.width, frames.height);
            }
        }
        if (frames.latestSlot == NoSlot || s.width == 0) return;

        // Mark the slot we are about to use, and make sure it still was the latest when the mark went up.
        Reader& me = frames.readers[s.place];
        LONG slot;
        do {
            slot = frames.latestSlot;
            me.readingSlot = slot;
        } while (slot != frames.latestSlot);
        if (slot < 0 || slot >= static_cast<LONG>(SlotCount) || frames.textureHandle[slot] == 0) {
            me.readingSlot = NoSlot;
            return;
        }
        if (!s.textures[slot] || s.textureGeneration[slot] != generation) {
            gs_texture_destroy(s.textures[slot]);
            s.textures[slot] = gs_texture_open_shared(static_cast<uint32_t>(frames.textureHandle[slot]));
            s.textureGeneration[slot] = generation;
            if (!s.textures[slot]) Info("shared texture %ld could not be opened", slot);
        }
        if (s.textures[slot]) {
            gs_effect_t* effect = obs_get_base_effect(OBS_EFFECT_OPAQUE);
            while (gs_effect_loop(effect, "Draw")) obs_source_draw(s.textures[slot], 0, 0, 0, 0, false);
            s.shownFrame = frames.frameNumber;
        }
        me.readingSlot = NoSlot;
    }

    void Defaults(obs_data_t* settings) {
        obs_data_set_default_int(settings, "producer", ProducerAny);
    }

    obs_properties_t* Properties(void* data) {
        Source* s = static_cast<Source*>(data);
        obs_properties_t* properties = obs_properties_create();
        obs_property_t* list = obs_properties_add_list(properties, "producer", obs_module_text("Producer"), OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_INT);
        obs_property_list_add_int(list, obs_module_text("ProducerAuto"), ProducerAny);
        obs_property_list_add_int(list, obs_module_text("ProducerOpenXR"), ProducerOpenXR);
        obs_property_list_add_int(list, obs_module_text("ProducerSteamVR"), ProducerOpenVR);
        char status[256];
        if (s && s->frames && s->frames->producerKind != ProducerNone) {
            snprintf(status, sizeof(status), "%s: %s (%s), %u x %u", s->frames->producerKind == ProducerOpenXR ? "OpenXR" : "SteamVR",
                     s->frames->producerProgram, s->frames->producerApplication, s->frames->width, s->frames->height);
        } else {
            snprintf(status, sizeof(status), "%s", obs_module_text("StatusNoGame"));
        }
        obs_properties_add_text(properties, "status", status, OBS_TEXT_INFO);
        obs_properties_add_text(properties, "hint", obs_module_text("Hint"), OBS_TEXT_INFO);
        return properties;
    }

} // namespace

bool obs_module_load(void) {
    obs_source_info info{};
    info.id = "gaze_mirror_capture";
    info.type = OBS_SOURCE_TYPE_INPUT;
    info.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_CUSTOM_DRAW | OBS_SOURCE_DO_NOT_DUPLICATE;
    info.get_name = GetName;
    info.create = Create;
    info.destroy = Destroy;
    info.update = Update;
    info.get_defaults = Defaults;
    info.get_properties = Properties;
    info.show = Show;
    info.hide = Hide;
    info.get_width = GetWidth;
    info.get_height = GetHeight;
    info.video_render = Render;
    info.icon_type = OBS_ICON_TYPE_GAME_CAPTURE;
    obs_register_source(&info);
    Info("loaded (format version %u)", FramesVersion);
    return true;
}
