using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GazeOverlay;

/// <summary>
/// The settings app. It never installs anything, never asks for administrator rights and never touches Program Files or
/// the system registry - the MSI does all of that. All it writes is two settings files in the user's profile: the ring's,
/// and (only when Apply is clicked on the Quad Views tab) Quad-Views-Foveated's.
/// </summary>
public partial class App : Application
{
    /// <summary>True for --selftest and --screenshots: nothing about the window is remembered from those runs.</summary>
    public static bool Headless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        Headless = args.Length >= 1 && args[0] is "--selftest" or "--screenshots";

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
            // Optional window size (--screenshots <dir> <width> <height>), to check the layout when it is docked small.
            if (args.Length >= 4 && double.TryParse(args[2], out var width) && double.TryParse(args[3], out var height))
            {
                window.Width = width;
                window.Height = height;
            }
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

    /// <summary>
    /// Quad Views tab logic against a canned copy of a real case: a Pimax driven through SteamVR, with a settings file
    /// written by the QuadViews Companion (focus size only under headset sections that SteamVR does not match).
    /// </summary>
    private static bool SelfTestQuadViews(string outputDir, StreamWriter report)
    {
        const string cannedLog = """
            2026-09-18 22:19:00 +0100: XR_APILAYER_MBUCCHIA_quad_views_foveated layer (v1.1.4) is active
            2026-09-18 22:19:01 +0100: Application: DCS (DCS.exe)
            2026-09-18 22:19:01 +0100: Using OpenXR runtime: SteamVR/OpenXR 2.17.10
            2026-09-18 22:19:01 +0100: Using OpenXR system: SteamVR/OpenXR : lighthouse
            2026-09-18 22:19:01 +0100: Trying to locate configuration file at 'Z:\nowhere\settings.cfg'...
            2026-09-18 22:19:01 +0100: Eye tracking is supported
            2026-09-18 22:19:01 +0100: Recommended peripheral resolution: 2270x2372 (0.320x density)
            2026-09-18 22:19:01 +0100: Recommended focus resolution: 3042x3178 (1.225x density)
            2026-09-18 22:19:01 +0100:   Stereo pixel count was: 105,191,104 (7096x7412)
            2026-09-18 22:19:01 +0100:   Quad views pixel count is: 30,103,832
            2026-09-18 22:19:01 +0100:   Savings: -71.4%
            """;
        const string cannedFile = """
            # QuadViews configuration file created using TallyMouse's QuadViews Companion App.

            # Common settings for all headsets (unless overriden below).
            smoothen_focus_view_edges=0.2
            sharpen_focus_view=0
            turbo_mode=1
            peripheral_multiplier=0.32
            focus_multiplier=1.225

            [Pimax]
            # Dynamic Foveated Rendering settings
            horizontal_focus_section=0.2
            vertical_focus_section=0.2
            peripheral_multiplier=0.32

            [SteamVR]
            # Turbo Mode causes unexplained errors with SteamVR.
            turbo_mode=0

            [app:SomeGame]
            horizontal_focus_section=0.6
            """;

        var logPath = Path.Combine(outputDir, "quadviews-canned.log");
        File.WriteAllText(logPath, cannedLog);
        var session = QuadViewsSession.Read(logPath);
        var file = QuadViewsFile.Parse(cannedFile.Replace("\r\n", "\n"));

        long Pixels(QuadViewsFile f)
        {
            var e = QuadViewsFile.Evaluate([("test", f)], session);
            return QuadViewsPixels.Compute(session!.StereoWidth, session.StereoHeight, e["peripheral_multiplier"], e["focus_multiplier"], e["horizontal_focus_section"], e["vertical_focus_section"]).Total;
        }

        var before = QuadViewsFile.Evaluate([("test", file)], session);
        var sliders = QuadViewsSetting.All.ToDictionary(s => s.Id, _ => 0.0);
        var untouched = QuadViewsFile.Plan(file, sliders, new HashSet<string>());
        var after = QuadViewsFile.Evaluate([("test", untouched)], session);
        var again = QuadViewsFile.Plan(untouched, sliders, new HashSet<string>());

        sliders["focus_h"] = 30; sliders["peripheral_res"] = 16; sliders["turbo"] = 1;
        var edited = QuadViewsFile.Plan(untouched, sliders, new HashSet<string> { "focus_h", "peripheral_res", "turbo" });
        var editedValues = QuadViewsFile.Evaluate([("test", edited)], session);
        File.WriteAllText(Path.Combine(outputDir, "quadviews-planned.cfg"), edited.Text);

        var checks = new (string Name, bool Ok)[]
        {
            ("log: runtime, game and resolution are read", session is { RuntimeName: "SteamVR/OpenXR 2.17.10", AppName: "DCS", ExeName: "DCS.exe", StereoWidth: 7096, StereoHeight: 7412, QuadPixels: 30103832, EyeTracking: true }),
            ("a [Pimax] focus size does not reach a SteamVR runtime (the layer's 35 % applies)", before["horizontal_focus_section"] == 0.35),
            ("pixel count equals what the layer logged (30,103,832)", Pixels(file) == 30103832),
            ("Apply with nothing changed copies the intended 20 % into the common part", after["horizontal_focus_section"] == 0.2 && after["vertical_focus_section"] == 0.2),
            ("...which gives 17,081,296 pixels", Pixels(untouched) == 17081296),
            ("...and leaves values that already reach the game as they were written", untouched.InCommon("peripheral_multiplier") == "0.32"),
            ("a second Apply changes nothing", again.Text == untouched.Text),
            ("a changed slider is written to the common part and the headset sections", editedValues["horizontal_focus_section"] == 0.3 && edited.Entries().Count(e => e.Key == "horizontal_focus_section" && e.Value == "0.3") == 2),
            ("16 % of the pixels is a 0.4 multiplier", edited.InCommon("peripheral_multiplier") == "0.4" && editedValues["peripheral_multiplier"] == 0.4),
            ("per-game sections are left alone", edited.Entries().Any(e => e.Section == "[app:SomeGame]" && e.Value == "0.6")),
            ("the [SteamVR] Turbo guard is left alone", editedValues["turbo_mode"] == 0 && edited.InCommon("turbo_mode") == "1"),
        };
        foreach (var (name, ok) in checks) report.WriteLine($"quad views - {(ok ? "ok  " : "FAIL")} {name}");

        // This PC's real files, for information only (they change whenever the user changes a setting).
        var real = QuadViewsSession.Read(QuadViewsSession.LogPath);
        if (real is { HasResolution: true })
        {
            var files = new List<(string, QuadViewsFile)>();
            var shipped = real.ConfigPaths.FirstOrDefault(p => !string.Equals(p, QuadViewsFile.DefaultPath, StringComparison.OrdinalIgnoreCase));
            if (shipped != null && File.Exists(shipped)) files.Add(("shipped", QuadViewsFile.Load(shipped)));
            files.Add(("user", QuadViewsFile.Load(QuadViewsFile.DefaultPath)));
            var e = QuadViewsFile.Evaluate(files, real);
            var sections = real.EyeTracking == false ? ("horizontal_fixed_section", "vertical_fixed_section") : ("horizontal_focus_section", "vertical_focus_section");
            var computed = QuadViewsPixels.Compute(real.StereoWidth, real.StereoHeight, e["peripheral_multiplier"], e["focus_multiplier"], e[sections.Item1], e[sections.Item2]);
            report.WriteLine($"quad views (this PC): runtime '{real.RuntimeName}', {real.StereoWidth}x{real.StereoHeight}; layer logged {real.QuadPixels}, computed from the files {computed.Total} " +
                             $"(focus {computed.FocusWidth}x{computed.FocusHeight} vs logged {real.FocusResolution}); focus size in effect {e[sections.Item1]}x{e[sections.Item2]}");
        }
        else
        {
            report.WriteLine("quad views (this PC): no usable Quad-Views-Foveated log");
        }
        return checks.All(c => c.Ok);
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

            var quadViewsOk = SelfTestQuadViews(outputDir, report);

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
            return checks.All(c => c.Ok) && liveOk && quadViewsOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.WriteLine("SELFTEST FAILED: " + ex);
            return 1;
        }
    }
}
