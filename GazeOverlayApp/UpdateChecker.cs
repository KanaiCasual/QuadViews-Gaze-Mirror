using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GazeOverlay;

public sealed record ReleaseInfo(Version Version, string Tag, string Title, bool IsBeta, string Url);

/// <summary>What the app itself remembers (not the ring settings - those live in the layer's own file).</summary>
/// <summary>One of the user's own preset slots: the values as they were when it was saved.</summary>
public sealed class SavedPreset
{
    public DateTime SavedAt { get; set; }
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class AppSettings
{
    public const int SlotCount = 3;
    /// <summary>The user's own presets: ring look (Look + Tail tabs) and Quad Views sliders. null = empty slot.</summary>
    public SavedPreset?[] RingSlots { get; set; } = new SavedPreset?[SlotCount];
    public SavedPreset?[] QuadViewsSlots { get; set; } = new SavedPreset?[SlotCount];

    public bool CheckForUpdates { get; set; } = true;
    public bool IncludeBetas { get; set; }
    public string? SkippedVersion { get; set; }
    public bool ShowPreview { get; set; } = true;
    /// <summary>Where the window was, so it comes back beside the game where it was left. 0 = not remembered yet.</summary>
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    /// <summary>Size of the mirror image the last crop-tool picture was taken from (the picture itself is a file next to this one).</summary>
    public int MirrorWidth { get; set; }
    public int MirrorHeight { get; set; }
    /// <summary>Crop tab: the box cannot be moved or resized by mouse (against accidental drags).</summary>
    public bool CropLocked { get; set; }
    /// <summary>The crop picture: which eye each stored picture shows (-1 = unknown / none), and which one is looked at.</summary>
    public int MirrorPictureEye { get; set; } = -1;
    public int MirrorPictureOtherEye { get; set; } = -1;
    public bool MirrorPictureShowOther { get; set; }
    /// <summary>The picture was taken with the capture key: it is not replaced by the automatic refreshes.</summary>
    public bool MirrorPictureKept { get; set; }
    /// <summary>Virtual-key code of the key that takes the picture while a capture is armed (the mirror layer watches it). 0x13 = Pause.</summary>
    public int CaptureKey { get; set; } = 0x13;
    /// <summary>The mirror window: 0 = an ordinary window, otherwise the monitor it fills (1 = primary); which eye it shows;
    /// whether the OBS plugin gets a blank picture while it is open; whether it keeps a title bar when filling a monitor.</summary>
    public int MirrorMonitor { get; set; } = 1;
    public string MirrorEye { get; set; } = "right";
    public bool MirrorExclusive { get; set; } = true;
    public bool MirrorTitled { get; set; }
    /// <summary>Size of the mirror window's picture in pixels = what a capture tool gets. 0 = fill the monitor.</summary>
    public int MirrorOutputWidth { get; set; }
    public int MirrorOutputHeight { get; set; }
    /// <summary>The mirror window draws at most this many pictures a second (60 or 30).</summary>
    public int MirrorFps { get; set; } = 60;

    /// <summary>This app's own folder: preferences and the saved layer orders.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuadViewsGazeMirror");

    private static string FilePath => Path.Combine(Folder, "app.json");

    /// <summary>Up to 0.9.0 the product was called OpenXR Gaze Overlay; preset slots and the rest come along on first start.</summary>
    private static void AdoptOldFolder()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenXRGazeOverlay", "app.json");
            if (File.Exists(FilePath) || !File.Exists(old)) return;
            Directory.CreateDirectory(Folder);
            File.Copy(old, FilePath);
        }
        catch
        {
            // Starting with default preferences is not worth an error.
        }
    }

    public static AppSettings Load()
    {
        AdoptOldFolder();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // A hand-edited or older file may hold another number of slots.
                loaded.RingSlots = [.. (loaded.RingSlots ?? []).Concat(new SavedPreset?[SlotCount]).Take(SlotCount)];
                loaded.QuadViewsSlots = [.. (loaded.QuadViewsSlots ?? []).Concat(new SavedPreset?[SlotCount]).Take(SlotCount)];
                return loaded;
            }
        }
        catch
        {
            // A damaged file just means defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember a preference is not worth bothering the user about.
        }
    }
}

/// <summary>
/// Looks at the project's GitHub releases to tell the user when a newer version exists. It only ever reads that public
/// list and offers a link to the release page: it never downloads or installs anything (an app that fetches and runs
/// installers is exactly what antivirus software is suspicious of - and the MSI handles upgrades properly anyway).
/// </summary>
public static partial class UpdateChecker
{
    /// <summary>"owner/name", baked in at build time (-p:GitHubRepository=...). Empty = update checks are unavailable.</summary>
    public static string Repository { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepository")?.Value?.Trim() ?? "";

    public static bool IsConfigured => RepositoryPattern().IsMatch(Repository);

    /// <summary>e.g. "0.4.0" or "0.4.0-beta.1" (whatever -p:Version was at build time).</summary>
    public static string CurrentVersionText { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static Version CurrentVersion => ParseVersion(CurrentVersionText) ?? new Version(0, 0, 0);

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The newest release that is newer than this build, or null. Throws on network/API problems.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(string repository, bool includeBetas, CancellationToken cancellation = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QuadViews-Gaze-Mirror/" + CurrentVersion.ToString(3));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{repository}/releases?per_page=30", cancellation);
        return PickUpdate(ParseReleases(json, repository), CurrentVersion, includeBetas);
    }

    public static ReleaseInfo? PickUpdate(IEnumerable<ReleaseInfo> releases, Version current, bool includeBetas) =>
        releases.Where(r => (includeBetas || !r.IsBeta) && r.Version > current)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();

    public static List<ReleaseInfo> ParseReleases(string json, string repository)
    {
        var releases = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null) continue;

            var prerelease = item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            var title = item.TryGetProperty("name", out var name) ? name.GetString() : null;
            // Only ever offer a link back into this project's own releases on github.com, whatever the API returned.
            var url = item.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            if (url == null || !url.StartsWith($"https://github.com/{repository}/", StringComparison.OrdinalIgnoreCase))
            {
                url = $"https://github.com/{repository}/releases";
            }
            releases.Add(new ReleaseInfo(version, tag, string.IsNullOrWhiteSpace(title) ? tag : title!, prerelease || tag.Contains('-'), url));
        }
        return releases;
    }

    /// <summary>Takes the first x.y.z (or x.y) found in a tag like "v0.4.0-beta.1". Betas are told apart by GitHub's flag, not by number.</summary>
    public static Version? ParseVersion(string text)
    {
        var match = VersionPattern().Match(text);
        if (!match.Success) return null;
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), patch);
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}
