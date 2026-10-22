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
        };

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
        _autoCleaner.Start();

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
            }
        };
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
