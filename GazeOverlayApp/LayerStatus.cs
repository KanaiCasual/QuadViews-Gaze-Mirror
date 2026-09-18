using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GazeOverlay;

public enum LayerHealth { NotActive, GazeBuild, OriginalWithoutGaze, Duplicate }

public sealed record LayerEntry(string JsonPath, bool Enabled, string? LayerName, string? DllPath);

public sealed record LayerReport(LayerHealth Health, string Detail);

public sealed record SystemStatus(
    LayerReport QuadViews, LayerReport ObsMirror, string? ObsPath, bool ObsPluginPresent,
    IReadOnlyList<LayerEntry> Layers, bool? OrderCorrect);

/// <summary>
/// Read-only look at how the OpenXR layers are set up. This app never installs or changes them - the MSI does that -
/// it only explains what it finds.
/// </summary>
public static class LayerStatus
{
    public const string QuadViewsLayer = "XR_APILAYER_MBUCCHIA_quad_views_foveated";
    public const string ObsMirrorLayer = "XR_APILAYER_NOVENDOR_OBSMirror";
    private const string LayersKey = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";

    public static List<LayerEntry> ReadLayers()
    {
        var layers = new List<LayerEntry>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(LayersKey);
        if (key == null) return layers;

        foreach (var name in key.GetValueNames())
        {
            var enabled = key.GetValue(name) is int flag && flag == 0;
            string? layerName = null, dllPath = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(name));
                var layer = doc.RootElement.GetProperty("api_layer");
                layerName = layer.GetProperty("name").GetString();
                var library = layer.GetProperty("library_path").GetString();
                if (library != null) dllPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(name)!, library));
            }
            catch
            {
                // Missing or unreadable manifest: still list it so the order is visible.
            }
            layers.Add(new LayerEntry(name, enabled, layerName, dllPath));
        }
        return layers;
    }

    public static SystemStatus Get()
    {
        var layers = ReadLayers();
        // Gaze builds are recognised by a string only they contain.
        var quadViews = Describe(layers, QuadViewsLayer, "Quad-Views-Foveated", Encoding.Unicode.GetBytes("QuadViewsFoveated.EyeGaze"));
        var obsMirror = Describe(layers, ObsMirrorLayer, "OBS Mirror", Encoding.ASCII.GetBytes("Gaze overlay:"));

        var obsPath = FindObs();
        var pluginPresent = obsPath != null && File.Exists(Path.Combine(obsPath, "obs-plugins", "64bit", "win-openxr.dll"));

        // Quad views must sit closer to the game than the mirror, which only understands the final stereo image.
        var enabled = layers.Where(l => l.Enabled).ToList();
        var quadIndex = enabled.FindIndex(l => l.LayerName == QuadViewsLayer);
        var mirrorIndex = enabled.FindIndex(l => l.LayerName == ObsMirrorLayer);
        bool? orderCorrect = quadIndex >= 0 && mirrorIndex >= 0 ? quadIndex < mirrorIndex : null;

        return new SystemStatus(quadViews, obsMirror, obsPath, pluginPresent, layers, orderCorrect);
    }

    private static LayerReport Describe(List<LayerEntry> layers, string layerName, string friendlyName, byte[] gazeMarker)
    {
        var active = layers.Where(l => l.Enabled && l.LayerName == layerName).ToList();
        if (active.Count == 0)
        {
            return new LayerReport(LayerHealth.NotActive, layers.Any(l => l.LayerName == layerName)
                ? $"{friendlyName} is installed but switched off, so games do not load it."
                : $"Not installed. Run the OpenXR Gaze Overlay installer (.msi).");
        }
        if (active.Count > 1)
        {
            return new LayerReport(LayerHealth.Duplicate,
                $"{active.Count} copies of {friendlyName} are active at once, which breaks it. Uninstall the original one and keep the gaze build:\n" +
                string.Join("\n", active.Select(l => "  " + Path.GetDirectoryName(l.JsonPath))));
        }

        var entry = active[0];
        var folder = Path.GetDirectoryName(entry.JsonPath);
        var isGazeBuild = entry.DllPath != null && File.Exists(entry.DllPath) && File.ReadAllBytes(entry.DllPath).AsSpan().IndexOf(gazeMarker) >= 0;
        return isGazeBuild
            ? new LayerReport(LayerHealth.GazeBuild, $"Active, with gaze support ({folder}).")
            : new LayerReport(LayerHealth.OriginalWithoutGaze,
                $"The original {friendlyName} is active ({folder}). It works, but it cannot drive the gaze ring. Uninstall it, then run the OpenXR Gaze Overlay installer.");
    }

    public static string? FindObs()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\OBS Studio");
            if (key?.GetValue(null) is string path && Directory.Exists(path)) return path;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
