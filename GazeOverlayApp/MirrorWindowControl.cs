using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GazeOverlay;

/// <summary>One display, in real pixels. Number 1 is the primary one, the rest follow from left to right - the same numbering MirrorWindow.exe uses.</summary>
public sealed record MonitorInfo(int Number, int Width, int Height);

/// <summary>
/// Starts, steers and closes the mirror window (MirrorWindow.exe) - the capturable window that shows the mirror picture.
/// The window usually sits behind the game where nobody can reach it, so everything goes through its command line:
/// starting the program again while it runs hands the new arguments to the running window.
/// </summary>
public static class MirrorWindowControl
{
    private const string SingleInstanceMutex = "QuadViewsGazeMirror.MirrorWindow.Single";

    /// <summary>Next to this app when installed; in the repository's build folder when run from there.</summary>
    public static string? FindProgram()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "MirrorWindow.exe");
        if (File.Exists(beside)) return beside;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var built = Path.Combine(folder.FullName, "MirrorWindow", "bin", "x64", "Release", "MirrorWindow.exe");
            if (File.Exists(built)) return built;
        }
        return null;
    }

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(SingleInstanceMutex, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static string Arguments(AppSettings settings)
    {
        var placement = settings.MirrorMonitor > 0 ? $"--monitor {settings.MirrorMonitor} --titled {(settings.MirrorTitled ? 1 : 0)}" : "--windowed";
        var size = settings.MirrorOutputWidth >= 64 && settings.MirrorOutputHeight >= 64 ? $"{settings.MirrorOutputWidth}x{settings.MirrorOutputHeight}" : "fill";
        return $"{placement} --size {size} --fps {(settings.MirrorFps <= 30 ? 30 : 60)} --eye {(settings.MirrorEye == "left" ? "left" : "right")} --exclusive {(settings.MirrorExclusive ? 1 : 0)}";
    }

    /// <summary>Opens the window, or - if it is open - applies the settings to it.</summary>
    public static bool Apply(AppSettings settings) => Run(Arguments(settings));

    public static bool Close() => Run("--close");

    private static bool Run(string arguments)
    {
        var program = FindProgram();
        if (program == null) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(program)! });
            return process != null;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ monitors, in real pixels whatever the scaling

    public static List<MonitorInfo> Monitors()
    {
        var found = new List<(int X, int Y, int Width, int Height)>();
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            const int AttachedToDesktop = 1;
            if ((device.StateFlags & AttachedToDesktop) != 0)
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(device.DeviceName, -1 /* current */, ref mode))
                {
                    found.Add((mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight));
                }
            }
            device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return [.. found.OrderBy(m => m.X == 0 && m.Y == 0 ? 0 : 1).ThenBy(m => m.X)
                        .Select((m, index) => new MonitorInfo(index + 1, m.Width, m.Height))];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
}
