using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace GazeOverlay;

/// <summary>What the 2.0 mirror is doing right now, read from the shared block every producer and reader share.</summary>
public sealed record MirrorState(bool Producing, bool OpenXR, string Program, string Application, int Width, int Height, bool GazeValid, int Eye);

/// <summary>
/// Read-only look at the 2.0 shared block ("GazeMirror2.Frames", v2\protocol\gaze_mirror_protocol.h): which producer is
/// live - the OpenXR layer inside a game, or the SteamVR helper - for which game, at what size, and whether gaze arrives.
/// Plus where the helper and the bundled Quad-Views-Foveated installer are. Nothing here polls: it is read when a tab
/// is shown or a button pressed.
/// </summary>
public static class MirrorLive
{
    private const string FramesName = "GazeMirror2.Frames";
    private const uint FramesMagic = 0x46324D47; // 'GM2F'
    // Offsets in Frames (all fields are 4- or 8-byte aligned integers/floats, see the header).
    private const int ProducerKindOffset = 12, WidthOffset = 24, HeightOffset = 28;
    private const int EyeOffset = 84, GazeValidOffset = 96, ProgramOffset = 112, ApplicationOffset = 176;
    private const int ProducerOpenXR = 1;
    public const string HelperRunningMutex = "GazeMirror2.HelperRunning";
    public const string HelperStopEvent = "GazeMirror2.HelperStop";

    public static MirrorState? Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(FramesName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, 320, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != FramesMagic) return null;
            var kind = view.ReadInt32(ProducerKindOffset);
            var program = new byte[64];
            var application = new byte[128];
            view.ReadArray(ProgramOffset, program, 0, program.Length);
            view.ReadArray(ApplicationOffset, application, 0, application.Length);
            static string Text(byte[] bytes)
            {
                var end = Array.IndexOf(bytes, (byte)0);
                return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
            }
            return new MirrorState(kind != 0, kind == ProducerOpenXR, Text(program), Text(application),
                (int)view.ReadUInt32(WidthOffset), (int)view.ReadUInt32(HeightOffset), view.ReadInt32(GazeValidOffset) != 0, view.ReadInt32(EyeOffset));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ the SteamVR helper

    /// <summary>Next to this app when installed; in the repository's 2.0 build folder when run from there.</summary>
    public static string? FindHelper() => FindBeside("GazeMirrorHelper.exe", Path.Combine("v2", "bin", "helper"));

    public static bool IsHelperRunning()
    {
        if (!Mutex.TryOpenExisting(HelperRunningMutex, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Starts the helper (it leaves by itself when SteamVR is not running).</summary>
    public static bool StartHelper() => RunHelper("", wait: false) != null;

    /// <summary>Tells a running helper to leave.</summary>
    public static void StopHelper()
    {
        try
        {
            using var stop = EventWaitHandle.OpenExisting(HelperStopEvent);
            stop.Set();
        }
        catch
        {
            // Not running.
        }
    }

    /// <summary>Registers the helper with SteamVR to start with it (or takes that back). Null = the helper was not found; otherwise its exit code (0 = done).</summary>
    public static async Task<int?> SetHelperAutostartAsync(bool on)
    {
        var process = RunHelper(on ? "--register" : "--unregister", wait: true);
        if (process == null) return null;
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    private static Process? RunHelper(string arguments, bool wait)
    {
        var program = FindHelper();
        if (program == null) return null;
        try
        {
            return Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(program)! });
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Quad-Views-Foveated's official installer

    /// <summary>The unmodified Quad-Views-Foveated installer shipped with this app, or null.</summary>
    public static string? FindQuadViewsInstaller()
    {
        var folder = FindBeside("Quad-Views-Foveated-1.1.3.msi", Path.Combine("v2", "external", "qvf"), subfolder: "Quad-Views-Foveated");
        return folder;
    }

    /// <summary>Starts Windows Installer on it; it asks for permission itself (this app never runs elevated).</summary>
    public static bool RunQuadViewsInstaller()
    {
        var msi = FindQuadViewsInstaller();
        if (msi == null) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\"") { UseShellExecute = true });
            return process != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindBeside(string fileName, string repositoryFolder, string? subfolder = null)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, subfolder ?? "", fileName);
        if (File.Exists(beside)) return beside;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var built = Path.Combine(folder.FullName, repositoryFolder, fileName);
            if (File.Exists(built)) return built;
        }
        return null;
    }
}
