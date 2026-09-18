using System.IO;
using System.IO.MemoryMappedFiles;

namespace GazeOverlay;

public enum GazeLiveState { NoGame, Tracking, EyesNotTracked, Stale }

/// <summary>
/// Peeks at the shared-memory block the modified Quad-Views-Foveated publishes (see eye_gaze_export.h), to tell the user
/// whether gaze data is actually flowing. Read-only.
/// </summary>
public static class GazeLive
{
    private const string SharedMemoryName = "QuadViewsFoveated.EyeGaze";
    private const uint Magic = 0x47455651; // 'QVEG'

    public static (GazeLiveState State, string Text) Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Magic) return (GazeLiveState.NoGame, "No gaze data. Start a game that uses quad views.");

            var version = view.ReadUInt32(4);
            var valid = view.ReadUInt32(12) != 0;
            var ageMs = Environment.TickCount64 - (long)view.ReadUInt64(16);
            if (ageMs > 2000) return (GazeLiveState.Stale, "A game published gaze data earlier, but nothing fresh is arriving (paused, or in a menu without foveated rendering).");
            return valid
                ? (GazeLiveState.Tracking, $"Receiving live gaze data (format v{version}). The ring shows in OBS while its mirror source is active.")
                : (GazeLiveState.EyesNotTracked, "The game is running, but the headset is not tracking your eyes right now.");
        }
        catch (FileNotFoundException)
        {
            return (GazeLiveState.NoGame, "No gaze data. Start a game that uses quad views (with eye tracking enabled in the headset software).");
        }
        catch (Exception e)
        {
            return (GazeLiveState.NoGame, "Could not check for gaze data: " + e.Message);
        }
    }
}
