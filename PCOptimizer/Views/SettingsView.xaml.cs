using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class SettingsView : UserControl
{
    AppSettings? _settings;

    public event EventHandler? TestNotificationRequested;
    public event Action<bool>? NowPlayingChanged;

    void NowPlaying_Click(object sender, RoutedEventArgs e)
    {
        _settings?.Save();
        NowPlayingChanged?.Invoke(_settings?.ShowNowPlaying == true);
    }

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"PC Optimizer {Updater.CurrentVersion.ToString(3)} · {(Updater.IsLite ? "lite build (uses installed .NET)" : "standalone build")}";
        UpdateText.Text = $"Version {Updater.CurrentVersion.ToString(3)}.";
    }

    UpdateInfo? _update;
    bool _checking;

    /// <summary>Raised when a newer version is found, so the sidebar can show it.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(quiet: false);

    /// <summary>quiet: background check, no error text if offline.</summary>
    public async Task CheckForUpdateAsync(bool quiet)
    {
        if (_checking) return;
        _checking = true;
        CheckUpdateButton.IsEnabled = false;
        if (!quiet) UpdateText.Text = "Checking GitHub…";
        try
        {
            _update = await Updater.CheckAsync();
            if (_update is null)
            {
                UpdateTitle.Text = "You're up to date";
                UpdateText.Text = $"Version {Updater.CurrentVersion.ToString(3)} is the latest. Checked {DateTime.Now:t}.";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateTitle.Text = $"Version {_update.Version.ToString(3)} is available";
                UpdateText.Text = $"You have {Updater.CurrentVersion.ToString(3)}. Download {Fmt.Bytes(_update.Size)}; the app restarts by itself.";
                InstallUpdateButton.Visibility = Visibility.Visible;
                UpdateFound?.Invoke(_update);
            }
        }
        catch (Exception ex)
        {
            if (!quiet) UpdateText.Text = $"Couldn't reach GitHub: {ex.Message}";
        }
        finally
        {
            _checking = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    public async Task InstallUpdateAsync()
    {
        if (_update is null) return;
        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Downloading version {_update.Version.ToString(3)}…";
        var progress = new Progress<double>(p => { UpdateProgress.Value = p; UpdateText.Text = $"{p:P0} of {Fmt.Bytes(_update.Size)}"; });
        if (!await Updater.InstallAsync(_update, progress))
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateTitle.Text = "Couldn't update automatically";
            UpdateText.Text = "The app's folder may be read-only. Opening the download page instead.";
            Shell.Open(_update.PageUrl);
        }
    }

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        DataContext = settings;
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled;
        SettingsPath.Text = "Settings file: " + (Environment.GetEnvironmentVariable("PCOPTIMIZER_SETTINGS") is { Length: > 0 } p
            ? p : @"%AppData%\PCOptimizer\settings.json");
    }

    void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try { AutoStart.Set(AutoStartSwitch.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show($"Couldn't change startup:\n{ex.Message}", "PC Optimizer"); }
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled;
    }

    void Save_Click(object sender, RoutedEventArgs e) => _settings?.Save();

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Backup.Filter,
            FileName = $"{Environment.UserName}-pc-optimizer-setup.pcopt",
        };
        if (dialog.ShowDialog() != true) return;
        // Personal history is only included when asked: the same file is also used to share a setup with friends.
        bool data = MessageBox.Show("Include your personal data too?\n\nPlay history, screen time, input stats, notes, benchmarks and window layouts. " +
                                    "Choose Yes for a backup of your own, No for a setup to share.",
            "Export setup", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        ExportButton.IsEnabled = false;
        try
        {
            await Backup.ExportAsync(dialog.FileName, _settings, new Progress<string>(s => BackupText.Text = s), data);
            BackupText.Text = $"Saved {Path.GetFileName(dialog.FileName)}. Send it to a friend, or keep it as a backup.";
            Shell.Reveal(dialog.FileName);
        }
        catch (Exception ex) { BackupText.Text = $"Couldn't export: {ex.Message}"; }
        finally { ExportButton.IsEnabled = true; }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Backup.Filter };
        if (dialog.ShowDialog() != true) return;

        BackupFile backup;
        try { backup = Backup.Read(dialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show($"That file couldn't be read:\n{ex.Message}", "Import setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(Backup.Describe(backup) +
                            "\n\nThis replaces your current Game Mode and app settings. Tweaks are switched on (you can turn any off later). Continue?",
                "Import setup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var skipped = Backup.Apply(backup, _settings);
        MessageBox.Show("Setup imported. PC Optimizer will restart to load it" +
                        (_settings.PendingInstallIds.Count > 0 ? ", then show the apps to install." : ".") +
                        (skipped.Count > 0
                            ? $"\n\nThese tweaks need administrator mode, so they weren't applied:\n  • {string.Join("\n  • ", skipped)}"
                            : ""),
            "Import setup", MessageBoxButton.OK, MessageBoxImage.Information);
        App.Restart();
    }

    void TestNotification_Click(object sender, RoutedEventArgs e) =>
        TestNotificationRequested?.Invoke(this, EventArgs.Empty);
}
