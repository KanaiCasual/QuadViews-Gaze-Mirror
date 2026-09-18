using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GazeOverlay;

public partial class MainWindow : Window
{
    private readonly string _configPath = ConfigFile.DefaultPath;
    private Dictionary<string, string> _values = Settings.Defaults();
    private readonly Dictionary<string, Action<string>> _controlSetters = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;
    private readonly HashSet<string> _dirtyKeys = [];
    private DateTime? _seenWriteTime;
    private readonly AppSettings _appSettings = AppSettings.Load();
    private ReleaseInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(); };
        _liveTimer.Tick += (_, _) => { UpdateLive(); WatchConfigFile(); };

        BuildSettingsUi();
        BuildPresets();
        AboutText.Text =
            "OpenXR Gaze Overlay - unofficial community build.\n\n" +
            "It is made of two modified OpenXR API layers:\n" +
            "  - Quad-Views-Foveated by Matthieu Bucchianeri (MIT licence). Change: it also publishes where your eyes are looking.\n" +
            "  - OpenXR-Layer-OBSMirror by Jabbah (MIT licence). Changes: it draws the gaze indicator on the mirror image, and fixes a texture leak.\n" +
            "The OBS plugin (win-openxr) is the unmodified one from OpenXR-Layer-OBSMirror.\n\n" +
            "These are not the authors' official releases - please do not ask them for support with this build. " +
            "The licence texts are installed next to the layers (in the install folder under Program Files).\n\n" +
            "Nothing here is code-signed, so Windows may warn about the installer, and games with anti-cheat may refuse to load the layers.\n\n" +
            "This app only edits the ring settings file below; it never installs anything or asks for administrator rights.\n\n" +
            "Settings file:\n  " + _configPath;

        InitializeUpdateUi();
        LoadConfig();
        RefreshStatus();
        UpdateLive();
        _liveTimer.Start();
    }

    // ------------------------------------------------------------------ settings UI

    private void BuildSettingsUi()
    {
        var panels = new Dictionary<string, StackPanel>
        {
            [Settings.GroupLook] = PanelLook, [Settings.GroupTail] = PanelTail,
            [Settings.GroupMotion] = PanelMotion, [Settings.GroupPlacement] = PanelPlacement,
        };

        foreach (var (group, panel) in panels)
        {
            panel.Children.Add(new TextBlock { Text = group, Style = (Style)FindResource("SectionTitle") });
            panel.Children.Add(new TextBlock { Text = Settings.GroupIntro[group], Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 10) });
            foreach (var def in Settings.All.Where(d => d.Group == group))
            {
                panel.Children.Add(BuildRow(def));
            }
        }
    }

    private FrameworkElement BuildRow(SettingDef def)
    {
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = def.Label, FontWeight = FontWeights.SemiBold };
        var valueText = new TextBlock { Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(label);
        header.Children.Add(valueText);

        switch (def.Kind)
        {
            case SettingKind.Toggle:
            {
                var box = new CheckBox { Content = def.Label, FontWeight = FontWeights.SemiBold };
                box.Checked += (_, _) => OnValueChanged(def.Key, "1");
                box.Unchecked += (_, _) => OnValueChanged(def.Key, "0");
                _controlSetters[def.Key] = v => box.IsChecked = v != "0";
                row.Children.Add(box);
                break;
            }
            case SettingKind.Choice:
            {
                var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
                foreach (var choice in def.Choices) combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string value }) OnValueChanged(def.Key, value);
                };
                _controlSetters[def.Key] = v =>
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) ?? combo.Items[0];
                row.Children.Add(header);
                row.Children.Add(combo);
                break;
            }
            case SettingKind.Slider:
            {
                var slider = new Slider
                {
                    Minimum = def.Min, Maximum = def.Max, SmallChange = def.Step, LargeChange = def.Step * 10,
                    TickFrequency = def.Step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 4, 0, 0),
                };
                slider.ValueChanged += (_, e) =>
                {
                    valueText.Text = def.Format(e.NewValue);
                    OnValueChanged(def.Key, def.Serialize(e.NewValue));
                };
                _controlSetters[def.Key] = v =>
                {
                    slider.Value = Math.Clamp(Settings.ParseDouble(v, Settings.ParseDouble(def.Default, def.Min)), def.Min, def.Max);
                    valueText.Text = def.Format(slider.Value);
                };
                row.Children.Add(header);
                row.Children.Add(slider);
                break;
            }
            case SettingKind.Color:
            {
                row.Children.Add(header);
                row.Children.Add(BuildColorEditor(def, valueText));
                break;
            }
        }

        row.Children.Add(new TextBlock { Text = def.Description, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
        return row;
    }

    private FrameworkElement BuildColorEditor(SettingDef def, TextBlock valueText)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var picker = new ColorPicker();

        void Publish(byte r, byte g, byte b)
        {
            var text = $"{r},{g},{b}";
            valueText.Text = text;
            OnValueChanged(def.Key, text);
        }
        picker.ColorChanged += Publish;

        // Quick picks above the full picker.
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var presets = new (string Name, string Value)[]
        {
            ("Sky blue", "40,170,245"), ("Cyan", "0,210,255"), ("White", "255,255,255"), ("Green", "80,255,120"),
            ("Yellow", "255,235,0"), ("Orange", "255,140,0"), ("Red", "255,70,60"), ("Magenta", "235,80,255"),
        };
        foreach (var (name, value) in presets)
        {
            var (r, g, b) = Settings.ParseColor(value);
            var swatch = new Border
            {
                Width = 28, Height = 22, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(4), ToolTip = name,
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)), BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.MouseLeftButtonUp += (_, _) => { picker.SetColor(r, g, b); Publish(r, g, b); };
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches);
        panel.Children.Add(picker);

        _controlSetters[def.Key] = value =>
        {
            var (r, g, b) = Settings.ParseColor(value);
            picker.SetColor(r, g, b);
            valueText.Text = $"{r},{g},{b}";
        };
        return panel;
    }

    private void BuildPresets()
    {
        foreach (var preset in Settings.Presets)
        {
            var button = new Button { Content = preset.Name, ToolTip = preset.Description, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => ApplyValues(preset.Values);
            PresetButtons.Children.Add(button);
        }
    }

    private void ApplyValues(IReadOnlyDictionary<string, string> changes)
    {
        _loading = true;
        foreach (var (key, value) in changes)
        {
            _values[key] = value;
            _dirtyKeys.Add(key);
            if (_controlSetters.TryGetValue(key, out var setter)) setter(value);
        }
        _loading = false;
        _previewTimer.Stop(); _previewTimer.Start();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _values[key] = value;
        _dirtyKeys.Add(key);
        if (key == "headset_marker") RefreshStatus();
        if (!_previewTimer.IsEnabled) _previewTimer.Start();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    // ------------------------------------------------------------------ config file

    private void LoadConfig()
    {
        var existed = File.Exists(_configPath);
        _loading = true;
        _values = ConfigFile.Load(_configPath);
        foreach (var (key, setter) in _controlSetters) setter(_values.GetValueOrDefault(key) ?? "");
        _loading = false;
        _dirtyKeys.Clear();
        _seenWriteTime = existed ? File.GetLastWriteTimeUtc(_configPath) : null;
        SaveStatus.Text = existed
            ? "Loaded your current settings. Changes save automatically and reach a running game within about a second."
            : "No settings file yet - showing the defaults. It is created as soon as you change something (or install).";
        RenderPreview();
    }

    /// <summary>
    /// The layer writes to this file too (Ctrl+Alt+arrow nudges in game), and so can a text editor. Anything changed on
    /// disk that the user has not just changed here wins, so this app never writes stale values back over it.
    /// </summary>
    private bool MergeFromDisk()
    {
        if (!File.Exists(_configPath)) return false;
        var changed = false;
        _loading = true;
        foreach (var (key, value) in ConfigFile.Load(_configPath))
        {
            if (_dirtyKeys.Contains(key) || _values.GetValueOrDefault(key) == value) continue;
            _values[key] = value;
            if (_controlSetters.TryGetValue(key, out var setter)) setter(value);
            changed = true;
        }
        _loading = false;
        return changed;
    }

    private void SaveNow()
    {
        try
        {
            MergeFromDisk();
            ConfigFile.Save(_configPath, _values);
            _dirtyKeys.Clear();
            _seenWriteTime = File.GetLastWriteTimeUtc(_configPath);
            SaveStatus.Text = $"Saved at {DateTime.Now:T}. A running game picks it up within about a second.";
            RenderPreview();
        }
        catch (Exception e)
        {
            SaveStatus.Text = "Could not save: " + e.Message;
        }
    }

    /// <summary>Picks up changes made outside this app (in-game nudging, a text editor) about once a second.</summary>
    private void WatchConfigFile()
    {
        try
        {
            if (_saveTimer.IsEnabled || !File.Exists(_configPath)) return;
            var writeTime = File.GetLastWriteTimeUtc(_configPath);
            if (writeTime == _seenWriteTime) return;
            _seenWriteTime = writeTime;
            if (MergeFromDisk())
            {
                SaveStatus.Text = $"Picked up a change made outside this app at {DateTime.Now:T}.";
                RenderPreview();
            }
        }
        catch
        {
            // The file may be mid-write; try again on the next tick.
        }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset every setting to its default?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            ApplyValues(Settings.Defaults());
        }
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_configPath)) SaveNow();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_configPath}\"") { UseShellExecute = true });
    }

    private void OnReloadConfig(object sender, RoutedEventArgs e) => LoadConfig();

    // ------------------------------------------------------------------ preview

    private void OnPreviewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RenderPreview();
    }

    private void RenderPreview()
    {
        var background = BgTerrain.IsChecked == true ? PreviewBackground.Terrain
                       : BgSky.IsChecked == true ? PreviewBackground.Sky
                       : BgBlack.IsChecked == true ? PreviewBackground.Black
                       : PreviewBackground.Cockpit;
        PreviewImage.Source = RingPreview.Render(_values, background, PreviewMoving.IsChecked == true, 340);
    }

    // ------------------------------------------------------------------ status tab (read-only)

    private void RefreshStatus()
    {
        SystemStatus status;
        try
        {
            status = LayerStatus.Get();
        }
        catch (Exception e)
        {
            StatusRows.Children.Clear();
            AddStatusRow("Could not read the OpenXR layer setup", e.Message, Colors.Firebrick);
            return;
        }

        StatusRows.Children.Clear();
        AddStatusRow("Quad-Views-Foveated", status.QuadViews.Detail, HealthColor(status.QuadViews.Health));
        AddStatusRow("OpenXR OBS Mirror layer", status.ObsMirror.Detail, HealthColor(status.ObsMirror.Health));
        AddStatusRow("OBS plugin",
            status.ObsPath == null ? "OBS Studio was not found on this PC."
            : status.ObsPluginPresent ? "Present in OBS."
            : $"Not in OBS yet ({status.ObsPath}). Re-run the installer with OBS installed, or use its Repair option.",
            status.ObsPath != null && status.ObsPluginPresent ? Colors.SeaGreen : Colors.Gray);
        if (_values.GetValueOrDefault("headset_marker") == "1")
        {
            AddStatusRow("Calibration is ON",
                "A marker is being drawn inside your headset, and Ctrl+Alt+arrow keys nudge the ring in game. " +
                "Switch it off on the Placement tab when you are done.", Colors.DarkOrange);
        }
        if (status.OrderCorrect == false)
        {
            AddStatusRow("Layer order",
                "OBS Mirror is above Quad-Views-Foveated, so the ring cannot work. Uninstall and reinstall OpenXR Gaze Overlay, or move " +
                "Quad-Views-Foveated above OBS Mirror with the \"OpenXR API Layers\" tool.", Colors.Firebrick);
        }

        LayerList.Text = status.Layers.Count == 0
            ? "(no OpenXR API layers are registered)"
            : string.Join("\n", status.Layers.Select((l, i) => $"{i + 1}. [{(l.Enabled ? "on " : "off")}] {l.LayerName ?? "(unreadable manifest)"}\n      {l.JsonPath}"));
    }

    private static Color HealthColor(LayerHealth health) => health switch
    {
        LayerHealth.GazeBuild => Colors.SeaGreen,
        LayerHealth.OriginalWithoutGaze => Colors.DarkOrange,
        LayerHealth.Duplicate => Colors.Firebrick,
        _ => Colors.Gray,
    };

    private void AddStatusRow(string title, string detail, Color color)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Hint") });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        StatusRows.Children.Add(row);
    }

    private void UpdateLive()
    {
        var (state, text) = GazeLive.Read();
        LiveText.Text = text;
        LiveDot.Fill = new SolidColorBrush(state switch
        {
            GazeLiveState.Tracking => Colors.SeaGreen,
            GazeLiveState.EyesNotTracked or GazeLiveState.Stale => Colors.DarkOrange,
            _ => Colors.Gray,
        });
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        UpdateLive();
    }
    // ------------------------------------------------------------------ updates (notify only - never downloads)

    private void InitializeUpdateUi()
    {
        VersionText.Text = "Version " + UpdateChecker.CurrentVersionText;
        _loading = true;
        CheckUpdatesBox.IsChecked = _appSettings.CheckForUpdates;
        IncludeBetasBox.IsChecked = _appSettings.IncludeBetas;
        _loading = false;

        if (!UpdateChecker.IsConfigured)
        {
            // Builds made before the project has a public home carry no repository name.
            CheckUpdatesBox.IsEnabled = IncludeBetasBox.IsEnabled = CheckNowButton.IsEnabled = ReleasesButton.IsEnabled = false;
            UpdateStatus.Text = "Update checks are not available in this build (it was not built with a release page to look at).";
            return;
        }
        UpdateStatus.Text = "Looks at " + UpdateChecker.ReleasesPage;
        if (_appSettings.CheckForUpdates) _ = CheckForUpdatesAsync(userAsked: false);
    }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (!UpdateChecker.IsConfigured) return;
        CheckNowButton.IsEnabled = false;
        if (userAsked) UpdateStatus.Text = "Checking...";
        try
        {
            var update = await UpdateChecker.CheckAsync(UpdateChecker.Repository, _appSettings.IncludeBetas);
            if (update == null)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                UpdateStatus.Text = $"You have the latest version ({UpdateChecker.CurrentVersionText}). Checked at {DateTime.Now:t}.";
            }
            else
            {
                UpdateStatus.Text = $"Version {update.Version.ToString(3)}{(update.IsBeta ? " (beta)" : "")} is available. Checked at {DateTime.Now:t}.";
                // A skipped version stays quiet on start-up, but "Check now" always shows what it found.
                if (userAsked || _appSettings.SkippedVersion != update.Version.ToString(3)) ShowUpdate(update);
            }
        }
        catch (Exception e)
        {
            // Offline, rate-limited, no releases yet... never worth a pop-up. Only say so if the user asked.
            if (userAsked) UpdateStatus.Text = "Could not check for updates: " + e.Message;
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private void ShowUpdate(ReleaseInfo update)
    {
        _availableUpdate = update;
        UpdateTitle.Text = update.IsBeta
            ? $"A beta version is available: {update.Version.ToString(3)}"
            : $"A new version is available: {update.Version.ToString(3)}";
        UpdateDetail.Text = $"You have {UpdateChecker.CurrentVersionText}. \"{update.Title}\" - download the .msi from the release page and run it; it upgrades in place.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private static void OpenInBrowser(string url)
    {
        // Only ever this project's own pages on github.com.
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser: nothing to do */ }
    }

    private void OnViewUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate != null) OpenInBrowser(_availableUpdate.Url);
    }

    private void OnSkipUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate != null)
        {
            _appSettings.SkippedVersion = _availableUpdate.Version.ToString(3);
            _appSettings.Save();
        }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnDismissUpdate(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void OnUpdatePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _appSettings.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
        _appSettings.IncludeBetas = IncludeBetasBox.IsChecked == true;
        _appSettings.Save();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userAsked: true);

    private void OnOpenReleases(object sender, RoutedEventArgs e) => OpenInBrowser(UpdateChecker.ReleasesPage);
}
