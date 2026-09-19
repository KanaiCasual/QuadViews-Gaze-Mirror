using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GazeOverlay;

/// <summary>
/// The Quad Views tab: the settings TallyMouse's QuadViews Companion edits, plus how many pixels they make the game render.
/// Unlike the ring's settings these only take effect when a game starts, so they are saved with Apply, not as you drag.
/// </summary>
public partial class MainWindow
{
    private readonly string _quadViewsPath = QuadViewsFile.DefaultPath;
    private QuadViewsFile _quadViewsFile = QuadViewsFile.Load(QuadViewsFile.DefaultPath);
    private QuadViewsSession? _quadViewsSession;
    private readonly Dictionary<string, double> _quadViewsValues = [];
    private readonly HashSet<string> _quadViewsDirty = [];
    private readonly Dictionary<string, Action<double>> _quadViewsSetters = [];
    private bool _quadViewsLoading;
    private bool _quadViewsBackedUp;

    private void BuildQuadViewsUi()
    {
        QuadViewsNotice.Visibility = Visibility.Collapsed;
        PanelQuadViews.Children.Add(new TextBlock { Text = "Quad Views", Style = (Style)FindResource("SectionTitle") });
        PanelQuadViews.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 6),
            Text = "Quad-Views-Foveated's own settings: how big and how sharp the region that follows your eyes is. They are read when a game starts, " +
                   "so click Apply and restart the game to see a change. The sliders work like TallyMouse's QuadViews Companion and edit the same file, " +
                   "with one difference: values are also written where every OpenXR runtime reads them, not only under a list of known headsets.",
        });

        foreach (var setting in QuadViewsSetting.All)
        {
            var row = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
            if (setting.IsToggle)
            {
                var box = new CheckBox { Content = setting.Label, FontWeight = FontWeights.SemiBold };
                box.Checked += (_, _) => OnQuadViewsChanged(setting.Id, 1);
                box.Unchecked += (_, _) => OnQuadViewsChanged(setting.Id, 0);
                _quadViewsSetters[setting.Id] = v => box.IsChecked = v != 0;
                row.Children.Add(box);
            }
            else
            {
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var valueText = new TextBlock { Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(valueText, 1);
                header.Children.Add(new TextBlock { Text = setting.Label, FontWeight = FontWeights.SemiBold });
                header.Children.Add(valueText);

                var slider = new Slider
                {
                    Minimum = setting.Min, Maximum = setting.Max, SmallChange = setting.Step, LargeChange = setting.Step * 5,
                    TickFrequency = setting.Step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 4, 0, 0),
                };
                slider.ValueChanged += (_, e) =>
                {
                    valueText.Text = $"{e.NewValue:0} {setting.Suffix}";
                    OnQuadViewsChanged(setting.Id, e.NewValue);
                };
                _quadViewsSetters[setting.Id] = v =>
                {
                    slider.Value = Math.Clamp(Math.Round(v / setting.Step) * setting.Step, setting.Min, setting.Max);
                    valueText.Text = $"{slider.Value:0} {setting.Suffix}";
                };
                row.Children.Add(header);
                row.Children.Add(slider);
            }
            row.Children.Add(new TextBlock { Text = setting.Description, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
            PanelQuadViews.Children.Add(row);
        }

        PanelQuadViews.Children.Add(new TextBlock { Text = "Presets", Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 16, 0, 4) });
        var presets = new WrapPanel();
        foreach (var preset in QuadViewsSetting.Presets)
        {
            var button = new Button { Content = preset.Name, ToolTip = preset.Description, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) =>
            {
                foreach (var (id, value) in preset.Values.Count > 0 ? preset.Values : QuadViewsLayerDefaults())
                {
                    _quadViewsValues[id] = value;
                    _quadViewsDirty.Add(id);
                    _quadViewsLoading = true;
                    _quadViewsSetters[id](value);
                    _quadViewsLoading = false;
                }
                RefreshQuadViewsReadout();
            };
            presets.Children.Add(button);
        }
        PanelQuadViews.Children.Add(presets);

        // Pick up what another tool (the Companion, a text editor) saved - when the tab is opened, not on a timer.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
            var showing = ReferenceEquals(Tabs.SelectedItem, QuadViewsTab);
            // The ring's preview and presets have nothing to do with this tab: give it the whole window.
            RingPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(Tabs, showing ? 2 : 1);
            Tabs.Margin = showing ? new Thickness(10) : new Thickness(10, 10, 0, 10);
            if (showing && _quadViewsDirty.Count == 0) LoadQuadViews();
        };
    }

    /// <summary>Slider values for "QV defaults": the shipped settings file alone, as the layer reads it for this runtime.</summary>
    private Dictionary<string, double> QuadViewsLayerDefaults()
    {
        var files = new List<(string, QuadViewsFile)>();
        var shipped = _quadViewsSession?.ConfigPaths.FirstOrDefault(p => !string.Equals(p, _quadViewsPath, StringComparison.OrdinalIgnoreCase));
        if (shipped != null && File.Exists(shipped))
        {
            try { files.Add(("the file shipped with the layer", QuadViewsFile.Load(shipped))); } catch { /* unreadable: the built-in values remain */ }
        }
        var defaults = QuadViewsFile.Evaluate(files, _quadViewsSession);
        return QuadViewsSetting.All.Where(s => !s.IsToggle).ToDictionary(s => s.Id, s => s.ToSlider(defaults[s.Keys[0]]));
    }

    private void OnQuadViewsChanged(string id, double value)
    {
        if (_quadViewsLoading) return;
        _quadViewsValues[id] = value;
        _quadViewsDirty.Add(id);
        RefreshQuadViewsReadout();
    }

    private void LoadQuadViews()
    {
        _quadViewsSession = QuadViewsSession.Read(QuadViewsSession.LogPath);
        _quadViewsFile = QuadViewsFile.Load(_quadViewsPath);
        _quadViewsDirty.Clear();

        var effective = EvaluateQuadViews(_quadViewsFile);
        var common = EvaluateQuadViews(_quadViewsFile, commonOnly: true);
        _quadViewsLoading = true;
        foreach (var setting in QuadViewsSetting.All)
        {
            // What the file says it wants; where it says nothing, what the layer falls back to.
            var key = setting.Keys[0];
            var fileValue = ParseQuadViewsValue(key, setting.CommonOnly ? _quadViewsFile.InCommon(key) : _quadViewsFile.Intended(key))
                            ?? (setting.CommonOnly ? common[key] : effective[key]);
            var shown = setting.IsToggle ? fileValue : setting.ToSlider(fileValue);
            _quadViewsValues[setting.Id] = shown;
            _quadViewsSetters[setting.Id](shown);
        }
        _quadViewsLoading = false;

        QuadViewsStatus.Text = _quadViewsFile.Existed
            ? "Loaded " + _quadViewsPath
            : "There is no settings file yet, so the game uses Quad-Views-Foveated's defaults. Apply creates one.";
        RefreshQuadViewsReadout();
    }

    private static double? ParseQuadViewsValue(string key, string? text) =>
        text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? QuadViewsFile.Clamp(key, number) : null;

    private QuadViewsFile PlannedQuadViewsFile() => QuadViewsFile.Plan(_quadViewsFile, _quadViewsValues, _quadViewsDirty);

    private QuadViewsEffective EvaluateQuadViews(QuadViewsFile userFile, bool commonOnly = false)
    {
        var files = new List<(string, QuadViewsFile)>();
        // The file shipped with the layer is read first; the log says where it is.
        var shipped = _quadViewsSession?.ConfigPaths.FirstOrDefault(p => !string.Equals(p, _quadViewsPath, StringComparison.OrdinalIgnoreCase));
        if (shipped != null && File.Exists(shipped))
        {
            try { files.Add(("the file shipped with the layer", QuadViewsFile.Load(shipped))); } catch { /* unreadable: evaluate without it */ }
        }
        files.Add(("your settings file", userFile));
        return QuadViewsFile.Evaluate(files, commonOnly ? null : _quadViewsSession);
    }

    private QuadViewsPixels.Result? QuadViewsPixelsFor(QuadViewsEffective effective)
    {
        if (_quadViewsSession is not { HasResolution: true } session) return null;
        var (horizontal, vertical) = QuadViewsSections(effective);
        return QuadViewsPixels.Compute(session.StereoWidth, session.StereoHeight, effective["peripheral_multiplier"], effective["focus_multiplier"], horizontal, vertical);
    }

    /// <summary>With eye tracking the layer sizes the sharp region from the focus keys, without it from the fixed ones.</summary>
    private (double Horizontal, double Vertical) QuadViewsSections(QuadViewsEffective effective) =>
        _quadViewsSession?.EyeTracking == false
            ? (effective["horizontal_fixed_section"], effective["vertical_fixed_section"])
            : (effective["horizontal_focus_section"], effective["vertical_focus_section"]);

    private void RefreshQuadViewsReadout()
    {
        var session = _quadViewsSession;
        var saved = EvaluateQuadViews(_quadViewsFile);
        var planned = PlannedQuadViewsFile();
        var next = EvaluateQuadViews(planned);
        var culture = CultureInfo.CurrentCulture;

        QuadViewsApply.IsEnabled = planned.Text != _quadViewsFile.Text;
        QuadViewsRevert.IsEnabled = _quadViewsDirty.Count > 0;

        // ---- render load
        if (session is { HasResolution: true })
        {
            QuadViewsLoadLast.Text = session.QuadPixels is { } last
                ? $"Last game start ({session.When:g}): {last.ToString("N0", culture)} pixels per frame - {100.0 * last / session.StereoPixels:0.0} % of the full {session.StereoWidth}x{session.StereoHeight} per eye. " +
                  $"Sharp region {session.FocusResolution}, surround {session.PeripheralResolution}. Runtime: {session.RuntimeName}."
                : $"Last game start ({session.When:g}): the game did not use quad views. Full resolution is {session.StereoWidth}x{session.StereoHeight} per eye.";
            var savedPixels = QuadViewsPixelsFor(saved)!;
            var nextPixels = QuadViewsPixelsFor(next)!;
            QuadViewsLoadSaved.Text = $"Saved settings: {savedPixels.Total.ToString("N0", culture)} pixels per frame ({100.0 * savedPixels.Total / session.StereoPixels:0.0} % of full).";
            var change = 100.0 * (nextPixels.Total - savedPixels.Total) / savedPixels.Total;
            QuadViewsLoadNext.Text = $"After Apply: {nextPixels.Total.ToString("N0", culture)} pixels per frame ({100.0 * nextPixels.Total / session.StereoPixels:0.0} % of full) - " +
                                     $"sharp region {nextPixels.FocusWidth}x{nextPixels.FocusHeight}, surround {nextPixels.PeripheralWidth}x{nextPixels.PeripheralHeight}" +
                                     (Math.Abs(change) < 0.05 ? ". No change." : $". {(change > 0 ? "+" : "")}{change:0.0} % pixels compared to saved.");
            QuadViewsLoadNext.Foreground = Math.Abs(change) < 0.05 ? (Brush)FindResource("Muted") : new SolidColorBrush(change > 0 ? Colors.Orange : Colors.MediumSeaGreen);
        }
        else
        {
            double Share(QuadViewsEffective e) { var (h, v) = QuadViewsSections(e); return 100 * QuadViewsPixels.Fraction(e["peripheral_multiplier"], e["focus_multiplier"], h, v); }
            QuadViewsLoadLast.Text = "Quad-Views-Foveated has not logged a game start yet, so your headset's resolution is not known. Run the game once to see real pixel counts here.";
            QuadViewsLoadSaved.Text = $"Saved settings: about {Share(saved):0.0} % of the full-resolution pixel count.";
            QuadViewsLoadNext.Text = $"After Apply: about {Share(next):0.0} %.";
            QuadViewsLoadNext.Foreground = (Brush)FindResource("Muted");
        }

        // ---- settings the file sets, but not for this runtime
        var problems = new List<string>();
        if (session != null)
        {
            foreach (var setting in QuadViewsSetting.All.Where(s => !s.IsToggle))
            {
                var key = session.EyeTracking == false && setting.Keys.Length > 1 ? setting.Keys[1] : setting.Keys[0];
                if (ParseQuadViewsValue(key, _quadViewsFile.Intended(key)) is not { } wanted || Math.Abs(wanted - saved[key]) < 0.0005) continue;
                var source = saved.DecidedBy.TryGetValue(key, out var decidedBy) ? decidedBy : "the layer's built-in value";
                problems.Add($"{setting.Label}: the file says {setting.ToSlider(wanted):0} %, the game gets {setting.ToSlider(saved[key]):0} % (from {source}).");
            }
        }
        var turboNote = session != null && _quadViewsValues.GetValueOrDefault("turbo") != 0 && next["turbo_mode"] == 0 && next.DecidedBy.TryGetValue("turbo_mode", out var turboBy)
            ? $"Turbo mode stays off with your runtime: {turboBy} switches it off on purpose." : null;

        if (problems.Count > 0)
        {
            QuadViewsNoticeText.Text =
                $"Some settings in the file do not reach your game. They are only written under headset sections, and none of those match your OpenXR runtime (\"{session!.RuntimeName}\"):\n  - " +
                string.Join("\n  - ", problems) + "\nApply also writes them to the common part of the file, which every runtime reads." +
                (turboNote != null ? "\n" + turboNote : "");
            QuadViewsNotice.Visibility = Visibility.Visible;
        }
        else if (turboNote != null)
        {
            QuadViewsNoticeText.Text = turboNote;
            QuadViewsNotice.Visibility = Visibility.Visible;
        }
        else
        {
            QuadViewsNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void OnQuadViewsApply(object sender, RoutedEventArgs e)
    {
        try
        {
            // Somebody else may have saved the file since it was loaded: apply the changes to what is there now.
            _quadViewsFile = QuadViewsFile.Load(_quadViewsPath);
            var planned = PlannedQuadViewsFile();

            string? backup = null;
            if (_quadViewsFile.Existed && !_quadViewsBackedUp)
            {
                backup = Path.Combine(QuadViewsSession.Folder, $"settings.before-gaze-overlay-{DateTime.Now:yyyyMMdd-HHmmss}.cfg");
                File.Copy(_quadViewsPath, backup, overwrite: false);
                _quadViewsBackedUp = true;
            }
            planned.Save(_quadViewsPath);
            LoadQuadViews();
            QuadViewsStatus.Text = $"Saved at {DateTime.Now:T}. It takes effect the next time the game starts." +
                                   (backup != null ? $" Your previous file was kept as {Path.GetFileName(backup)}." : "");
        }
        catch (Exception error)
        {
            QuadViewsStatus.Text = "Could not save: " + error.Message;
        }
    }

    private void OnQuadViewsRevert(object sender, RoutedEventArgs e) => LoadQuadViews();

    private void OnQuadViewsOpenFile(object sender, RoutedEventArgs e) => OpenInNotepad(_quadViewsPath);

    private void OnQuadViewsOpenLog(object sender, RoutedEventArgs e) => OpenInNotepad(QuadViewsSession.LogPath);

    private void OpenInNotepad(string path)
    {
        if (!File.Exists(path))
        {
            QuadViewsStatus.Text = "That file does not exist yet: " + path;
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
