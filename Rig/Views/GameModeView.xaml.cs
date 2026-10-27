using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class GameModeView : UserControl
{
    GameModeService? _service;
    AppSettings? _settings;
    /// <summary>_apps is everything Game Mode closes; _otherApps is the part not covered by a suggestion (shown as chips).</summary>
    readonly ObservableCollection<string> _games = [], _apps = [], _otherApps = [];
    List<CloseSuggestion> _suggestions = [];
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    public GameModeView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => Render();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Render(); if (!_preview) { _ = RefreshSuggestionsAsync(); _ = LoadProfileGamesAsync(); } _clock.Start(); }
            else _clock.Stop();
        };
    }

    public void Attach(GameModeService service, AppSettings settings, bool preview = false)
    {
        _service = service;
        _settings = settings;
        _preview = preview;
        DataContext = settings;
        foreach (var g in settings.CustomGames) _games.Add(g);
        foreach (var a in settings.AppsToClose) _apps.Add(a);
        GamesList.ItemsSource = _games;
        AppsList.ItemsSource = _otherApps;
        RenderOtherApps();
        ActivityList.ItemsSource = service.Activity;
        service.Activity.CollectionChanged += (_, _) =>
            NoActivity.Visibility = service.Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        service.StateChanged += (_, _) => Render();
        service.IgnoredChanged += (_, _) => { RenderIgnored(); _games.Clear(); foreach (var g in settings.CustomGames) _games.Add(g); };
        service.StatsUpdated += (_, _) => RenderStats();
        service.SessionEnded += _ => { RenderHistory(); RenderProfileSuggestions(); };
        RenderLibraries();
        RenderIgnored();
        if (!preview) RenderHistory();
        RenderProfiles();
        RenderOverlay();
        Render();
        if (IsVisible && !preview) _ = LoadProfileGamesAsync();
    }

    void RenderStats()
    {
        if (_service is null) return;
        var s = _service.Stats;
        LiveStats.Visibility = s.Running ? Visibility.Visible : Visibility.Collapsed;
        LiveCpu.Text = $"{s.LastCpu:0}%";
        LiveGpu.Text = $"{s.LastGpu:0}%";
        LiveRam.Text = s.LastGameRam > 0 ? Fmt.Bytes(s.LastGameRam) : "–";
    }

    // ---------- Overlay ----------

    /// <summary>Raised to show (true) or hide (false) the overlay preview; the main window owns the overlay.</summary>
    public event Action<bool>? OverlayPreview;
    public event EventHandler? OverlaySettingsChanged;
    bool _previewing;
    bool _preview, _loadingGames, _gamesLoaded;
    GameProfile? _lastAddedProfile;

    internal sealed record ProfileGame(string Name, string Store, string? Folder)
    {
        public string Label => $"{Name} · {Store}";
    }

    void RenderOverlay()
    {
        if (_settings is null) return;
        var corners = new[] { Corner0, Corner1, Corner2, Corner3 };
        corners[Math.Clamp(_settings.OverlayCorner, 0, 3)].IsChecked = true;
    }

    void Overlay_Click(object sender, RoutedEventArgs e)
    {
        _settings?.Save();
        OverlaySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || ((FrameworkElement)sender).Tag is not string c) return;
        _settings.OverlayCorner = int.Parse(c);
        _settings.Save();
        OverlaySettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void PreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        _previewing = !_previewing;
        PreviewOverlayButton.Content = _previewing ? "Hide preview" : "Preview";
        OverlayPreview?.Invoke(_previewing);
    }

    void RenderProfiles()
    {
        if (_settings is null) return;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _settings.GameProfiles.ToList();
        RenderProfileSuggestions();
    }

    void RenderProfileSuggestions()
    {
        if (_settings is null || _preview) return;
        // Suggest games you've played (or the one running now) that don't have a profile yet.
        var known = GameStats.Load().Select(s => s.Game)
            .Concat(_service?.Game is { } g ? [g.Name] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !_settings.GameProfiles.Any(p => p.Game.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Take(8).ToList();
        ProfileSuggestions.ItemsSource = known;
        ProfileHint.Text = known.Count > 0 ? "Recently played:" : "";
    }

    void AddProfile(string name, string? folder = null)
    {
        name = name.Trim();
        if (_settings is null || name.Length == 0) { ProfileStatus.Text = "Choose a game or enter its name first."; return; }
        bool exists = _settings.GameProfiles.Any(p => folder is { Length: > 0 }
            ? string.Equals(p.MatchPath?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(p.MatchPath) && p.Game.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exists) { ProfileStatus.Text = "That game already has a profile below."; return; }
        // Start from the current defaults so nothing changes until the user flips a switch.
        _lastAddedProfile = new GameProfile
        {
            Game = name, MatchPath = folder, PowerPlan = _settings.BoostPowerPlan, Priority = _settings.BoostPriority,
            KeepAwake = _settings.KeepAwake, CloseApps = _settings.CloseApps,
        };
        _settings.GameProfiles.Add(_lastAddedProfile);
        _settings.Save();
        RenderProfiles();
        ProfileStatus.Text = $"Created {name}'s profile from your current defaults. Changes apply next session.";
    }

    void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string name) AddProfile(name);
    }

    void AddTypedProfile_Click(object sender, RoutedEventArgs e) => AddSelectedProfile();

    void AddSelectedProfile()
    {
        if (ProfileBox.SelectedItem is ProfileGame game && ProfileBox.Text.Equals(game.Label, StringComparison.OrdinalIgnoreCase))
            AddProfile(game.Name, game.Folder);
        else AddProfile(ProfileBox.Text);
        ProfileBox.SelectedItem = null;
        ProfileBox.Text = "";
    }

    void ProfileBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ProfileBox.IsDropDownOpen) return;
        AddSelectedProfile();
        e.Handled = true;
    }

    void ProfileExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && ReferenceEquals(expander.DataContext, _lastAddedProfile))
        {
            expander.IsExpanded = true;
            _lastAddedProfile = null;
        }
    }

    async void RefreshGames_Click(object sender, RoutedEventArgs e) => await LoadProfileGamesAsync(force: true);

    async Task LoadProfileGamesAsync(bool force = false)
    {
        if (_preview || _settings is null || _loadingGames || (_gamesLoaded && !force)) return;
        _loadingGames = true;
        RefreshGamesButton.IsEnabled = false;
        ProfileStatus.Text = "Finding installed games… You can still type a name.";
        try
        {
            var games = await Task.Run(InstalledGames.FindForProfiles);
            var choices = games.Select(g => new ProfileGame(g.Name, g.Store, g.Folder))
                .Concat(_settings.CustomGames.Select(g => new ProfileGame(Path.GetFileNameWithoutExtension(g), "Custom", Path.IsPathRooted(g) ? Path.GetDirectoryName(g) : null)))
                .DistinctBy(g => (g.Label.ToLowerInvariant(), g.Folder?.ToLowerInvariant())).OrderBy(g => g.Name).ThenBy(g => g.Store).ToList();
            ProfileBox.ItemsSource = choices;
            ProfileStatus.Text = choices.Count == 0 ? "No installed games found. Type a name to create a custom profile."
                : $"{choices.Count} installed {(choices.Count == 1 ? "game" : "games")} available. Folder matching keeps profiles tied to the right installation.";
            _gamesLoaded = true;
        }
        catch { ProfileStatus.Text = "Couldn't read the game libraries. You can still create a profile by name."; }
        finally { _loadingGames = false; RefreshGamesButton.IsEnabled = true; }
    }

    void Profile_Click(object sender, RoutedEventArgs e) => _settings?.Save();

    void ProfileDisplay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count > 0) _settings?.Save(); // not while the list is first filled in
    }

    void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || ((Button)sender).Tag is not GameProfile p) return;
        _settings.GameProfiles.Remove(p);
        _settings.Save();
        RenderProfiles();
    }

    void RenderHistory()
    {
        var sessions = GameStats.Load();
        TotalsList.ItemsSource = GameStats.Totals(sessions).Take(6).ToList();
        SessionList.ItemsSource = sessions.Take(6).ToList();
        NoHistory.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryGrid.Visibility = sessions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    sealed record IgnoredItem(string Value, string Display);

    void RenderIgnored()
    {
        if (_settings is null) return;
        IgnoredList.ItemsSource = _settings.IgnoredGames
            .Select(v => new IgnoredItem(v, Path.GetFileName(v.TrimEnd('\\')) is { Length: > 0 } n ? n : v))
            .ToList();
        NoIgnored.Visibility = _settings.IgnoredGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void NotGame_Click(object sender, RoutedEventArgs e)
    {
        if (_service is not null) await _service.MarkNotGameAsync();
    }

    void RemoveIgnored_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string value || _settings is null) return;
        _settings.IgnoredGames.RemoveAll(i => i.Equals(value, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RenderIgnored();
    }

    void Render()
    {
        if (_service is null) return;
        bool on = _service.IsActive;
        var state = _service.State;
        var summary = GameModeSummary.Create(state, _settings?.AutoGameMode == true, _service.Game?.Name);
        ToggleButton.IsEnabled = !summary.Busy;
        var problems = _service.Problems;
        RecoveryNotice.Visibility = problems.Count > 0 || state == GameModeState.RecoveryNeeded ? Visibility.Visible : Visibility.Collapsed;
        ProblemDetail.Text = state == GameModeState.RecoveryNeeded
            ? $"Not put back yet: {string.Join("; ", problems)}."
            : problems.Count > 0 ? $"Not done: {string.Join("; ", problems)}." : "";
        RetryRestoreButton.Visibility = state == GameModeState.RecoveryNeeded ? Visibility.Visible : Visibility.Collapsed;
        var accent = (Brush)FindResource(summary.Tone == StatusTone.Attention ? "WarnBrush" : summary.Tone == StatusTone.Active ? "GoodBrush" : "AccentLightBrush");
        StateBadgeText.Text = summary.Badge;
        StateBadgeText.Foreground = accent;
        StateBadge.Background = (Brush)FindResource(summary.Tone == StatusTone.Attention ? "WarnTintBrush" : summary.Tone == StatusTone.Active ? "GoodTintBrush" : "InfoTintBrush");
        HeroGlyph.Foreground = accent;
        HeroIcon.Background = (Brush)FindResource(on ? "GoodTintBrush" : "ControlBrush");
        Hero.BorderBrush = on ? accent : (Brush)FindResource("CardBorderBrush");
        ToggleButton.Content = state == GameModeState.RecoveryNeeded ? "Try restoring again" : summary.Action;
        ToggleButton.Style = (Style)FindResource(on ? "DangerButton" : "PrimaryButton");
        NotGameButton.Visibility = state == GameModeState.On && _service.Game is not null ? Visibility.Visible : Visibility.Collapsed;
        RenderStats();
        AppliedPanel.Visibility = state == GameModeState.On && _service.Boosts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppliedChanges.ItemsSource = _service.Boosts;
        StatusTitle.Text = summary.Title;
        StatusDetail.Text = summary.Detail;
        BoostDetail.Text = "";

        if (state == GameModeState.On)
        {
            StatusTitle.Text = _service.Game is { } g ? $"Game Mode on · {g.Name}" : "Game Mode is on";
            var since = _service.Since is { } s ? Fmt.Duration(DateTime.Now - s) : "";
            StatusDetail.Text = (_service.IsAuto ? "Turned on automatically" : "Turned on manually") +
                                (since.Length > 0 ? $" · {since}" : "") +
                                (_service.Game is { } game ? $" · {game.Source}" : "");
            BoostDetail.Text = _service.Boosts.Count > 0 ? "Changes made this session are listed below. They are restored when you turn Game Mode off." : "No changes were applied this session.";
        }
    }

    void RenderLibraries()
    {
        if (_service is null) return;
        var libs = _service.Detector.Libraries;
        LibraryList.ItemsSource = libs;
        LibrariesTitle.Text = libs.Count == 0 ? "No game libraries found" : $"{libs.Count} game libraries found";
    }

    async void RetryRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        RetryRestoreButton.IsEnabled = false;
        try { await _service.RetryRecoveryAsync(); }
        finally { RetryRestoreButton.IsEnabled = true; Render(); }
    }

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        ToggleButton.IsEnabled = false;
        try
        {
            if (_service.State == GameModeState.RecoveryNeeded) await _service.RetryRecoveryAsync();
            else await _service.ToggleAsync();
        }
        finally { Render(); }
    }

    void Setting_Click(object sender, RoutedEventArgs e)
    {
        _settings?.Save();
        Render();
    }

    void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service?.RefreshLibraries();
        RenderLibraries();
    }

    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the game's .exe",
            Filter = "Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() != true) return;
        var exe = Path.GetFileName(dialog.FileName);
        if (_games.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        _games.Add(exe);
        SaveLists();
    }

    void AddApp_Click(object sender, RoutedEventArgs e) => AddApp(AppBox.Text);

    void AppBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddApp(AppBox.Text);
    }

    void AddApp(string raw)
    {
        var name = raw.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || _apps.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _apps.Add(name);
        AppBox.Clear();
        EnableClosing();
        SaveLists();
        SyncSuggestions();
        RenderOtherApps();
    }

    void RemoveChip_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string value) return;
        _games.Remove(value);
        _apps.Remove(value);
        SaveLists();
        RenderOtherApps();
        _ = RefreshOpenAppsAsync();
    }

    /// <summary>Shows known background hogs that are actually running now, plus every other open app for quick adding.</summary>
    async Task RefreshSuggestionsAsync()
    {
        var selected = _apps.ToList();
        _suggestions = await Task.Run(() => BackgroundApps.Detect(selected));
        foreach (var s in _suggestions) s.Toggled = OnSuggestionToggled;
        SyncSuggestions();
        SuggestionList.ItemsSource = _suggestions;
        SuggestionHint.Text = _suggestions.Count == 0
            ? "None of the usual background hogs are running right now."
            : "Known background hogs running right now. Voice chat, overlays and launchers are never suggested.";
        AddAllButton.Visibility = _suggestions.Any(s => s.Recommended && s.IsRunning) ? Visibility.Visible : Visibility.Collapsed;
        RenderOtherApps();
        await RefreshOpenAppsAsync();
    }

    async Task RefreshOpenAppsAsync()
    {
        var exclude = _apps.Concat(_suggestions.SelectMany(s => s.Processes)).ToList();
        var open = await Task.Run(() => BackgroundApps.OpenApps(exclude));
        OpenAppsList.ItemsSource = open;
        OpenAppsLabel.Visibility = open.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string process) return;
        AddApp(process);
        await RefreshOpenAppsAsync();
    }

    void SyncSuggestions()
    {
        foreach (var s in _suggestions)
            s.Sync(s.Processes.Any(p => _apps.Contains(p, StringComparer.OrdinalIgnoreCase)));
    }

    void OnSuggestionToggled(CloseSuggestion s, bool on)
    {
        foreach (var p in s.Processes)
        {
            var existing = _apps.FirstOrDefault(a => a.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (on && existing is null) _apps.Add(p);
            if (!on && existing is not null) _apps.Remove(existing);
        }
        if (on) EnableClosing();
        SaveLists();
        RenderOtherApps();
    }

    void AddAllRecommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _suggestions.Where(s => s.Recommended && s.IsRunning && !s.IsOn)) s.IsOn = true;
    }

    /// <summary>Picking an app to close implies the user wants closing turned on.</summary>
    void EnableClosing()
    {
        if (_settings is null || _settings.CloseApps) return;
        _settings.CloseApps = true;
        CloseAppsSwitch.IsChecked = true;
    }

    void RenderOtherApps()
    {
        var covered = _suggestions.SelectMany(s => s.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _otherApps.Clear();
        foreach (var a in _apps.Where(a => !covered.Contains(a))) _otherApps.Add(a);
    }

    void SaveLists()
    {
        if (_settings is null) return;
        _settings.CustomGames = _games.ToList();
        _settings.AppsToClose = _apps.ToList();
        _settings.Save();
    }
}
