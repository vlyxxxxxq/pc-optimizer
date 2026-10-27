using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly GameModeService _gameMode;
    readonly AutoCleaner _autoCleaner;
    readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    Version? _notifiedUpdate;
    readonly TrayIcon _tray = new();
    readonly Dictionary<RadioButton, UIElement> _pages;
    bool _exiting, _cleanedUp, _healthChecked;

    public MainWindow()
    {
        InitializeComponent();

        _pages = new()
        {
            [NavDashboard] = DashboardPage, [NavGame] = GamePage, [NavCleaner] = CleanerPage, [NavDisk] = DiskPage,
            [NavStartup] = StartupPage, [NavProcesses] = ProcessesPage, [NavTweaks] = TweaksPage,
            [NavApps] = AppsPage, [NavUninstall] = UninstallPage, [NavTools] = ToolsPage, [NavSettings] = SettingsPage,
            [NavNetwork] = NetworkPage, [NavHardware] = HardwarePage, [NavMenu] = MenuPage, [NavSecurity] = SecurityPage,
            [NavLibrary] = LibraryPage, [NavClips] = ClipsPage, [NavClipboard] = ClipboardPage, [NavScreenTime] = ScreenTimePage,
            [NavCrosshair] = CrosshairPage, [NavController] = ControllerPage, [NavDownloads] = DownloadsPage, [NavInput] = InputPage, [NavLayouts] = LayoutsPage,
        };

        SetUpPins();
        AdminStatus.Text = Elevation.IsElevated ? "● Administrator mode" : "● Standard mode · some features need admin";
        AdminStatus.Foreground = (Brush)FindResource(Elevation.IsElevated ? "GoodBrush" : "MutedBrush");

        // Game Mode runs whether or not the window is visible.
        _gameMode = new GameModeService(_settings);
        _gameMode.Notify += Notify;
        _gameMode.StateChanged += (_, _) => RenderGameChip();
        _gameMode.Start(); // finds game libraries, so it must run before the page reads them
        GamePage.Attach(_gameMode, _settings);
        SettingsPage.Attach(_settings);
        SettingsPage.TestNotificationRequested += (_, _) =>
            _tray.Notify("Notifications work", "You'll see Game Mode alerts like this one.");
        AppsPage.Notify += Notify;
        RenderGameChip();

        _autoCleaner = new AutoCleaner(_settings, () => _gameMode.IsActive);
        _autoCleaner.Notify += Notify;
        CleanerPage.Attach(_settings, _autoCleaner);
        ToolsPage.Attach(_settings);
        SecurityPage.Attach(() => _gameMode.Detector.Libraries);

        // Performance overlay: shown with Game Mode (if enabled) or while previewing.
        GamePage.OverlayPreview += on => { _overlayPreview = on; UpdateOverlay(); };
        GamePage.OverlaySettingsChanged += (_, _) => UpdateOverlay();
        _gameMode.StateChanged += (_, _) => UpdateOverlay();
        _autoCleaner.Start();

        // Game library, clips, clipboard history and screen time.
        LibraryPage.Attach(_settings, _gameMode);
        ClipsPage.Attach(_settings);
        ClipsPage.HotkeysChanged += (_, _) => RegisterCaptureHotkeys();
        ClipsPage.HotkeysPaused += (_, _) => { _captureKeys?.Dispose(); _captureKeys = null; };
        ClipsPage.ScreenshotRequested += (_, _) => TakeScreenshot();
        ClipsPage.RecordRequested += (_, _) => ToggleRecording();
        _recorder.Finished += (file, error) => Dispatcher.Invoke(() => RecordingFinished(file, error));

        // Keyboard and mouse stats, per game while Game Mode is on.
        InputPage.Attach(_settings, _inputStats);
        LayoutsPage.Attach(_settings);
        _power = new PowerTimer(() => _gameMode.Detector.Libraries.Where(l => l.Source == "Steam" && l.Name is null).Select(l => l.Root));
        _power.Due += action => new Views.PowerCountdown(action).Show();
        ToolsPage.AttachPowerTimer(_power);
        SettingsPage.NowPlayingChanged += SetNowPlaying;
        _musicTimer.Tick += async (_, _) => await UpdateMusicAsync();
        SetNowPlaying(_settings.ShowNowPlaying);
        _gameMode.StateChanged += (_, _) => _inputStats.Game = _gameMode.IsActive ? _gameMode.Game?.Name : null;
        if (_settings.InputStatsEnabled) _inputStats.Start();
        _clipboard.Enabled = _settings.ClipboardHistoryEnabled;
        ClipboardPage.Attach(_settings, _clipboard);
        _screenTime = new ScreenTime(_settings, () => _gameMode.IsActive);
        _screenTime.Notify += Notify;
        ScreenTimePage.Attach(_settings, _screenTime);
        _screenTime.Start();
        SetUpHotkeys(); // own hidden window, so they work while the app sits in the tray

        // Crosshair: follows the chosen games, the "Show now" switch and its hotkey.
        CrosshairPage.Attach(_settings);
        CrosshairPage.Changed += (_, _) => UpdateCrosshair();
        CrosshairPage.ShowRequested += on => { _crosshairManual = on; UpdateCrosshair(); };
        CrosshairPage.HotkeyPaused += (_, _) => { _crosshairKeys?.Dispose(); _crosshairKeys = null; };
        CrosshairPage.HotkeyChanged += (_, _) => RegisterCrosshairHotkey();
        RegisterCrosshairHotkey();
        _crosshairTimer.Tick += (_, _) => UpdateCrosshair();
        _crosshairTimer.Start();

        DownloadsPage.Attach(_settings);
        DownloadsPage.Notify += Notify;

        // After each game: a recap card with playtime, FPS and anything captured during it.
        _gameMode.SessionEnded += ShowRecap;

        // Free games: check a minute after start, then every 6 hours; each giveaway is announced once.
        _freeTimer.Tick += async (_, _) => { _freeTimer.Interval = TimeSpan.FromHours(6); await CheckFreeGamesAsync(); };
        _freeTimer.Start();

        // Updates: clean up after a previous update, then check shortly after start and once a day.
        Updater.CleanUpOld();
        SettingsPage.UpdateFound += info =>
        {
            UpdateChipTitle.Text = $"Version {info.Version.ToString(3)} available";
            UpdateChip.Visibility = Visibility.Visible;
            if (_notifiedUpdate != info.Version)
            {
                _notifiedUpdate = info.Version;
                Notify("Update available", $"PC Optimizer {info.Version.ToString(3)} is out. Open the app to update in one click.");
            }
        };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_settings.AutoCheckUpdates) await SettingsPage.CheckForUpdateAsync(quiet: true);
        };
        _updateTimer.Start();

        // Test hook: PCOPTIMIZER_TEST_CRASH=1 throws once the window is up, to check the crash reporter.
        if (Environment.GetEnvironmentVariable("PCOPTIMIZER_TEST_CRASH") == "1")
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                throw new InvalidOperationException("Test crash requested by PCOPTIMIZER_TEST_CRASH")));

        // Just imported a setup: open Apps with its programs ticked.
        if (_settings.PendingInstallIds.Count > 0)
        {
            AppsPage.Preselect(_settings.PendingInstallIds);
            _settings.PendingInstallIds = [];
            _settings.Save();
            Loaded += (_, _) => NavApps.IsChecked = true;
        }

        _tray.OpenRequested += (_, _) => ShowFromTray();
        _tray.GameModeRequested += async (_, _) => await _gameMode.ToggleAsync();
        _tray.ExitRequested += (_, _) => ExitApp();

        DashboardPage.ActionRequested += async (_, action) =>
        {
            switch (action)
            {
                case "clean": NavCleaner.IsChecked = true; await CleanerPage.CleanAsync(); break;
                case "startup": NavStartup.IsChecked = true; break;
                case "disk": NavDisk.IsChecked = true; break;
                case "processes": NavProcesses.IsChecked = true; break;
                case "drivers": NavTools.IsChecked = true; break;
            }
        };
        DashboardPage.RecheckRequested += async (_, _) => await RunHealthCheckAsync();
        CleanerPage.Changed += (_, _) => RefreshHealth();
        StartupPage.Changed += (_, _) => RefreshHealth();
        DiskPage.Changed += (_, _) => RefreshHealth();

        // The health check scans files, so only run it once the window is actually shown.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_healthChecked)
            {
                _healthChecked = true;
                await RunHealthCheckAsync();
                // Warm up the heaviest page in the background so it opens instantly later.
                await Task.Delay(3000);
                UninstallPage.EnsureLoaded();
            }
        };
    }

    Views.CrosshairOverlay? _crosshair;
    bool _crosshairManual;
    HotkeyHost? _crosshairKeys;
    readonly System.Windows.Threading.DispatcherTimer _crosshairTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    void RegisterCrosshairHotkey()
    {
        _crosshairKeys?.Dispose();
        _crosshairKeys = new HotkeyHost();
        bool ok = Hotkey.Parse(_settings.CrosshairHotkey) is { } key && _crosshairKeys.Register(key, () =>
        {
            _crosshairManual = !_crosshairManual;
            CrosshairPage.SetShowing(_crosshairManual);
            UpdateCrosshair();
        });
        CrosshairPage.ShowHotkeyState(ok);
    }

    /// <summary>Shows the crosshair when switched on by hand, or while a chosen game is the foreground window.</summary>
    void UpdateCrosshair()
    {
        IntPtr gameWindow = IntPtr.Zero;
        if (_settings.CrosshairGames.Count > 0)
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out int pid);
            // A minimised game can stay "foreground" until something else is clicked; treat it as left.
            if (pid != 0 && pid != Environment.ProcessId && !Native.IsIconic(hwnd) && Native.ProcessPath(pid) is { } path &&
                _settings.CrosshairGames.Any(g =>
                    (g.Folder is { Length: > 3 } f && path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ||
                    (g.Exe is { } exe && Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase))))
                gameWindow = hwnd;
        }
        if (_crosshairManual || gameWindow != IntPtr.Zero)
        {
            _crosshair ??= new Views.CrosshairOverlay();
            _crosshair.Apply(_settings.Crosshair);
            _crosshair.ShowOn(gameWindow);
        }
        else _crosshair?.Hide();
    }

    public void ShowRecap(GameSession session)
    {
        if (!_settings.SessionRecap) return;
        var end = session.Start + session.Duration;
        int captures = Captures.List().Count(c => c.Taken >= session.Start && c.Taken <= end.AddMinutes(1));
        var recap = new Views.SessionRecap(session, captures);
        recap.OpenRequested += (_, _) => ShowPage(NavGame);
        recap.Show();
    }

    readonly System.Windows.Threading.DispatcherTimer _freeTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    async Task CheckFreeGamesAsync()
    {
        if (!_settings.FreeGameAlerts) return;
        List<Giveaway> free;
        try { free = await GameFeeds.FreeGamesAsync(); }
        catch { return; } // offline; try again next time
        var fresh = free.Where(g => !_settings.SeenGiveaways.Contains(g.Id)).ToList();
        if (fresh.Count == 0) return;
        _settings.SeenGiveaways = free.Select(g => g.Id).ToList(); // ended giveaways drop off the list
        _settings.Save();
        Notify(fresh.Count == 1 ? "Free game to claim" : $"{fresh.Count} free games to claim",
            string.Join(", ", fresh.Take(3).Select(g => $"{g.Title} ({g.Platform})")) + (fresh.Count > 3 ? "…" : "") +
            ". Open Game Library to claim.");
    }

    void Notify(string title, string message)
    {
        if (_settings.Notifications) _tray.Notify(title, message);
    }

    void RenderGameChip()
    {
        bool on = _gameMode.IsActive;
        GameChipTitle.Text = on ? "Game Mode on" : "Game Mode off";
        GameChipDetail.Text = on
            ? _gameMode.Game?.Name ?? "Manual"
            : _settings.AutoGameMode ? "Watching for games" : "Click to turn on";
        GameDot.Fill = (Brush)FindResource(on ? "GoodBrush" : "MutedBrush");
        GameChip.BorderBrush = (Brush)FindResource(on ? "GoodBrush" : "CardBorderBrush");
        _tray.SetGameMode(on, _gameMode.Game?.Name);
    }

    async void GameChip_Click(object sender, MouseButtonEventArgs e) => await _gameMode.ToggleAsync();

    void UpdateChip_Click(object sender, MouseButtonEventArgs e) => NavSettings.IsChecked = true;

    Views.Overlay? _overlay;
    bool _overlayPreview;

    void UpdateOverlay()
    {
        bool show = _overlayPreview || (_settings.OverlayWithGameMode && _gameMode.IsActive);
        if (show) (_overlay ??= new Views.Overlay(() => _gameMode.Game?.Pid)).ShowAt(_settings.OverlayCorner);
        else _overlay?.HideOverlay();
    }

    readonly ClipboardHistory _clipboard = new();
    readonly ScreenTime _screenTime;
    HotkeyHost? _hotkeys, _captureKeys;
    readonly InputStats _inputStats = new();
    PowerTimer? _power;
    readonly System.Windows.Threading.DispatcherTimer _musicTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Sidebar mini player: shows the current track (checked every 3 s, off the UI thread).</summary>
    async Task UpdateMusicAsync()
    {
        var track = _settings.ShowNowPlaying ? await Task.Run(NowPlaying.Current) : null;
        MusicChip.Visibility = track is null ? Visibility.Collapsed : Visibility.Visible;
        if (track is null) return;
        MusicTitle.Text = track.Title;
        MusicDetail.Text = track.Artist is null ? track.App : $"{track.Artist} · {track.App}";
        MusicChip.ToolTip = $"{track.Title}{(track.Artist is null ? "" : $" — {track.Artist}")} ({track.App})";
    }

    public void SetNowPlaying(bool on)
    {
        if (on) { _musicTimer.Start(); _ = UpdateMusicAsync(); }
        else { _musicTimer.Stop(); MusicChip.Visibility = Visibility.Collapsed; }
    }

    void MusicPlay_Click(object sender, RoutedEventArgs e) => NowPlaying.PlayPause();
    async void MusicNext_Click(object sender, RoutedEventArgs e) { NowPlaying.Next(); await Task.Delay(1200); await UpdateMusicAsync(); }
    async void MusicPrev_Click(object sender, RoutedEventArgs e) { NowPlaying.Previous(); await Task.Delay(1200); await UpdateMusicAsync(); }
    readonly ScreenRecorder _recorder = new();
    Views.RecordingBar? _recordingBar;
    bool _picking;
    Views.ClipboardPopup? _clipboardPopup;

    /// <summary>Global hotkeys: Ctrl+Alt+V for clipboard history (always), capture keys if switched on.</summary>
    void SetUpHotkeys()
    {
        _hotkeys = new HotkeyHost();
        _hotkeys.ClipboardChanged += (_, _) => _clipboard.OnClipboardChanged();
        _hotkeys.Register(HotkeyHost.MOD_CONTROL | HotkeyHost.MOD_ALT, Key.V, () =>
        {
            if (_clipboard.Enabled) (_clipboardPopup ??= new Views.ClipboardPopup(_clipboard)).Open();
        });
        // Ctrl+Alt+Space if free, otherwise Ctrl+Alt+P; the sidebar shows which one worked.
        PaletteKeyText.Text = _hotkeys.Register(HotkeyHost.MOD_CONTROL | HotkeyHost.MOD_ALT, Key.Space, OpenPalette) ? "Ctrl+Alt+Space"
            : _hotkeys.Register(HotkeyHost.MOD_CONTROL | HotkeyHost.MOD_ALT, Key.P, OpenPalette) ? "Ctrl+Alt+P" : "";
        RegisterCaptureHotkeys();
    }

    void PaletteHint_Click(object sender, MouseButtonEventArgs e) => OpenPalette();

    void RegisterCaptureHotkeys()
    {
        _captureKeys?.Dispose();
        _captureKeys = null;
        bool clipOk = true, shotOk = true, recordOk = true;
        if (_settings.CaptureHotkeys)
        {
            _captureKeys = new HotkeyHost();
            clipOk = Hotkey.Parse(_settings.ClipHotkey) is { } clip && _captureKeys.Register(clip, SaveClip);
            shotOk = Hotkey.Parse(_settings.ScreenshotHotkey) is { } shot && _captureKeys.Register(shot, TakeScreenshot);
            recordOk = Hotkey.Parse(_settings.RecordHotkey) is { } rec && _captureKeys.Register(rec, ToggleRecording);
        }
        ClipsPage.ShowHotkeyState(clipOk, shotOk, recordOk);
    }

    /// <summary>Starts a recording (after picking an area) or stops the one running.</summary>
    async void ToggleRecording()
    {
        if (_recorder.IsRecording) { _recorder.Stop(); return; }
        if (_picking) return;
        _picking = true;
        ScreenArea? area;
        try { area = await new Views.RegionPicker().PickAsync(); }
        finally { _picking = false; }
        if (area is not { Width: >= 16, Height: >= 16 } a) return;
        _recorder.Start(a, _settings.RecordGif, _settings.RecordCursor, _gameMode.Game?.Name ?? "Recording");
        _recordingBar = new Views.RecordingBar(a, () => _recorder.Elapsed, _settings.RecordHotkey);
        _recordingBar.StopRequested += (_, _) => _recorder.Stop();
        _recordingBar.Show();
        ClipsPage.ShowRecording(true);
    }

    void RecordingFinished(string? file, string? error)
    {
        _recordingBar?.Close();
        _recordingBar = null;
        ClipsPage.ShowRecording(false);
        if (file is null)
        {
            Notify("Recording failed", error ?? "Nothing was saved.");
            if (!_settings.Notifications) MessageBox.Show($"The recording couldn't be saved.\n{error}", "PC Optimizer");
            return;
        }
        Notify(file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "GIF saved" : "Recording saved", Path.GetFileName(file));
        if (ClipsPage.IsVisible) ClipsPage.Reload();
    }

    void SaveClip()
    {
        if (!Captures.BackgroundRecording)
        {
            Notify("Clips need background recording", "Open Clips & Screenshots in PC Optimizer and turn on background recording.");
            return;
        }
        Captures.SaveClip();
        // Game Bar shows its own "Game clip recorded" toast; refresh the gallery once the file has been written.
        Dispatcher.InvokeAsync(async () => { await Task.Delay(8000); if (ClipsPage.IsVisible) ClipsPage.Reload(); });
    }

    void TakeScreenshot()
    {
        var file = Captures.Screenshot(_gameMode.Game?.Name);
        if (file is null) { Notify("Screenshot failed", "Windows didn't allow the screen to be captured."); return; }
        Notify("Screenshot saved", Path.GetFileName(file));
        if (ClipsPage.IsVisible) ClipsPage.Reload();
    }

    /// <summary>Dark caption bar that matches the sidebar (Windows 11; ignored on older builds).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x00211815, border = 0x00382B26; // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably jump in front of other windows
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_settings.TrayHintShown)
            {
                _settings.TrayHintShown = true;
                _settings.Save();
                _tray.Notify("PC Optimizer is still running",
                    "Game Mode keeps watching for games. Right-click the tray icon to exit.");
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_exiting) ExitApp();
    }

    void ExitApp()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>Restores anything Game Mode changed and removes the tray icon. Safe to call twice.</summary>
    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _gameMode.Shutdown();
        _overlay?.Close();
        _crosshair?.Close();
        _crosshairKeys?.Dispose();
        _screenTime.Stop();
        _recorder.Stop();
        _inputStats.Stop();
        ClipboardPage.FlushNotes();
        _hotkeys?.Dispose();
        _captureKeys?.Dispose();
        _tray.Dispose();
    }

    /// <summary>Full check: rescans junk (read-only) and reloads startup items, then scores.</summary>
    async Task RunHealthCheckAsync()
    {
        DashboardPage.ShowChecking();
        StartupPage.EnsureLoaded();
        await CleanerPage.ScanAsync();
        RefreshHealth();
    }

    /// <summary>Rescore from what the pages already know, e.g. after a clean or a startup change.</summary>
    void RefreshHealth() =>
        DashboardPage.ShowHealth(Health.Evaluate(CleanerPage.JunkBytes, StartupPage.EnabledCount));

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pages is null) return; // fires during InitializeComponent before pages exist

        var selected = (RadioButton)sender;
        foreach (var (nav, page) in _pages)
            page.Visibility = nav == selected ? Visibility.Visible : Visibility.Collapsed;
        AnimateIn(_pages[selected]);

        if (selected == NavStartup) StartupPage.EnsureLoaded();
        if (selected == NavTools) ToolsPage.EnsureLoaded();
        if (selected == NavTweaks) TweaksPage.EnsureLoaded();
        if (selected == NavApps) AppsPage.EnsureLoaded();
        if (selected == NavUninstall) UninstallPage.EnsureLoaded();
        if (selected == NavNetwork) NetworkPage.EnsureLoaded();
        if (selected == NavHardware) HardwarePage.EnsureLoaded();
        if (selected == NavMenu) MenuPage.EnsureLoaded();
        if (selected == NavSecurity) SecurityPage.EnsureLoaded();
        if (selected == NavLibrary) LibraryPage.EnsureLoaded();
        if (selected == NavClips) ClipsPage.EnsureLoaded();
        if (selected == NavScreenTime) ScreenTimePage.EnsureLoaded();
        if (selected == NavCrosshair) CrosshairPage.EnsureLoaded();
        if (selected == NavDownloads) DownloadsPage.EnsureLoaded();
        if (selected == NavInput) InputPage.EnsureLoaded();
        if (selected == NavLayouts) LayoutsPage.EnsureLoaded();
    }

    /// <summary>Quick fade + slide up when a page opens.</summary>
    static void AnimateIn(UIElement page)
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        var shift = new TranslateTransform(0, 12);
        page.RenderTransform = shift;
        page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }
}
