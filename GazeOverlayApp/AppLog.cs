using System.IO;

namespace GazeOverlay;

/// <summary>
/// The app's own log: a few lines per session, written when something happens (start, settings loaded, helper started,
/// profile applied, update found, an error) - never on a timer. Always on, in every build, because the volume is tiny
/// and the one time it matters is the one time nobody switched it on. It sits next to the layer's and the helper's logs:
/// %LocalAppData%\QuadViewsGazeMirror\app.log; the previous session's log is kept once as app.previous.log.
/// Nothing here can fail the app: every write swallows its own errors.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _started;
    private static bool _disabled;

    public static string Path => System.IO.Path.Combine(AppSettings.Folder, "app.log");

    /// <summary>Called once at start-up. Headless runs (self-test, screenshots) leave the real log alone.</summary>
    public static void Start(bool headless)
    {
        lock (Gate)
        {
            _disabled = headless;
            if (_disabled || _started) return;
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var previous = System.IO.Path.Combine(AppSettings.Folder, "app.previous.log");
                if (File.Exists(Path)) File.Move(Path, previous, overwrite: true);
            }
            catch
            {
                // No folder, no previous: appending to whatever is there is still fine.
            }
            _started = true;
        }
    }

    public static void Write(string text)
    {
        lock (Gate)
        {
            if (_disabled) return;
            try
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
            catch
            {
                // A log that cannot be written is not worth a message.
            }
        }
    }

    public static void Write(string what, Exception error) => Write($"{what}: {error.GetType().Name}: {error.Message}");
}
