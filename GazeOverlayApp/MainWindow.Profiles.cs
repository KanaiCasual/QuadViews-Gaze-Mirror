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
            var row = BuildRow(def);
            row.Margin = new Thickness(0, 1, 14, 1);
            PanelOutput.Children.Add(row);
        }

        // Crop profiles.
        _profileSaveTimer.Tick += (_, _) => { _profileSaveTimer.Stop(); SaveProfiles(); };
        LoadProfiles();
        CropProfile.SelectionChanged += (_, _) => OnProfileSelected();
        CropProfileGame.LostFocus += (_, _) => OnProfileGameChanged();
        CropProfileGame.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnProfileGameChanged(); };
        CropProfileName.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) OnProfileSaveConfirm(this, new RoutedEventArgs());
            if (e.Key == System.Windows.Input.Key.Escape) { CropProfileNamePanel.Visibility = Visibility.Collapsed; }
        };

        // The helper.
        HelperAutostart.IsChecked = _appSettings.HelperAutostart;
        HelperAutostart.Click += async (_, _) => await OnHelperAutostartChangedAsync();
    }

    // ------------------------------------------------------------------ crop profiles

    private void LoadProfiles()
    {
        _profileLoading = true;
        try { _profiles = CropProfiles.Load(); }
        catch (Exception e) { _profiles = []; CropProfileStatus.Text = "The profiles file could not be read: " + e.Message; }
        var selectedName = _selectedProfile?.Name ?? _appSettings.SelectedCropProfile;
        CropProfile.Items.Clear();
        CropProfile.Items.Add(new ComboBoxItem { Content = "(none - the settings as they are)", Tag = null });
        foreach (var profile in _profiles)
        {
            CropProfile.Items.Add(new ComboBoxItem { Content = profile.Game.Length > 0 ? $"{profile.Name}  ({profile.Game})" : profile.Name, Tag = profile });
        }
        _selectedProfile = _profiles.FirstOrDefault(p => p.Name == selectedName);
        CropProfile.SelectedItem = CropProfile.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, _selectedProfile)) ?? CropProfile.Items[0];
        _profileLoading = false;
        RefreshProfileUi();
    }

    private void RefreshProfileUi()
    {
        var has = _selectedProfile != null;
        CropProfileGame.IsEnabled = has;
        CropProfileDelete.IsEnabled = has;
        CropProfileGame.Text = _selectedProfile?.Game ?? "";
        var live = MirrorLive.Read();
        var running = live is { Producing: true } ? live.Program : null;
        CropProfileStatus.Text = _selectedProfile == null
            ? (running != null ? $"Running now: {running}. Save the framing as a profile for it to have it applied whenever it starts." : "Save the framing as a profile to have it applied whenever a game starts.")
            : _selectedProfile.Game.Length == 0 ? "Edits are saved into this profile. Type a game's program file (for example DCS.exe) to have it applied when that game starts."
            : $"Edits are saved into this profile. It is applied when {_selectedProfile.Game} starts" + (running != null && !string.Equals(running, _selectedProfile.Game, StringComparison.OrdinalIgnoreCase) ? $" (running now: {running})." : ".");
    }

    private void OnProfileSelected()
    {
        if (_profileLoading) return;
        _selectedProfile = CropProfile.SelectedItem is ComboBoxItem { Tag: CropProfile profile } ? profile : null;
        _appSettings.SelectedCropProfile = _selectedProfile?.Name ?? "";
        _appSettings.Save();
        if (_selectedProfile != null && _selectedProfile.Values.Count > 0)
        {
            // Show it: its values become the current settings (and reach a running game like any change).
            ApplyValues(_selectedProfile.Values);
            ApplyCropMargin(moveBox: false);
            DrawCrop();
        }
        RefreshProfileUi();
    }

    private void OnProfileGameChanged()
    {
        if (_selectedProfile == null) return;
        var game = CropProfileGame.Text.Trim();
        if (game == _selectedProfile.Game) return;
        _selectedProfile.Game = game;
        SaveProfiles();
        LoadProfiles();
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
        catch (Exception e) { CropProfileStatus.Text = "The profiles could not be saved: " + e.Message; }
    }

    private void OnProfileSaveAs(object sender, RoutedEventArgs e)
    {
        var live = MirrorLive.Read();
        CropProfileName.Text = live is { Producing: true } && live.Program.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(live.Program) : "";
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
            var live = MirrorLive.Read();
            profile = new CropProfile { Name = name, Game = live is { Producing: true } ? live.Program : "" };
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
        LoadProfiles();
    }

    private void OnProfileDelete(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null) return;
        _profiles.Remove(_selectedProfile);
        _selectedProfile = null;
        _appSettings.SelectedCropProfile = "";
        _appSettings.Save();
        SaveProfiles();
        LoadProfiles();
    }

    // ------------------------------------------------------------------ the SteamVR helper and what the mirror is doing

    private void RefreshMirrorLive()
    {
        var live = MirrorLive.Read();
        MirrorSource.Text = live is { Producing: true }
            ? $"Picture from {live.Program}{(live.Application.Length > 0 && live.Application != live.Program ? $" ({live.Application})" : "")} - {(live.OpenXR ? "OpenXR game" : "SteamVR game")}, {live.Width} x {live.Height}, {(live.GazeValid ? "gaze ok" : "no gaze")}."
            : "No VR game is running.";
        var helper = MirrorLive.FindHelper();
        var running = helper != null && MirrorLive.IsHelperRunning();
        HelperStart.IsEnabled = helper != null && !running;
        HelperStop.IsEnabled = running;
        HelperStatus.Text = helper == null ? "GazeMirrorHelper.exe was not found next to this app."
            : running ? "The SteamVR helper is running." : "The SteamVR helper is not running (it leaves by itself when SteamVR is not running).";
    }

    private async void OnHelperStart(object sender, RoutedEventArgs e)
    {
        if (!MirrorLive.StartHelper()) { HelperStatus.Text = "The helper could not be started."; return; }
        for (var attempt = 0; attempt < 20 && !MirrorLive.IsHelperRunning(); attempt++) await Task.Delay(250);
        RefreshMirrorLive();
        if (!MirrorLive.IsHelperRunning()) HelperStatus.Text = "The helper left again: SteamVR is not running.";
    }

    private async void OnHelperStop(object sender, RoutedEventArgs e)
    {
        MirrorLive.StopHelper();
        for (var attempt = 0; attempt < 20 && MirrorLive.IsHelperRunning(); attempt++) await Task.Delay(250);
        RefreshMirrorLive();
    }

    private async Task OnHelperAutostartChangedAsync()
    {
        var on = HelperAutostart.IsChecked == true;
        HelperAutostart.IsEnabled = false;
        var result = await MirrorLive.SetHelperAutostartAsync(on);
        HelperAutostart.IsEnabled = true;
        if (result == 0)
        {
            _appSettings.HelperAutostart = on;
            _appSettings.Save();
            HelperStatus.Text = on ? "SteamVR will start the helper with itself from now on." : "SteamVR no longer starts the helper.";
        }
        else
        {
            HelperAutostart.IsChecked = !on;
            HelperStatus.Text = result == null ? "GazeMirrorHelper.exe was not found next to this app."
                : "SteamVR could not be told (is Steam installed?). The helper's log says more.";
        }
    }
}
