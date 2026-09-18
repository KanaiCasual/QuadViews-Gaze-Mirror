using System.IO;
using System.IO.MemoryMappedFiles;

namespace GazeOverlay;

/// <summary>
/// Tells a running game that the settings file changed. The OBSMirror layer never polls the file: it creates a tiny
/// shared-memory block (see SettingsSignal in eye_gaze_export.h) and compares a counter in it each frame. Bumping that
/// counter is what makes an edit go live - so with this app closed, nothing in the game ever re-reads anything.
/// </summary>
public static class LiveLink
{
    private const string SignalName = "GazeOverlay.SettingsSignal";
    private const uint Magic = 0x53534F47; // 'GOSS'

    /// <summary>Returns true when a running game was told to reload (false = no game running; it reads the file at start).</summary>
    public static bool NotifySettingsChanged()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(SignalName, MemoryMappedFileRights.ReadWrite);
            using var view = mapping.CreateViewAccessor(0, 12, MemoryMappedFileAccess.ReadWrite);
            if (view.ReadUInt32(0) != Magic) return false;
            // This app is the only writer, so a plain read-modify-write is enough.
            view.Write(8, view.ReadInt32(8) + 1);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // E.g. the game runs as administrator and this app does not. The settings still apply at the next game start.
            return false;
        }
    }
}
