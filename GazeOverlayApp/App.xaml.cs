using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GazeOverlay;

/// <summary>
/// The settings app. It never installs anything, never asks for administrator rights and never touches Program Files or
/// the system registry - the MSI does all of that. All it writes is the ring's settings file in the user's profile.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length >= 2 && args[0] == "--selftest")
        {
            Shutdown(SelfTest(args[1]));
            return;
        }

        if (args.Length >= 2 && args[0] == "--screenshots")
        {
            // Development aid: renders every tab of the real window to PNG files, off-screen.
            var outputDir = args[1];
            Directory.CreateDirectory(outputDir);
            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false };
            window.Show();
            window.Dispatcher.InvokeAsync(() =>
            {
                for (var i = 0; i < window.Tabs.Items.Count; i++)
                {
                    window.Tabs.SelectedIndex = i;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var root = (FrameworkElement)window.Content;
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var context = visual.RenderOpen())
                    {
                        var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                        context.DrawRectangle((System.Windows.Media.Brush)window.FindResource("WindowBack"), null, bounds);
                        context.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, bounds);
                    }
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outputDir, $"tab{i}.png"));
                    encoder.Save(file);
                }
                Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>Exercises everything without showing a window, writing the results into a folder.</summary>
    private static int SelfTest(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        using var report = new StreamWriter(Path.Combine(outputDir, "selftest.txt"));
        try
        {
            var status = LayerStatus.Get();
            report.WriteLine($"quad views: {status.QuadViews.Health} - {status.QuadViews.Detail}");
            report.WriteLine($"obs mirror: {status.ObsMirror.Health} - {status.ObsMirror.Detail}");
            report.WriteLine($"obs: {status.ObsPath ?? "(not found)"} plugin present={status.ObsPluginPresent}");
            report.WriteLine($"order correct: {status.OrderCorrect?.ToString() ?? "n/a"}");
            foreach (var layer in status.Layers) report.WriteLine($"  [{(layer.Enabled ? "on " : "off")}] {layer.LayerName ?? "?"}  {layer.JsonPath}");

            var (liveState, liveText) = GazeLive.Read();
            report.WriteLine($"live: {liveState} - {liveText}");

            // Config round trip.
            var configPath = Path.Combine(outputDir, "roundtrip.cfg");
            var values = ConfigFile.Load(ConfigFile.DefaultPath);
            ConfigFile.Save(configPath, values);
            var reloaded = ConfigFile.Load(configPath);
            var mismatches = values.Where(kv => reloaded.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
            report.WriteLine($"config round trip: {values.Count} keys, mismatches: {(mismatches.Count == 0 ? "none" : string.Join(", ", mismatches))}");

            // Previews.
            var index = 0;
            foreach (var (background, moving) in new[] { (PreviewBackground.Cockpit, false), (PreviewBackground.Terrain, false), (PreviewBackground.Sky, false), (PreviewBackground.Cockpit, true) })
            {
                var bitmap = RingPreview.Render(values, background, moving, 340);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(outputDir, $"preview{index++}.png"));
                encoder.Save(file);
            }
            report.WriteLine("previews: ok");

            // Live link: play the layer's part (it creates the signal block) and check that the app's notify bumps it.
            var liveOk = true;
            try
            {
                using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("GazeOverlay.SettingsSignal");
                report.WriteLine("live link: a game is running, so its real signal block exists - simulated test skipped (notify returned " + LiveLink.NotifySettingsChanged() + ")");
            }
            catch (FileNotFoundException)
            {
                var withoutGame = LiveLink.NotifySettingsChanged();
                using var block = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("GazeOverlay.SettingsSignal", 12);
                using var view = block.CreateViewAccessor(0, 12);
                view.Write(0, 0x53534F47u);
                view.Write(4, 1u);
                var first = LiveLink.NotifySettingsChanged();
                var second = LiveLink.NotifySettingsChanged();
                var generation = view.ReadInt32(8);
                liveOk = !withoutGame && first && second && generation == 2;
                report.WriteLine($"live link - {(liveOk ? "ok  " : "FAIL")} no game -> {withoutGame}; with a (simulated) game -> {first}, {second}; counter = {generation} (expected 2)");
            }

            // Update check: version parsing and picking, against a canned release list (no network).
            const string canned = """
                [
                  {"tag_name":"v0.9.0-beta.1","name":"0.9 beta","prerelease":true,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/v0.9.0-beta.1"},
                  {"tag_name":"v0.5.2","name":"0.5.2","prerelease":false,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/v0.5.2"},
                  {"tag_name":"v0.6.0","name":"unfinished","prerelease":false,"draft":true,"html_url":"https://github.com/someone/project/releases/tag/v0.6.0"},
                  {"tag_name":"v0.5.10","name":"0.5.10","prerelease":false,"draft":false,"html_url":"https://evil.example/not-github"},
                  {"tag_name":"nightly","name":"no version in tag","prerelease":false,"draft":false,"html_url":"https://github.com/someone/project/releases/tag/nightly"}
                ]
                """;
            var releases = UpdateChecker.ParseReleases(canned, "someone/project");
            var stable = UpdateChecker.PickUpdate(releases, new Version(0, 5, 2), includeBetas: false);
            var beta = UpdateChecker.PickUpdate(releases, new Version(0, 5, 2), includeBetas: true);
            var none = UpdateChecker.PickUpdate(releases, new Version(1, 0, 0), includeBetas: true);
            var checks = new (string Name, bool Ok)[]
            {
                ("drafts and unversioned tags are ignored", releases.Count == 3),
                ("0.5.10 is newer than 0.5.2 (numeric, not text)", stable?.Version == new Version(0, 5, 10)),
                ("betas only when asked for", beta?.Version == new Version(0, 9, 0) && beta.IsBeta),
                ("nothing offered when already newer", none == null),
                ("foreign links are replaced by the project's releases page", stable?.Url == "https://github.com/someone/project/releases"),
            };
            foreach (var (name, ok) in checks) report.WriteLine($"update check - {(ok ? "ok  " : "FAIL")} {name}");
            report.WriteLine($"this build: version {UpdateChecker.CurrentVersionText}, repository '{UpdateChecker.Repository}' (configured: {UpdateChecker.IsConfigured})");

            // Optional live read of a public repository's release list, to prove the network path (--selftest <dir> <owner/name>).
            var liveRepo = Environment.GetCommandLineArgs().Skip(3).FirstOrDefault();
            if (liveRepo != null)
            {
                try
                {
                    // Task.Run: blocking the UI thread on an await that wants to resume on it would deadlock.
                    var found = Task.Run(() => UpdateChecker.CheckAsync(liveRepo, includeBetas: true)).GetAwaiter().GetResult();
                    report.WriteLine($"live check of {liveRepo}: " + (found == null ? "reachable, nothing newer than this build" : $"reachable, newest is {found.Version} ({found.Tag}) -> {found.Url}"));
                }
                catch (Exception liveError)
                {
                    report.WriteLine($"live check of {liveRepo} failed: {liveError.Message}");
                }
            }
            return checks.All(c => c.Ok) && liveOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.WriteLine("SELFTEST FAILED: " + ex);
            return 1;
        }
    }
}
