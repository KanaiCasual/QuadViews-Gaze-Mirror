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
    private FileSystemWatcher? _configWatcher;
    private DateTime _ownWriteUntil = DateTime.MinValue;
    private readonly DispatcherTimer _outsideChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly AppSettings _appSettings = AppSettings.Load();
    private ReleaseInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(); };
        _liveTimer.Tick += (_, _) => UpdateLive();
        _outsideChangeTimer.Tick += (_, _) => { _outsideChangeTimer.Stop(); ApplyOutsideChange(); };

        RestoreWindowPlacement();
        BuildSettingsUi();
        BuildQuadViewsUi();
        LoadQuadViews();
        AboutText.Text =
            "OpenXR Gaze Overlay - unofficial community build.\n\n" +
            "It is made of two modified OpenXR API layers:\n" +
            "  - Quad-Views-Foveated by Matthieu Bucchianeri (MIT licence). Change: it also publishes where your eyes are looking.\n" +
            "  - OpenXR-Layer-OBSMirror by Jabbah (MIT licence). Changes: it draws the gaze indicator on the mirror image, and fixes a texture leak.\n" +
            "The OBS plugin (win-openxr) is the unmodified one from OpenXR-Layer-OBSMirror.\n\n" +
            "These are not the authors' official releases - please do not ask them for support with this build. " +
            "The licence texts are installed next to the layers (in the install folder under Program Files).\n\n" +
            "Nothing here is code-signed, so Windows may warn about the installer, and games with anti-cheat may refuse to load the layers.\n\n" +
            "The Quad Views tab edits the same settings as QuadViews Companion by TallyMouse, and follows its slider logic so both can be used on the same file. " +
            "It is not TallyMouse's app and is not endorsed by them.\n\n" +
            "This app only edits the two settings files below; it never installs anything or asks for administrator rights.\n\n" +
            "Ring settings:\n  " + _configPath + "\n" +
            "Quad-Views-Foveated settings:\n  " + QuadViewsFile.DefaultPath;

        InitializeUpdateUi();
        LoadConfig();
        RefreshStatus();
        StartWatchingConfigFile();
        Tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, Tabs)) { UpdateLiveTimer(); UpdatePanels(); } };
        SizeChanged += (_, _) => UpdatePanels();
        Loaded += (_, _) => UpdatePanels();
        Closing += (_, _) => SaveWindowPlacement();
        Activated += (_, _) => UpdateLiveTimer();
        Deactivated += (_, _) => UpdateLiveTimer();
        UpdateLiveTimer();
    }

    /// <summary>The once-a-second gaze light only ticks while it can be seen: Status tab showing and the window active.</summary>
    private void UpdateLiveTimer()
    {
        var visible = Tabs.SelectedIndex == 0 && IsActive;
        if (visible == _liveTimer.IsEnabled) return;
        if (visible) { UpdateLive(); _liveTimer.Start(); } else { _liveTimer.Stop(); }
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
            panel.Children.Add(new TextBlock { Text = Settings.GroupIntro[group], Style = (Style)FindResource("Hint"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
            if (group == Settings.GroupLook) panel.Children.Add(BuildPresets());
            foreach (var def in Settings.All.Where(d => d.Group == group))
            {
                panel.Children.Add(BuildRow(def));
            }
        }
    }

    // ---- compact rows: label | editor | value | (i). The explanation lives in the tooltip, so a tab fits without scrolling.

    private const double RowLabelWidth = 172, RowValueWidth = 104;

    private static ToolTip WrappedToolTip(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 } };

    private static void ShowToolTipsPatiently(DependencyObject element)
    {
        ToolTipService.SetInitialShowDelay(element, 250);
        ToolTipService.SetShowDuration(element, 60000);
    }

    /// <summary>One settings row. The editor goes in column 1 (or 1-2 when there is no value text); column 3 is the info mark.</summary>
    private Grid NewRow(string description)
    {
        var row = new Grid { MinHeight = 30, Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowLabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowValueWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

        var info = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
            Foreground = (Brush)FindResource("Muted"), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Help,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = WrappedToolTip(description),
        };
        ShowToolTipsPatiently(info);
        Grid.SetColumn(info, 3);
        row.Children.Add(info);
        return row;
    }

    private TextBlock AddRowLabel(Grid row, string label, string description)
    {
        var text = new TextBlock
        {
            Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), ToolTip = WrappedToolTip(label + "\n\n" + description),
        };
        ShowToolTipsPatiently(text);
        row.Children.Add(text);
        return text;
    }

    private TextBlock AddRowValue(Grid row)
    {
        var value = new TextBlock
        {
            Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return value;
    }

    private static void AddRowEditor(Grid row, FrameworkElement editor, int column = 1, int span = 1)
    {
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, column);
        Grid.SetColumnSpan(editor, span);
        row.Children.Add(editor);
    }

    private FrameworkElement BuildRow(SettingDef def)
    {
        var row = NewRow(def.Description);
        switch (def.Kind)
        {
            case SettingKind.Toggle:
            {
                var box = new CheckBox { Content = def.Label, FontWeight = FontWeights.SemiBold, ToolTip = WrappedToolTip(def.Description) };
                ShowToolTipsPatiently(box);
                box.Checked += (_, _) => OnValueChanged(def.Key, "1");
                box.Unchecked += (_, _) => OnValueChanged(def.Key, "0");
                _controlSetters[def.Key] = v => box.IsChecked = v != "0";
                AddRowEditor(row, box, column: 0, span: 3);
                return row;
            }
            case SettingKind.Choice:
            {
                AddRowLabel(row, def.Label, def.Description);
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var choice in def.Choices) combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string value }) OnValueChanged(def.Key, value);
                };
                _controlSetters[def.Key] = v =>
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) ?? combo.Items[0];
                AddRowEditor(row, combo, column: 1, span: 2);
                return row;
            }
            case SettingKind.Slider:
            {
                AddRowLabel(row, def.Label, def.Description);
                var valueText = AddRowValue(row);
                var slider = new Slider
                {
                    Minimum = def.Min, Maximum = def.Max, SmallChange = def.Step, LargeChange = def.Step * 10,
                    TickFrequency = def.Step, IsSnapToTickEnabled = true,
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
                AddRowEditor(row, slider);
                return row;
            }
            default:
                return BuildColorEditor(def, row);
        }
    }

    /// <summary>Quick picks in the row itself; the full picker is folded away underneath until it is wanted.</summary>
    private FrameworkElement BuildColorEditor(SettingDef def, Grid row)
    {
        AddRowLabel(row, def.Label, def.Description);
        var valueText = AddRowValue(row);
        var picker = new ColorPicker { Margin = new Thickness(0, 6, 0, 0) };

        void Publish(byte r, byte g, byte b)
        {
            var text = $"{r},{g},{b}";
            valueText.Text = text;
            OnValueChanged(def.Key, text);
        }
        picker.ColorChanged += Publish;

        var swatches = new WrapPanel();
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
                Width = 22, Height = 18, Margin = new Thickness(0, 2, 5, 2), CornerRadius = new CornerRadius(4), ToolTip = name,
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)), BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.MouseLeftButtonUp += (_, _) => { picker.SetColor(r, g, b); Publish(r, g, b); };
            swatches.Children.Add(swatch);
        }
        AddRowEditor(row, swatches);

        _controlSetters[def.Key] = value =>
        {
            var (r, g, b) = Settings.ParseColor(value);
            picker.SetColor(r, g, b);
            valueText.Text = $"{r},{g},{b}";
        };
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(new Expander { Header = "Custom colour", Content = picker, Margin = new Thickness(RowLabelWidth, 0, 0, 4) });
        return panel;
    }

    private FrameworkElement BuildPresets()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(new TextBlock { Text = "Presets", Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var preset in Settings.Presets)
        {
            var button = new Button { Content = preset.Name, ToolTip = WrappedToolTip(preset.Description + " Presets leave motion and placement alone."), Margin = new Thickness(0, 2, 6, 2) };
            button.Click += (_, _) => ApplyValues(preset.Values);
            bar.Children.Add(button);
        }
        var reset = new Button { Content = "Reset all", ToolTip = "Reset every ring setting to its default", Margin = new Thickness(8, 2, 0, 2) };
        reset.Click += OnResetAll;
        bar.Children.Add(reset);
        return bar;
    }

    // ---- layout: the preview and the ring's save bar step aside when they are not wanted or there is no room

    private const double PreviewNeedsWindowWidth = 760;

    private void UpdatePanels()
    {
        var quadViews = ReferenceEquals(Tabs.SelectedItem, QuadViewsTab);
        var roomForPreview = ActualWidth >= PreviewNeedsWindowWidth;
        var showPreview = !quadViews && PreviewToggle.IsChecked == true && roomForPreview;
        RingPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Margin = showPreview ? new Thickness(8, 8, 0, 0) : new Thickness(8, 8, 8, 0);
        // The Quad Views tab has its own Apply and status; the ring's save line would only confuse there.
        BottomBar.Visibility = quadViews ? Visibility.Collapsed : Visibility.Visible;
        PreviewToggle.IsEnabled = roomForPreview;
        PreviewToggle.ToolTip = roomForPreview ? "Show the ring preview beside the settings" : "The window is too narrow for the preview - widen it to bring it back";
    }

    private void OnLayoutOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        _appSettings.ShowPreview = PreviewToggle.IsChecked == true;
        _appSettings.Save();
        UpdatePanels();
    }

    private void RestoreWindowPlacement()
    {
        _loading = true;
        PreviewToggle.IsChecked = _appSettings.ShowPreview;
        _loading = false;
        if (App.Headless || _appSettings.WindowWidth < MinWidth || _appSettings.WindowHeight < MinHeight) return;
        // Only if it still lands on a screen (a monitor may have gone since).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var wanted = new Rect(_appSettings.WindowLeft, _appSettings.WindowTop, _appSettings.WindowWidth, _appSettings.WindowHeight);
        var visible = Rect.Intersect(screen, wanted);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 120) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wanted.Left; Top = wanted.Top; Width = wanted.Width; Height = wanted.Height;
    }

    private void SaveWindowPlacement()
    {
        if (App.Headless || WindowState != WindowState.Normal) return;
        _appSettings.WindowLeft = Left; _appSettings.WindowTop = Top; _appSettings.WindowWidth = Width; _appSettings.WindowHeight = Height;
        _appSettings.Save();
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
        SaveStatus.Text = existed
            ? "Loaded your current settings. Changes save automatically and reach a running game instantly."
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
            _ownWriteUntil = DateTime.UtcNow.AddMilliseconds(600);
            ConfigFile.Save(_configPath, _values);
            _dirtyKeys.Clear();
            // The game never polls this file; telling it is what makes the change go live.
            SaveStatus.Text = LiveLink.NotifySettingsChanged()
                ? $"Saved at {DateTime.Now:T} and applied to the running game."
                : $"Saved at {DateTime.Now:T}. No game is running right now - it reads the settings when it starts.";
            RenderPreview();
        }
        catch (Exception e)
        {
            SaveStatus.Text = "Could not save: " + e.Message;
        }
    }

    /// <summary>
    /// Windows tells us when the settings file changes (no polling): that is how in-game calibration nudges show up here
    /// live, and how edits made in a text editor reach a running game while this app is open.
    /// </summary>
    private void StartWatchingConfigFile()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(folder);
            _configWatcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(OnConfigFileChangedOutside);
            _configWatcher.Changed += changed;
            _configWatcher.Created += changed;
            _configWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(OnConfigFileChangedOutside);
        }
        catch
        {
            // Without the watcher the app still works; outside changes then only show after "Reload from file".
        }
    }

    private void OnConfigFileChangedOutside()
    {
        // Our own save triggers the watcher too; so does a save that is about to happen.
        if (DateTime.UtcNow < _ownWriteUntil || _saveTimer.IsEnabled) return;
        _outsideChangeTimer.Stop();
        _outsideChangeTimer.Start(); // Editors write in several steps: wait for the file to settle.
    }

    private void ApplyOutsideChange()
    {
        try
        {
            if (!MergeFromDisk()) return;
            // A text editor changed it (the layer already holds the values it wrote itself): make a running game reload.
            LiveLink.NotifySettingsChanged();
            SaveStatus.Text = $"Picked up a change made outside this app at {DateTime.Now:T}.";
            if (_values.ContainsKey("headset_marker")) RefreshStatus();
            RenderPreview();
        }
        catch
        {
            // The file may still be mid-write; the next change notification tries again.
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
        PreviewImage.Source = RingPreview.Render(_values, background, PreviewMoving.IsChecked == true, 260);
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