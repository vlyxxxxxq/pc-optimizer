using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Rig.Services;

namespace Rig;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly GameModeService _gameMode;
    readonly AutoCleaner _autoCleaner;
    readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    Version? _notifiedUpdate;
    readonly TrayIcon _tray = new();
    bool _exiting, _cleanedUp, _healthChecked;

    public MainWindow()
    {
        InitializeComponent();

        AdminStatus.Text = Elevation.IsElevated ? "● Administrator mode" : "● Standard mode · some features need admin";
        AdminStatus.Foreground = (Brush)FindResource(Elevation.IsElevated ? "GoodBrush" : "MutedBrush");

        // Game Mode runs whether or not the window is visible.
        _gameMode = new GameModeService(_settings);
        _gameMode.Notify += Notify;
        _gameMode.StateChanged += (_, _) => RenderGameChip();
        _gameMode.Start(); // finds game libraries, so it must run before any page reads them
        _autoCleaner = new AutoCleaner(_settings, () => _gameMode.IsActive);
        _autoCleaner.Notify += Notify;
        _screenTime = new ScreenTime(_settings, () => _gameMode.IsActive);
        _screenTime.Notify += Notify;
        _power = new PowerTimer(() => _gameMode.Detector.Libraries.Where(l => l.Source == "Steam" && l.Name is null).Select(l => l.Root));
        _power.Due += action => new Views.PowerCountdown(action).Show();
        _recorder.Finished += (file, error) => Dispatcher.Invoke(() => RecordingFinished(file, error));
        _timers.Fired += TimerFired;
        RegisterPages();

        // Background services, each only while its module is on.
        RenderGameChip();
        _gameMode.StateChanged += (_, _) => UpdateOverlay();
        _gameMode.StateChanged += (_, _) => _inputStats.Game = _gameMode.IsActive ? _gameMode.Game?.Name : null;
        if (IsOn("Cleaner")) _autoCleaner.Start();
        if (IsOn("Input") && _settings.InputStatsEnabled) _inputStats.Start();
        _clipboard.Enabled = IsOn("Clipboard") && _settings.ClipboardHistoryEnabled;
        if (IsOn("ScreenTime")) _screenTime.Start();
        _musicTimer.Tick += async (_, _) => await UpdateMusicAsync();
        SetNowPlaying(_settings.ShowNowPlaying);
        SetUpHotkeys(); // own hidden window, so they work while the app sits in the tray

        // Crosshair: follows the chosen games, the "Show now" switch and its hotkey.
        _crosshairTimer.Tick += (_, _) => UpdateCrosshair();
        if (IsOn("Crosshair")) { RegisterCrosshairHotkey(); _crosshairTimer.Start(); }

        // Automatic sorting watches the Downloads folder from its page.
        if (IsOn("Downloads") && _settings.DownloadsAutoOrganize) _ = Page("Downloads");

        // After each game: a recap card with playtime, FPS and anything captured during it.
        _gameMode.SessionEnded += ShowRecap;

        // Free games: check a minute after start, then every 6 hours; each giveaway is announced once.
        _freeTimer.Tick += async (_, _) => { _freeTimer.Interval = TimeSpan.FromHours(6); await CheckFreeGamesAsync(); };
        _freeTimer.Start();

        // Updates: clean up after a previous update, then check shortly after start and once a day.
        Updater.CleanUpOld();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_settings.AutoCheckUpdates) await SettingsPage.CheckForUpdateAsync(quiet: true);
        };
        _updateTimer.Start();

        // Test hook: RIG_TEST_CRASH=1 throws once the window is up, to check the crash reporter.
        if (Environment.GetEnvironmentVariable("RIG_TEST_CRASH") == "1")
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                throw new InvalidOperationException("Test crash requested by RIG_TEST_CRASH")));

        BuildNav();
        Show(Modules.Home);
        SetUpPins();

        // Just imported a setup: open Apps with its programs ticked.
        if (_settings.PendingInstallIds.Count > 0)
        {
            AppsPage.Preselect(_settings.PendingInstallIds);
            _settings.PendingInstallIds = [];
            _settings.Save();
            Loaded += (_, _) => Go("Apps");
        }

        _tray.OpenRequested += (_, _) => ShowFromTray();
        _tray.GameModeRequested += async (_, _) => await _gameMode.ToggleAsync();
        _tray.ExitRequested += (_, _) => ExitApp();

        // The health check scans files, so only run it once the window is actually shown.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_healthChecked)
            {
                _healthChecked = true;
                await RunHealthCheckAsync();
                // Warm up the heaviest page in the background so it opens instantly later.
                await Task.Delay(3000);
                if (IsOn("Uninstall")) UninstallPage.EnsureLoaded();
            }
        };
    }

    readonly System.Windows.Threading.DispatcherTimer _homeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    GameSession? _lastSession;
    bool _lastSessionLoaded;
    List<Giveaway>? _freeGames;
    DateTime _freeGamesAt;

    /// <summary>Fills Home's cards from the services (every 5 s while Home is on screen).</summary>
    async Task RefreshHomeAsync()
    {
        if (Created<Views.DashboardView>(Modules.Home) is not { } home) return;
        if (home.Shows("gamemode"))
        {
            if (_lastSession is null || !_lastSessionLoaded)
            {
                _lastSession = await Task.Run(() => GameStats.Load().OrderByDescending(s => s.Start).FirstOrDefault());
                _lastSessionLoaded = _lastSession is not null;
            }
            home.SetGameMode(_gameMode.IsActive, _gameMode.Game?.Name, _lastSession);
        }
        if (home.Shows("music")) home.SetMusic(await Task.Run(NowPlaying.Current));
        if (home.Shows("screentime"))
        {
            var today = _screenTime.Day(DateTime.Today);
            home.SetScreenTime(TimeSpan.FromSeconds(today.Values.Sum()), today.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault());
        }
        if (home.Shows("input")) home.SetInput(_inputStats.TodayTotals(), _settings.InputStatsEnabled);
        if (home.Shows("timers"))
        {
            var next = _timers.Items.Where(t => !t.Paused).OrderBy(t => t.EndsAt).FirstOrDefault();
            home.SetTimers(next is null ? null : $"{next.Title}", next?.RemainingText, _timers.Items.Count);
        }
        if (home.Shows("power")) home.SetPower(_power.Armed ? _power.Status : "Off. Shut down, restart or sleep later or after downloads.");
        if (home.Shows("freegames") && (_freeGames is null || DateTime.Now - _freeGamesAt > TimeSpan.FromHours(6)))
        {
            _freeGamesAt = DateTime.Now;
            try { _freeGames = await GameFeeds.FreeGamesAsync(); } catch { _freeGames = null; }
            home.SetFreeGames(_freeGames);
        }
    }

    bool _colorKeyOk = true;

    /// <summary>Hides Rig (if it's in the way), freezes the screen and picks a pixel; the HEX goes on the clipboard.</summary>
    async Task PickColorAsync()
    {
        bool wasVisible = IsVisible && WindowState != WindowState.Minimized;
        if (wasVisible) { Hide(); await Task.Delay(250); }
        var result = await Views.PickerOverlay.ShowAsync(Views.PickerOverlay.Mode.Color);
        if (wasVisible) ShowFromTray();
        if (result is not System.Windows.Media.Color c) return;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        try { Clipboard.SetText(hex); } catch { }
        _settings.ColorHistory.Remove(hex);
        _settings.ColorHistory.Insert(0, hex);
        if (_settings.ColorHistory.Count > 24) _settings.ColorHistory.RemoveRange(24, _settings.ColorHistory.Count - 24);
        _settings.Save();
        Created<Views.ColorPickerView>("ColorPicker")?.Render();
        if (!wasVisible) Notify("Colour copied", hex);
    }

    async Task MeasureAsync()
    {
        bool wasVisible = IsVisible && WindowState != WindowState.Minimized;
        if (wasVisible) { Hide(); await Task.Delay(250); }
        var result = await Views.PickerOverlay.ShowAsync(Views.PickerOverlay.Mode.Ruler);
        if (wasVisible) ShowFromTray();
        if (result is ValueTuple<int, int> size)
        {
            try { Clipboard.SetText($"{size.Item1} × {size.Item2}"); } catch { }
            Created<Views.ColorPickerView>("ColorPicker")?.ShowMeasure(size);
        }
    }

    /// <summary>A timer or reminder went off: always say so (even with other notifications off), with a sound.</summary>
    void TimerFired(TimerItem t)
    {
        if (Environment.GetEnvironmentVariable("RIG_TEST_QUIET") == "1") return; // tests: no sounds or toasts
        System.Media.SystemSounds.Exclamation.Play();
        _tray.Notify(t.IsReminder ? "Reminder" : "Time's up", t.IsReminder ? t.Text : $"{t.Title} finished.");
    }

    // Pages, created on first use. Each one's wiring lives with its factory.
    Views.DashboardView DashboardPage => (Views.DashboardView)Page(Modules.Home);
    Views.GameModeView GamePage => (Views.GameModeView)Page("Game");
    Views.SettingsView SettingsPage => (Views.SettingsView)Page(Modules.Settings);
    Views.AppsView AppsPage => (Views.AppsView)Page("Apps");
    Views.CleanerView CleanerPage => (Views.CleanerView)Page("Cleaner");
    Views.StartupView StartupPage => (Views.StartupView)Page("Startup");
    Views.UninstallView UninstallPage => (Views.UninstallView)Page("Uninstall");

    void RegisterPages()
    {
        Register(Modules.Home, () =>
        {
            var p = new Views.DashboardView();
            p.Attach(_settings, IsOn);
            p.OpenRequested += Go;
            p.MusicCommand += async cmd =>
            {
                if (cmd == "play") NowPlaying.PlayPause(); else if (cmd == "next") NowPlaying.Next(); else NowPlaying.Previous();
                await Task.Delay(1200);
                await RefreshHomeAsync();
            };
            p.CardsChanged += async (_, _) => await RefreshHomeAsync();
            p.ActionRequested += async (_, action) =>
            {
                switch (action)
                {
                    case "clean": Go("Cleaner"); await CleanerPage.CleanAsync(); break;
                    case "startup": Go("Startup"); break;
                    case "disk": Go("Disk"); break;
                    case "processes": Go("Processes"); break;
                    case "drivers": Go("Tools"); break;
                }
            };
            p.RecheckRequested += async (_, _) => await RunHealthCheckAsync();
            return p;
        });
        _homeTimer.Tick += async (_, _) => { if (Created<Views.DashboardView>(Modules.Home) is { IsVisible: true }) await RefreshHomeAsync(); };
        _homeTimer.Start();
        _gameMode.StateChanged += async (_, _) => await RefreshHomeAsync();
        _gameMode.SessionEnded += _ => _lastSession = null; // reloaded on the next refresh

        Register("Game", () =>
        {
            var p = new Views.GameModeView();
            p.Attach(_gameMode, _settings);
            p.OverlayPreview += on => { _overlayPreview = on; UpdateOverlay(); };
            p.OverlaySettingsChanged += (_, _) => UpdateOverlay();
            return p;
        });
        Register(Modules.Settings, () =>
        {
            var p = new Views.SettingsView();
            p.Attach(_settings);
            p.TestNotificationRequested += (_, _) => _tray.Notify("Notifications work", "You'll see Game Mode alerts like this one.");
            p.NowPlayingChanged += SetNowPlaying;
            p.ModuleChanged += ApplyModule;
            p.UpdateFound += info =>
            {
                UpdateChipTitle.Text = $"Version {info.Version.ToString(3)} available";
                UpdateChip.Visibility = Visibility.Visible;
                if (_notifiedUpdate != info.Version)
                {
                    _notifiedUpdate = info.Version;
                    Notify("Update available", $"Rig {info.Version.ToString(3)} is out. Open the app to update in one click.");
                }
            };
            return p;
        });
        Register("Apps", () => { var p = new Views.AppsView(); p.Notify += Notify; return p; }, p => p.EnsureLoaded());
        Register("Cleaner", () =>
        {
            var p = new Views.CleanerView();
            p.Attach(_settings, _autoCleaner);
            p.Changed += (_, _) => RefreshHealth();
            return p;
        });
        Register("Startup", () => { var p = new Views.StartupView(); p.Changed += (_, _) => RefreshHealth(); return p; }, p => p.EnsureLoaded());
        Register("Disk", () => { var p = new Views.DiskView(); p.Changed += (_, _) => RefreshHealth(); return p; });
        Register("Tools", () => { var p = new Views.ToolsView(); p.Attach(_settings); p.AttachPowerTimer(_power); return p; }, p => p.EnsureLoaded());
        Register("Security", () => { var p = new Views.SecurityView(); p.Attach(() => _gameMode.Detector.Libraries); return p; }, p => p.EnsureLoaded());
        Register("Library", () => { var p = new Views.LibraryView(); p.Attach(_settings, _gameMode); return p; }, p => p.EnsureLoaded());
        Register("Clips", () =>
        {
            var p = new Views.ClipsView();
            p.Attach(_settings);
            p.HotkeysChanged += (_, _) => RegisterCaptureHotkeys();
            p.HotkeysPaused += (_, _) => { _captureKeys?.Dispose(); _captureKeys = null; };
            p.ScreenshotRequested += (_, _) => TakeScreenshot();
            p.RecordRequested += (_, _) => ToggleRecording();
            p.ShowHotkeyState(_captureState.Clip, _captureState.Shot, _captureState.Record);
            p.ShowRecording(_recorder.IsRecording);
            return p;
        }, p => p.EnsureLoaded());
        Register("Input", () => { var p = new Views.InputStatsView(); p.Attach(_settings, _inputStats); return p; }, p => p.EnsureLoaded());
        Register("Renamer", () => new Views.RenamerView(), p => p.EnsureLoaded());
        Register("Photos", () => new Views.PhotosView());
        Register("ColorPicker", () =>
        {
            var p = new Views.ColorPickerView();
            p.Attach(_settings, _colorKeyOk ? _settings.ColorHotkey : $"{_settings.ColorHotkey} is taken");
            p.PickRequested += async (_, _) => await PickColorAsync();
            p.MeasureRequested += async (_, _) => await MeasureAsync();
            return p;
        });
        Register("Timers", () => { var p = new Views.TimersView(); p.Attach(_timers); return p; });
        Register("Layouts", () => { var p = new Views.LayoutsView(); p.Attach(_settings); return p; }, p => p.EnsureLoaded());
        Register("Clipboard", () => { var p = new Views.ClipboardView(); p.Attach(_settings, _clipboard); return p; });
        Register("ScreenTime", () => { var p = new Views.ScreenTimeView(); p.Attach(_settings, _screenTime); return p; }, p => p.EnsureLoaded());
        Register("Crosshair", () =>
        {
            var p = new Views.CrosshairView();
            p.Attach(_settings);
            p.Changed += (_, _) => UpdateCrosshair();
            p.ShowRequested += on => { _crosshairManual = on; UpdateCrosshair(); };
            p.HotkeyPaused += (_, _) => { _crosshairKeys?.Dispose(); _crosshairKeys = null; };
            p.HotkeyChanged += (_, _) => RegisterCrosshairHotkey();
            p.ShowHotkeyState(_crosshairKeyOk);
            p.SetShowing(_crosshairManual);
            return p;
        }, p => p.EnsureLoaded());
        Register("Downloads", () =>
        {
            var p = new Views.DownloadsView();
            p.Attach(_settings);
            p.Notify += Notify;
            p.SetActive(IsOn("Downloads"));
            return p;
        }, p => p.EnsureLoaded());
        Register("Controller", () => new Views.ControllerView());
        Register("Network", () => new Views.NetworkView(), p => p.EnsureLoaded());
        Register("Hardware", () => new Views.HardwareView(), p => p.EnsureLoaded());
        Register("Menu", () => new Views.ContextMenuView(), p => p.EnsureLoaded());
        Register("Processes", () => new Views.ProcessesView());
        Register("Tweaks", () => new Views.TweaksView(), p => p.EnsureLoaded());
        Register("Uninstall", () => new Views.UninstallView(), p => p.EnsureLoaded());
    }

    Views.CrosshairOverlay? _crosshair;
    bool _crosshairManual;
    HotkeyHost? _crosshairKeys;
    bool _crosshairKeyOk = true;
    (bool Clip, bool Shot, bool Record) _captureState = (true, true, true);
    readonly System.Windows.Threading.DispatcherTimer _crosshairTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    void RegisterCrosshairHotkey()
    {
        _crosshairKeys?.Dispose();
        _crosshairKeys = new HotkeyHost();
        _crosshairKeyOk = Hotkey.Parse(_settings.CrosshairHotkey) is { } key && _crosshairKeys.Register(key, () =>
        {
            _crosshairManual = !_crosshairManual;
            Created<Views.CrosshairView>("Crosshair")?.SetShowing(_crosshairManual);
            UpdateCrosshair();
        });
        Created<Views.CrosshairView>("Crosshair")?.ShowHotkeyState(_crosshairKeyOk);
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
        recap.OpenRequested += (_, _) => ShowPage("Game");
        recap.Show();
    }

    readonly System.Windows.Threading.DispatcherTimer _freeTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    async Task CheckFreeGamesAsync()
    {
        if (!_settings.FreeGameAlerts || !IsOn("Library")) return;
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

    void UpdateChip_Click(object sender, MouseButtonEventArgs e) => Go(Modules.Settings);

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
    readonly Timers _timers = new();
    readonly PowerTimer _power;
    readonly System.Windows.Threading.DispatcherTimer _musicTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Sidebar mini player: shows the current track (checked every 3 s, off the UI thread).</summary>
    async Task UpdateMusicAsync()
    {
        var track = _settings.ShowNowPlaying && !_settings.SidebarCollapsed ? await Task.Run(NowPlaying.Current) : null;
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
        if (_settings.CaptureHotkeys && IsOn("Clips"))
        {
            _captureKeys = new HotkeyHost();
            clipOk = Hotkey.Parse(_settings.ClipHotkey) is { } clip && _captureKeys.Register(clip, SaveClip);
            shotOk = Hotkey.Parse(_settings.ScreenshotHotkey) is { } shot && _captureKeys.Register(shot, TakeScreenshot);
            recordOk = Hotkey.Parse(_settings.RecordHotkey) is { } rec && _captureKeys.Register(rec, ToggleRecording);
        }
        if (IsOn("ColorPicker"))
        {
            _captureKeys ??= new HotkeyHost();
            _colorKeyOk = Hotkey.Parse(_settings.ColorHotkey) is { } col && _captureKeys.Register(col, () => _ = PickColorAsync());
        }
        _captureState = (clipOk, shotOk, recordOk);
        Created<Views.ClipsView>("Clips")?.ShowHotkeyState(clipOk, shotOk, recordOk);
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
        Created<Views.ClipsView>("Clips")?.ShowRecording(true);
    }

    void RecordingFinished(string? file, string? error)
    {
        _recordingBar?.Close();
        _recordingBar = null;
        Created<Views.ClipsView>("Clips")?.ShowRecording(false);
        if (file is null)
        {
            Notify("Recording failed", error ?? "Nothing was saved.");
            if (!_settings.Notifications) MessageBox.Show($"The recording couldn't be saved.\n{error}", "Rig");
            return;
        }
        Notify(file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "GIF saved" : "Recording saved", Path.GetFileName(file));
        ReloadClips();
    }

    void ReloadClips() { if (Created<Views.ClipsView>("Clips") is { IsVisible: true } clips) clips.Reload(); }

    void SaveClip()
    {
        if (!Captures.BackgroundRecording)
        {
            Notify("Clips need background recording", "Open Clips & Screenshots in Rig and turn on background recording.");
            return;
        }
        Captures.SaveClip();
        // Game Bar shows its own "Game clip recorded" toast; refresh the gallery once the file has been written.
        Dispatcher.InvokeAsync(async () => { await Task.Delay(8000); ReloadClips(); });
    }

    void TakeScreenshot()
    {
        var file = Captures.Screenshot(_gameMode.Game?.Name);
        if (file is null) { Notify("Screenshot failed", "Windows didn't allow the screen to be captured."); return; }
        Notify("Screenshot saved", Path.GetFileName(file));
        ReloadClips();
    }

    /// <summary>Dark caption bar that matches the sidebar (Windows 11; ignored on older builds).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = Theme.IsDark ? 1 : 0;
        var (caption, border) = Theme.WindowFrame;
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
                _tray.Notify("Rig is still running",
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
        Created<Views.ClipboardView>("Clipboard")?.FlushNotes();
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
