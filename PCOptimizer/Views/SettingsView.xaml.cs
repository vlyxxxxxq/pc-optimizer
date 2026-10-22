using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class SettingsView : UserControl
{
    AppSettings? _settings;

    public event EventHandler? TestNotificationRequested;

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

    void TestNotification_Click(object sender, RoutedEventArgs e) =>
        TestNotificationRequested?.Invoke(this, EventArgs.Empty);
}
