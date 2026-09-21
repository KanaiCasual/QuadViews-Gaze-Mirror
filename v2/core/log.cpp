#include "pch.h"

#include "log.h"

namespace gaze_mirror {

    namespace {
        std::mutex g_lock;
        FILE* g_file = nullptr;
        bool g_tried = false;
        std::wstring g_name = L"gaze-mirror";

        void Open() {
            g_tried = true;
            wchar_t overridden[MAX_PATH];
            const DWORD overriddenLength = GetEnvironmentVariableW(L"GAZE_MIRROR_LOG_FILE", overridden, MAX_PATH);
            if (overriddenLength > 0 && overriddenLength < MAX_PATH) {
                g_file = _wfsopen(overridden, L"w", _SH_DENYWR);
                return;
            }
            wchar_t folder[MAX_PATH];
            const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", folder, MAX_PATH);
            if (length == 0 || length >= MAX_PATH) return;
            std::wstring path = std::wstring(folder) + L"\\QuadViewsGazeMirror";
            CreateDirectoryW(path.c_str(), nullptr);
            path += L"\\" + g_name + L".log";
            MoveFileExW(path.c_str(), (path + L".previous").c_str(), MOVEFILE_REPLACE_EXISTING);
            g_file = _wfsopen(path.c_str(), L"w", _SH_DENYWR);
        }

        void Write(const char* format, va_list arguments) {
            std::lock_guard<std::mutex> lock(g_lock);
            if (!g_tried) Open();
            if (!g_file) return;
            SYSTEMTIME now;
            GetLocalTime(&now);
            fprintf(g_file, "%02d:%02d:%02d.%03d  ", now.wHour, now.wMinute, now.wSecond, now.wMilliseconds);
            vfprintf(g_file, format, arguments);
            fputc('\n', g_file);
            fflush(g_file);
        }
    } // namespace

    void SetLogName(const wchar_t* name) {
        std::lock_guard<std::mutex> lock(g_lock);
        if (!g_tried) g_name = name;
    }

    void Log(const char* format, ...) {
        va_list arguments;
        va_start(arguments, format);
        Write(format, arguments);
        va_end(arguments);
    }

    void LogFewTimes(int& counter, int limit, const char* format, ...) {
        if (counter >= limit) return;
        counter++;
        va_list arguments;
        va_start(arguments, format);
        Write(format, arguments);
        va_end(arguments);
    }

} // namespace gaze_mirror
