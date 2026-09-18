using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GazeOverlay;

public sealed record ReleaseInfo(Version Version, string Tag, string Title, bool IsBeta, string Url);

/// <summary>What the app itself remembers (not the ring settings - those live in the layer's own file).</summary>
public sealed class AppSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public bool IncludeBetas { get; set; }
    public string? SkippedVersion { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenXRGazeOverlay", "app.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenXR-Gaze-Overlay/" + CurrentVersion.ToString(3));
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
