using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

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
            if (IsVisible) { Render(); _ = RefreshSuggestionsAsync(); _clock.Start(); }
            else _clock.Stop();
        };
    }

    public void Attach(GameModeService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
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
        service.SessionEnded += _ => { RenderHistory(); RenderProfiles(); };
        service.StateChanged += (_, _) => RenderProfiles();
        RenderLibraries();
        RenderIgnored();
        RenderHistory();
        RenderProfiles();
        RenderOverlay();
        Render();
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
        // Suggest games you've played (or the one running now) that don't have a profile yet.
        var known = GameStats.Load().Select(s => s.Game)
            .Concat(_service?.Game is { } g ? [g.Name] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => _settings.ProfileFor(n) is null)
            .Take(8).ToList();
        ProfileSuggestions.ItemsSource = known;
        ProfileHint.Text = known.Count > 0 ? "Add a profile for:" : "Add a profile for a game:";
    }

    void AddProfile(string name)
    {
        name = name.Trim();
        if (_settings is null || name.Length == 0 || _settings.ProfileFor(name) is not null) return;
        // Start from the current defaults so nothing changes until the user flips a switch.
        _settings.GameProfiles.Add(new GameProfile
        {
            Game = name, PowerPlan = _settings.BoostPowerPlan, Priority = _settings.BoostPriority,
            KeepAwake = _settings.KeepAwake, CloseApps = _settings.CloseApps,
        });
        _settings.Save();
        RenderProfiles();
    }

    void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string name) AddProfile(name);
    }

    void AddTypedProfile_Click(object sender, RoutedEventArgs e) { AddProfile(ProfileBox.Text); ProfileBox.Clear(); }

    void ProfileBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddProfile(ProfileBox.Text);
        ProfileBox.Clear();
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
        var accent = (Brush)FindResource(on ? "GoodBrush" : "MutedBrush");
        HeroGlyph.Foreground = accent;
        HeroIcon.Background = on ? new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x2E)) : (Brush)new BrushConverter().ConvertFrom("#262B38")!;
        Hero.BorderBrush = on ? accent : (Brush)FindResource("CardBorderBrush");
        ToggleButton.Content = on ? "Turn off" : "Turn on";
        ToggleButton.Style = (Style)FindResource(on ? "DangerButton" : "PrimaryButton");
        NotGameButton.Visibility = on && _service.Game is not null ? Visibility.Visible : Visibility.Collapsed;
        RenderStats();

        if (on)
        {
            StatusTitle.Text = _service.Game is { } g ? $"Game Mode on · {g.Name}" : "Game Mode is on";
            var since = _service.Since is { } s ? Fmt.Duration(DateTime.Now - s) : "";
            StatusDetail.Text = (_service.IsAuto ? "Turned on automatically" : "Turned on manually") +
                                (since.Length > 0 ? $" · {since}" : "") +
                                (_service.Game is { } game ? $" · {game.Source}" : "");
            BoostDetail.Text = _service.Boosts.Count > 0 ? "Active: " + string.Join(" · ", _service.Boosts) : "No boosts enabled.";
        }
        else
        {
            StatusTitle.Text = "Game Mode is off";
            StatusDetail.Text = _settings?.AutoGameMode == true
                ? "Watching for games. It'll switch on by itself when you start one."
                : "Automatic mode is off. Turn it on here or from the tray icon.";
            BoostDetail.Text = "";
        }
    }

    void RenderLibraries()
    {
        if (_service is null) return;
        var libs = _service.Detector.Libraries;
        LibraryList.ItemsSource = libs;
        LibrariesTitle.Text = libs.Count == 0 ? "No game libraries found" : $"{libs.Count} game libraries found";
    }

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        ToggleButton.IsEnabled = false;
        try { await _service.ToggleAsync(); }
        finally { ToggleButton.IsEnabled = true; }
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
