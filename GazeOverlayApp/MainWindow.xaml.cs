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
        BuildCropUi();
        LoadQuadViews();
        AboutText.Text =
            "QuadViews Gaze Mirror - unofficial combined fork: foveated rendering (quad views) plus an OBS mirror that shows where you look.\n\n" +
            "It is made of two modified OpenXR API layers:\n" +
            "  - Quad-Views-Foveated by Matthieu Bucchianeri (MIT licence). Change: it also publishes where your eyes are looking.\n" +
            "  - OpenXR-Layer-OBSMirror by Jabbah (MIT licence). Changes: it draws the gaze indicator on the mirror image, and fixes a texture leak.\n" +
            "The OBS plugin (win-openxr) is the unmodified one from OpenXR-Layer-OBSMirror.\n\n" +
            "These are not the authors' official releases - please do not ask them for support with this build. " +
            "The licence texts are installed next to the layers (in the install folder under Program Files).\n\n" +
            "Nothing here is code-signed, so Windows may warn about the installer, and games with anti-cheat may refuse to load the layers.\n\n" +
            "The Quad Views tab edits the same settings as QuadViews Companion by TallyMouse, and follows its slider logic so both can be used on the same file. " +
            "It is not TallyMouse's app and is not endorsed by them.\n\n" +
            "This app only edits the two settings files below and never installs anything. It never runs with administrator rights either: " +
            "switching a layer on or off on the Status tab makes Windows ask for permission and lets Windows' own reg.exe change that one value.\n\n" +
            "Ring settings:\n  " + _configPath + "\n" +
            "Quad-Views-Foveated settings:\n  " + QuadViewsFile.DefaultPath;

        InitializeUpdateUi();
        LoadConfig();
        RefreshStatus();
        StartWatchingConfigFile();
        Tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, Tabs)) { UpdateLiveTimer(); UpdatePanels(); } };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePanels); // after the layout pass that raised it
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

        var slots = BuildSlotBar(_appSettings.RingSlots, CaptureRingSlot,
            values => ApplyValues(values.Where(kv => Settings.All.Any(d => d.Key == kv.Key && IsRingSlotKey(d))).ToDictionary(kv => kv.Key, kv => kv.Value)),
            DescribeRingSlot);
        slots.Margin = new Thickness(14, 0, 0, 0);
        bar.Children.Add(slots);
        return bar;
    }

    // ---- the user's own preset slots (kept in the app's own app.json, not in any settings file a layer reads)

    /// <summary>
    /// "Mine: [1] [2] [3] [Save]". A slot button applies what was saved in it; Save opens a menu to store the current values
    /// in a slot (or clear one). The tooltip of a filled slot says what is in it.
    /// </summary>
    private FrameworkElement BuildSlotBar(SavedPreset?[] slots, Func<Dictionary<string, string>> capture,
        Action<Dictionary<string, string>> apply, Func<Dictionary<string, string>, string> describe)
    {
        var bar = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new TextBlock { Text = "Mine", Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var buttons = new Button[slots.Length];
        var save = new Button { Content = "Save", Margin = new Thickness(2, 2, 0, 2), ToolTip = "Keep the current values in one of your slots" };

        void Refresh()
        {
            for (var i = 0; i < slots.Length; i++)
            {
                buttons[i].IsEnabled = slots[i] != null;
                buttons[i].ToolTip = slots[i] is { } filled
                    ? WrappedToolTip($"Your slot {i + 1}, saved {filled.SavedAt:g}\n\n{describe(filled.Values)}")
                    : WrappedToolTip($"Slot {i + 1} is empty. Use Save to keep the current values here.");
                ToolTipService.SetShowOnDisabled(buttons[i], true);
                ShowToolTipsPatiently(buttons[i]);
            }
        }

        for (var i = 0; i < slots.Length; i++)
        {
            var index = i;
            buttons[i] = new Button { Content = (i + 1).ToString(), MinWidth = 30, Margin = new Thickness(0, 2, 4, 2) };
            buttons[i].Click += (_, _) => { if (slots[index] is { } filled) apply(new Dictionary<string, string>(filled.Values)); };
            bar.Children.Add(buttons[i]);
        }

        save.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = save, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            for (var i = 0; i < slots.Length; i++)
            {
                var index = i;
                var item = new MenuItem { Header = slots[i] is { } filled ? $"Save to slot {i + 1} - replaces what was saved {filled.SavedAt:g}" : $"Save to slot {i + 1} (empty)" };
                item.Click += (_, _) =>
                {
                    slots[index] = new SavedPreset { SavedAt = DateTime.Now, Values = capture() };
                    _appSettings.Save();
                    Refresh();
                };
                menu.Items.Add(item);
            }
            if (slots.Any(s => s != null)) menu.Items.Add(new Separator());
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                var index = i;
                var item = new MenuItem { Header = $"Clear slot {i + 1}" };
                item.Click += (_, _) => { slots[index] = null; _appSettings.Save(); Refresh(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        bar.Children.Add(save);
        Refresh();
        return bar;
    }

    /// <summary>A ring slot holds what the built-in ring presets cover: the Look and Tail tabs, but not the on/off switch.</summary>
    private static bool IsRingSlotKey(SettingDef def) => (def.Group == Settings.GroupLook || def.Group == Settings.GroupTail) && def.Key != "enabled";

    private Dictionary<string, string> CaptureRingSlot() =>
        Settings.All.Where(IsRingSlotKey).ToDictionary(d => d.Key, d => _values.GetValueOrDefault(d.Key) ?? d.Default);

    private static string DescribeRingSlot(Dictionary<string, string> values) =>
        string.Join("\n", Settings.All.Where(d => IsRingSlotKey(d) && values.ContainsKey(d.Key)).Select(d =>
            d.Kind == SettingKind.Slider ? $"{d.Label}: {d.Format(Settings.ParseDouble(values[d.Key], 0))}"
            : d.Kind == SettingKind.Choice ? $"{d.Label}: {d.Choices.FirstOrDefault(c => c.Value == values[d.Key])?.Label ?? values[d.Key]}"
            : $"{d.Label}: {values[d.Key]}"));

    // ---- layout: the preview and the ring's save bar step aside when they are not wanted or there is no room

    private const double PreviewNeedsWindowWidth = 760;

    private void UpdatePanels()
    {
        var quadViews = ReferenceEquals(Tabs.SelectedItem, QuadViewsTab);
        var roomForPreview = ActualWidth >= PreviewNeedsWindowWidth;
        // The preview only means something next to the ring's own settings (Look, Tail, Motion, Placement).
        var ringTab = Tabs.SelectedItem is TabItem { Content: ScrollViewer { Content: StackPanel panel } } &&
                      (ReferenceEquals(panel, PanelLook) || ReferenceEquals(panel, PanelTail) || ReferenceEquals(panel, PanelMotion) || ReferenceEquals(panel, PanelPlacement));
        var showPreview = ringTab && PreviewToggle.IsChecked == true && roomForPreview;
        RingPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        // Set the column too: a Grid does not always re-measure an Auto column when its only child collapses during a layout pass.
        PreviewColumn.Width = showPreview ? GridLength.Auto : new GridLength(0);
        (Tabs.Parent as UIElement)?.InvalidateMeasure();
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
            // Ring settings only: the crop box belongs to the OBS scene, not to the look of the ring.
            var cropKeys = Settings.All.Where(d => d.Group == Settings.GroupCrop).Select(d => d.Key).ToHashSet();
            ApplyValues(Settings.Defaults().Where(kv => !cropKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
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
                "OBS Mirror is above Quad-Views-Foveated, so the ring cannot work. Use \"Fix order\" under the layer list below.", Colors.Firebrick);
        }

        BuildLayerRows(status.Layers);
    }

    // ---- the small layers tool: what is registered, in load order; on/off, move up/down, and an order check

    /// <summary>An order the user has arranged but not applied yet (per list), so several moves cost one permission prompt.</summary>
    private List<LayerEntry>? _pendingMachineOrder, _pendingUserOrder;

    private void BuildLayerRows(IReadOnlyList<LayerEntry> machineLayers)
    {
        LayerRows.Children.Clear();
        List<LayerEntry> userLayers;
        try { userLayers = LayerStatus.ReadLayers(perUser: true); } catch { userLayers = []; }

        // A pending order only survives while it still describes the same entries.
        static List<LayerEntry>? StillValid(List<LayerEntry>? pending, IReadOnlyList<LayerEntry> actual) =>
            pending != null && pending.Select(l => l.JsonPath).Order().SequenceEqual(actual.Select(l => l.JsonPath).Order())
                ? [.. pending.Select(p => actual.First(a => a.JsonPath == p.JsonPath))] : null;
        _pendingMachineOrder = StillValid(_pendingMachineOrder, machineLayers);
        _pendingUserOrder = StillValid(_pendingUserOrder, userLayers);
        var machine = _pendingMachineOrder ?? [.. machineLayers];
        var user = _pendingUserOrder ?? userLayers;
        var pending = _pendingMachineOrder != null || _pendingUserOrder != null;

        if (machine.Count == 0 && user.Count == 0)
        {
            LayerRows.Children.Add(new TextBlock { Text = "No OpenXR API layers are registered.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 6, 0, 6) });
        }

        var position = 1;
        foreach (var group in new[] { machine, user })
        {
            for (var i = 0; i < group.Count; i++)
            {
                LayerRows.Children.Add(BuildLayerRow(group, i, position++, pending));
            }
        }

        // ---- order check, on the order as shown (so it clears up while arranging)
        var shown = machine.Concat(user).ToList();
        var problems = LayerOrder.Problems(shown);
        LayerProblems.Text = problems.Count == 0 ? "" : "Order problem" + (problems.Count > 1 ? "s" : "") + ":\n  - " + string.Join("\n  - ", problems);
        LayerProblems.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var suggestion = problems.Count > 0 ? LayerOrder.Suggest(machine) : null;
        var fixable = suggestion != null && LayerOrder.Problems([.. suggestion, .. user]).Count == 0;
        LayerFix.Visibility = problems.Count > 0 && !pending ? Visibility.Visible : Visibility.Collapsed;
        LayerFix.IsEnabled = fixable;
        LayerFix.ToolTip = fixable ? "Arrange the PC-wide layers so that every known rule holds, moving as few as possible"
            : "No automatic fix: the rules involve a layer registered for this user only, or contradict each other. Arrange them by hand.";
        LayerApply.Visibility = LayerCancel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;

        if (pending) LayerNote.Text = "Order changed but not applied yet. Apply asks Windows for permission once.";
        else if (LayerNote.Text.StartsWith("Order changed", StringComparison.Ordinal) || LayerNote.Text.Length == 0)
        {
            LayerNote.Text = problems.Count == 0 && shown.Count > 1 ? "The order satisfies every rule this app knows about." : "";
        }
    }

    private FrameworkElement BuildLayerRow(List<LayerEntry> group, int index, int position, bool pending)
    {
        var layer = group[index];
        var ours = layer.LayerName is LayerStatus.QuadViewsLayer or LayerStatus.ObsMirrorLayer;
        var box = new CheckBox
        {
            IsChecked = layer.Enabled, VerticalAlignment = VerticalAlignment.Top, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0),
            IsEnabled = !pending, ToolTip = pending ? "Apply or cancel the new order first" : "Switch this layer on or off",
        };
        ToolTipService.SetShowOnDisabled(box, true);

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Opacity = layer.Enabled ? 1 : 0.55 };
        title.Inlines.Add($"{position}. {layer.LayerName ?? "(manifest missing or unreadable)"}");
        if (layer.PerUser) title.Inlines.Add(new System.Windows.Documents.Run("   this user only") { FontWeight = FontWeights.Normal, Foreground = (Brush)FindResource("Muted") });
        if (!layer.Enabled) title.Inlines.Add(new System.Windows.Documents.Run("   off") { FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(Colors.Orange) });
        var text = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = layer.JsonPath, Style = (Style)FindResource("Hint"), FontSize = 11.5 });

        Button Arrow(string glyph, string tip, int offset)
        {
            var target = index + offset;
            var button = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
                Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = target >= 0 && target < group.Count, ToolTip = tip,
            };
            button.Click += (_, _) =>
            {
                var order = group.ToList();
                (order[index], order[target]) = (order[target], order[index]);
                if (layer.PerUser) _pendingUserOrder = order; else _pendingMachineOrder = order;
                RefreshStatus();
            };
            return button;
        }

        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(Arrow("\uE70E", "Move up: closer to the game", -1));
        arrows.Children.Add(Arrow("\uE70D", "Move down: closer to the headset runtime", +1));

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(arrows, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(arrows);
        row.Children.Add(text);

        box.Click += async (_, _) =>
        {
            var enable = box.IsChecked == true;
            box.IsEnabled = false;
            try
            {
                var changed = await LayerStatus.SetEnabledAsync(layer, enable);
                LayerNote.Text = !changed ? "Nothing was changed (the Windows permission prompt was declined)."
                    : $"{LayerOrder.Short(layer)} is now {(enable ? "on" : "off")}. Games pick that up the next time they start." +
                      (ours && !enable ? " The gaze ring needs both of its layers on." : "");
            }
            catch (Exception error)
            {
                LayerNote.Text = "Could not change the layer: " + error.Message;
            }
            RefreshStatus(); // Rebuilds the rows from what the registry really says now.
        };
        return row;
    }

    private async void OnLayerApplyOrder(object sender, RoutedEventArgs e)
    {
        LayerApply.IsEnabled = LayerCancel.IsEnabled = false;
        try
        {
            var done = true;
            if (_pendingUserOrder != null) done &= await LayerOrder.ApplyAsync(perUser: true, _pendingUserOrder);
            if (done && _pendingMachineOrder != null) done &= await LayerOrder.ApplyAsync(perUser: false, _pendingMachineOrder);
            if (done)
            {
                _pendingMachineOrder = _pendingUserOrder = null;
                LayerNote.Text = "New order applied. Games use it the next time they start. (The previous order was saved as a .reg file next to this app's preferences.)";
            }
            else
            {
                LayerNote.Text = "Nothing was changed (the Windows permission prompt was declined). The new order is still waiting.";
            }
        }
        catch (Exception error)
        {
            _pendingMachineOrder = _pendingUserOrder = null;
            LayerNote.Text = "Could not change the order: " + error.Message;
        }
        LayerApply.IsEnabled = LayerCancel.IsEnabled = true;
        var note = LayerNote.Text;
        RefreshStatus();
        LayerNote.Text = note;
    }

    private void OnLayerCancelOrder(object sender, RoutedEventArgs e)
    {
        _pendingMachineOrder = _pendingUserOrder = null;
        LayerNote.Text = "";
        RefreshStatus();
    }

    private void OnLayerFixOrder(object sender, RoutedEventArgs e)
    {
        if (LayerOrder.Suggest(LayerStatus.ReadLayers()) is { } suggestion) _pendingMachineOrder = suggestion;
        RefreshStatus();
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
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
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