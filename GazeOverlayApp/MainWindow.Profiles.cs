using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GazeOverlay;

/// <summary>
/// The 2.0 additions to the Mirror tab: the picture's eye, size cap and rate (settings the mirror reads); crop profiles
/// (saved framings, each optionally tied to a game - the mirror applies the matching one when that game starts); and
/// the SteamVR helper for OpenVR games.
/// </summary>
public partial class MainWindow
{
    /// <summary>One entry of the profile list; Profile is null for "(none)".</summary>
    private sealed record ProfileItem(CropProfile? Profile, string Label);

    /// <summary>One running program with a window, for the game picker.</summary>
    public sealed record RunningApp(string Exe, string Label);

    private List<CropProfile> _profiles = [];
    private CropProfile? _selectedProfile;
    private bool _profileLoading;
    private readonly DispatcherTimer _profileSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private void BuildOutputAndProfilesUi()
    {
        // The three settings the mirror itself reads: ordinary rows, placed here rather than on a tab of their own.
        foreach (var key in new[] { "mirror_eye", "output_max_side", "output_fps" })
        {
            var def = Settings.All.First(d => d.Key == key);
            var row = (Grid)BuildRow(def);
            // In a card a third of the page wide, the label column gives way to the drop-down.
            row.ColumnDefinitions[0].Width = new GridLength(78);
            PanelOutput.Children.Add(row);
        }

        // Crop profiles.
        _profileSaveTimer.Tick += (_, _) => { _profileSaveTimer.Stop(); SaveProfiles(); };
        CropProfile.SelectionChanged += (_, _) => OnProfileSelected();
        CropProfileGame.DropDownOpened += (_, _) => FillGamePicker(filter: "");
        CropProfileGame.SelectionChanged += (_, _) => { if (CropProfileGame.SelectedItem is RunningApp app) { CropProfileGame.Text = app.Exe; OnProfileGameChanged(); } };
        CropProfileGame.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            // Typing: the list narrows to the running programs whose name contains the text.
            if (_profileLoading || CropProfileGame.SelectedItem is RunningApp) return;
            FillGamePicker(CropProfileGame.Text);
            if (CropProfileGame.HasItems && !CropProfileGame.IsDropDownOpen && CropProfileGame.IsKeyboardFocusWithin) CropProfileGame.IsDropDownOpen = true;
        }));
        CropProfileGame.LostKeyboardFocus += (_, _) => { if (!CropProfileGame.IsDropDownOpen) OnProfileGameChanged(); };
        CropProfileGame.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { CropProfileGame.IsDropDownOpen = false; OnProfileGameChanged(); } };
        CropProfileName.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) OnProfileSaveConfirm(this, new RoutedEventArgs());
            if (e.Key == System.Windows.Input.Key.Escape) CropProfileNamePanel.Visibility = Visibility.Collapsed;
        };
        LoadProfiles();

        // The helper: always on. SteamVR starts it with itself once it has been told about it (once, here, on the first
        // start of this app - the installer runs no code), and it is started now in case SteamVR is already running.
        if (!App.Headless) _ = EnsureHelperAsync();
    }

    // ------------------------------------------------------------------ crop profiles

    private void LoadProfiles()
    {
        var selectedName = _selectedProfile?.Name ?? _appSettings.SelectedCropProfile;
        try { _profiles = CropProfiles.Load(); }
        catch (Exception e) { _profiles = []; CropProfileStatus.Text = "The profiles file could not be read: " + e.Message; AppLog.Write("The crop profiles could not be read", e); }
        _selectedProfile = _profiles.FirstOrDefault(p => p.Name == selectedName);
        RefreshProfileList();
    }

    /// <summary>Rebuilds the list from scratch (a fresh ItemsSource, so the drop-down always matches what is there).</summary>
    private void RefreshProfileList()
    {
        _profileLoading = true;
        var items = new List<ProfileItem>();
        // "(none)" is only there while no profile is chosen: once one is, the settings belong to it.
        if (_selectedProfile == null) items.Add(new ProfileItem(null, _profiles.Count == 0 ? "(none yet - save the framing as a profile)" : "(none - the settings as they are)"));
        foreach (var profile in _profiles)
        {
            items.Add(new ProfileItem(profile, profile.Game.Length > 0 ? $"{profile.Name}  ({profile.Game})" : profile.Name));
        }
        CropProfile.ItemsSource = items;
        CropProfile.SelectedItem = items.FirstOrDefault(i => ReferenceEquals(i.Profile, _selectedProfile)) ?? items[0];
        _profileLoading = false;
        RefreshProfileUi();
    }

    private void RefreshProfileUi()
    {
        _profileLoading = true;
        CropProfileDelete.IsEnabled = _selectedProfile != null;
        if (!CropProfileGame.IsKeyboardFocusWithin) CropProfileGame.Text = _selectedProfile?.Game ?? CropProfileGame.Text;
        _profileLoading = false;
        var live = MirrorLive.Read();
        var running = live is { Producing: true } ? live.Program : null;
        CropProfileStatus.Text = _selectedProfile == null
            ? (running != null ? $"Running now: {running}. Save the framing as a profile to have it applied whenever that game starts." : "Save the framing as a profile to have it applied whenever a game starts. \"For game\" can be filled in first.")
            : _selectedProfile.Game.Length == 0 ? "Edits are saved into this profile. Choose or type a game (its program file, for example DCS.exe) to have it applied when that game starts."
            : $"Edits are saved into this profile. It is applied when {_selectedProfile.Game} starts" + (running != null && !string.Equals(running, _selectedProfile.Game, StringComparison.OrdinalIgnoreCase) ? $" (running now: {running})." : ".");
    }

    private void OnProfileSelected()
    {
        if (_profileLoading) return;
        var chosen = CropProfile.SelectedItem is ProfileItem item ? item.Profile : null;
        if (ReferenceEquals(chosen, _selectedProfile)) return;
        _selectedProfile = chosen;
        _appSettings.SelectedCropProfile = _selectedProfile?.Name ?? "";
        _appSettings.Save();
        AppLog.Write(_selectedProfile == null ? "Crop profile: none chosen." : $"Crop profile \"{_selectedProfile.Name}\" chosen (game \"{_selectedProfile.Game}\").");
        if (_selectedProfile != null && _selectedProfile.Values.Count > 0)
        {
            // Show it: its values become the current settings (and reach a running game like any change).
            ApplyValues(_selectedProfile.Values);
            ApplyCropMargin(moveBox: false);
            DrawCrop();
        }
        RefreshProfileList();
    }

    /// <summary>The running programs that have a window (no background processes), for the game picker.</summary>
    private void FillGamePicker(string filter)
    {
        var apps = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                var exe = process.ProcessName + ".exe";
                if (filter.Length > 0 && !exe.Contains(filter, StringComparison.OrdinalIgnoreCase) && !process.MainWindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(exe)) apps.Add(new RunningApp(exe, $"{exe}  -  {process.MainWindowTitle}"));
            }
            catch
            {
                // A process that ended or refuses questions.
            }
            finally
            {
                process.Dispose();
            }
        }
        apps.Sort((a, b) => string.Compare(a.Exe, b.Exe, StringComparison.OrdinalIgnoreCase));
        var text = CropProfileGame.Text;
        _profileLoading = true;
        CropProfileGame.ItemsSource = apps;
        CropProfileGame.Text = text; // Changing the list must not wipe what was typed.
        _profileLoading = false;
    }

    private void OnProfileGameChanged()
    {
        if (_profileLoading || _selectedProfile == null) return;
        var game = CropProfileGame.Text.Trim();
        if (game == _selectedProfile.Game) return;
        _selectedProfile.Game = game;
        SaveProfiles();
        RefreshProfileList();
    }

    /// <summary>A crop key changed while a profile is selected: the profile follows (debounced - sliders fire a lot).</summary>
    private void OnProfileValueChanged(string key, string value)
    {
        if (_selectedProfile == null || !CropProfiles.IsProfileKey(key)) return;
        _selectedProfile.Values[key] = value;
        _profileSaveTimer.Stop();
        _profileSaveTimer.Start();
    }

    private void SaveProfiles()
    {
        try { CropProfiles.Save(_profiles); }
        catch (Exception e) { CropProfileStatus.Text = "The profiles could not be saved: " + e.Message; AppLog.Write("The crop profiles could not be saved", e); }
    }

    private void OnProfileSaveAs(object sender, RoutedEventArgs e)
    {
        var live = MirrorLive.Read();
        var suggested = CropProfileGame.Text.Trim();
        if (suggested.Length == 0 && live is { Producing: true }) suggested = live.Program;
        CropProfileName.Text = suggested.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(suggested) : "";
        CropProfileNamePanel.Visibility = Visibility.Visible;
        CropProfileName.Focus();
        CropProfileName.SelectAll();
    }

    private void OnProfileSaveConfirm(object sender, RoutedEventArgs e)
    {
        var name = CropProfileName.Text.Trim().Replace("[", "").Replace("]", "");
        if (name.Length == 0) return;
        CropProfileNamePanel.Visibility = Visibility.Collapsed;
        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            // The game: what is in the box, or the game running now.
            var game = CropProfileGame.Text.Trim();
            if (game.Length == 0 && MirrorLive.Read() is { Producing: true } live) game = live.Program;
            profile = new CropProfile { Name = name, Game = game };
            _profiles.Add(profile);
        }
        profile.Values.Clear();
        foreach (var def in Settings.All.Where(d => CropProfiles.IsProfileKey(d.Key)))
        {
            profile.Values[def.Key] = _values.GetValueOrDefault(def.Key) ?? def.Default;
        }
        _selectedProfile = profile;
        _appSettings.SelectedCropProfile = profile.Name;
        _appSettings.Save();
        SaveProfiles();
        AppLog.Write($"Crop profile \"{profile.Name}\" saved (game \"{profile.Game}\", {profile.Values.Count} values).");
        RefreshProfileList();
    }

    /// <summary>Deletes the chosen profile and moves to its neighbour (the one after it, else the one before); "(none)" only when it was the last.</summary>
    private void OnProfileDelete(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null) return;
        var index = _profiles.IndexOf(_selectedProfile);
        AppLog.Write($"Crop profile \"{_selectedProfile.Name}\" deleted.");
        _profiles.Remove(_selectedProfile);
        _selectedProfile = _profiles.Count > 0 ? _profiles[Math.Min(Math.Max(index, 0), _profiles.Count - 1)] : null;
        _appSettings.SelectedCropProfile = _selectedProfile?.Name ?? "";
        _appSettings.Save();
        SaveProfiles();
        _profileLoading = true;
        CropProfileGame.Text = _selectedProfile?.Game ?? "";
        _profileLoading = false;
        if (_selectedProfile != null && _selectedProfile.Values.Count > 0)
        {
            ApplyValues(_selectedProfile.Values);
            ApplyCropMargin(moveBox: false);
            DrawCrop();
        }
        RefreshProfileList();
    }

    /// <summary>Development aid for --screenshots: saves, re-saves and deletes profiles and reports what the list shows after each step.</summary>
    public string ExerciseProfiles(Action<string> snapshot)
    {
        var report = new System.Text.StringBuilder();
        string Describe(string step)
        {
            var items = CropProfile.ItemsSource as List<ProfileItem> ?? [];
            var selected = CropProfile.SelectedItem is ProfileItem item ? item.Label : "(nothing)";
            return $"{step}: {items.Count} entries [{string.Join(" | ", items.Select(i => i.Label))}], selected \"{selected}\", game box \"{CropProfileGame.Text}\"\n";
        }
        report.Append(Describe("start"));
        CropProfileGame.Text = "DCS.exe";
        CropProfileName.Text = "First";
        OnProfileSaveConfirm(this, new RoutedEventArgs());
        report.Append(Describe("after saving First for DCS.exe"));
        snapshot("profiles-first");
        CropProfileGame.Text = "";
        CropProfileName.Text = "Second";
        OnProfileSaveConfirm(this, new RoutedEventArgs());
        report.Append(Describe("after saving Second"));
        CropProfileName.Text = "Third";
        OnProfileSaveConfirm(this, new RoutedEventArgs());
        report.Append(Describe("after saving Third"));
        CropProfile.SelectedItem = (CropProfile.ItemsSource as List<ProfileItem>)!.First(i => i.Profile?.Name == "First");
        report.Append(Describe("after choosing First"));
        OnProfileDelete(this, new RoutedEventArgs());
        report.Append(Describe("after deleting First (expect Second chosen)"));
        snapshot("profiles-after-delete");
        CropProfile.SelectedItem = (CropProfile.ItemsSource as List<ProfileItem>)!.First(i => i.Profile?.Name == "Third");
        OnProfileDelete(this, new RoutedEventArgs());
        report.Append(Describe("after deleting Third, the last one (expect Second chosen)"));
        OnProfileDelete(this, new RoutedEventArgs());
        report.Append(Describe("after deleting Second (expect none)"));
        FillGamePicker("");
        report.Append($"game picker: {(CropProfileGame.ItemsSource as List<RunningApp>)?.Count ?? 0} running programs with a window\n");
        return report.ToString();
    }

    // ------------------------------------------------------------------ the SteamVR helper and what the mirror is doing

    private void RefreshMirrorLive()
    {
        var live = MirrorLive.Read();
        MirrorSource.Text = live is { Producing: true }
            ? $"Picture from {live.Program}{(live.Application.Length > 0 && live.Application != live.Program ? $" ({live.Application})" : "")} - {(live.OpenXR ? "OpenXR game" : "SteamVR game")}, {live.Width} x {live.Height}, {(live.GazeValid ? "gaze ok" : "no gaze")}."
            : "No VR game is running.";
        var helper = MirrorLive.FindHelper();
        HelperStatus.Text = helper == null ? "GazeMirrorHelper.exe was not found next to this app."
            : MirrorLive.IsHelperRunning() ? "The helper is running next to SteamVR."
            : _appSettings.HelperRegisteredPath.Length > 0 ? "The helper starts with SteamVR and leaves with it. Not running now."
            : "SteamVR has not been told to start the helper yet (is Steam installed?). It is tried again when this app starts.";
    }

    /// <summary>
    /// Registers the helper with SteamVR to be started with it (once; SteamVR remembers), then starts it now - it leaves
    /// by itself when SteamVR is not running, so this costs nothing otherwise.
    /// </summary>
    private async Task EnsureHelperAsync()
    {
        var helper = MirrorLive.FindHelper();
        if (helper == null) { AppLog.Write("SteamVR helper: GazeMirrorHelper.exe not found next to the app."); return; }
        if (!string.Equals(_appSettings.HelperRegisteredPath, helper, StringComparison.OrdinalIgnoreCase))
        {
            var result = await MirrorLive.SetHelperAutostartAsync(true);
            AppLog.Write($"SteamVR helper: registration of {helper} with SteamVR {(result == 0 ? "done" : $"not done (exit code {result?.ToString() ?? "none"}); tried again next start")}.");
            if (result == 0)
            {
                _appSettings.HelperRegisteredPath = helper;
                _appSettings.Save();
            }
        }
        if (!MirrorLive.IsHelperRunning())
        {
            var started = MirrorLive.StartHelper();
            AppLog.Write($"SteamVR helper: {(started ? "started" : "could not be started")}.");
        }
        else AppLog.Write("SteamVR helper: already running.");
        RefreshMirrorLive();
    }
}
