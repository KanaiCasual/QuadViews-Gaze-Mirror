// QuadViews Gaze Mirror 2.0 - core: a plain text log. Each program says where its log goes (SetLogFile) before the
// first line; until then, lines go to %LocalAppData%\QuadViewsGazeMirror\<name>.log with the name given here.
#pragma once

namespace gaze_mirror {

    // Chooses the file. `name` is used as %LocalAppData%\QuadViewsGazeMirror\<name>.log; the environment variable
    // GAZE_MIRROR_LOG_FILE (tests) wins over it. The previous log of the same name is kept once as ".previous".
    void SetLogName(const wchar_t* name);

    // printf-style; a time stamp is added.
    void Log(const char* format, ...);

    // For things that would otherwise be written every frame: only the first `limit` times.
    void LogFewTimes(int& counter, int limit, const char* format, ...);

} // namespace gaze_mirror
