using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class SettingsView : UserControl
{
    AppSettings? _settings;

    public event EventHandler? TestNotificationRequested;
    public event Action<bool>? NowPlayingChanged;
    /// <summary>A tracking switch changed; the main window starts or stops that tracking.</summary>
    public event EventHandler? PrivacyChanged;

    void Privacy_Click(object sender, RoutedEventArgs e)
    {
        _settings?.Save();
        PrivacyChanged?.Invoke(this, EventArgs.Empty);
    }

    void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(AppSettings.DataFile("settings.json"))!;
        Directory.CreateDirectory(dir);
        Shell.Open(dir);
    }

    /// <summary>Every global shortcut, with the key it's on now.</summary>
    public void ShowShortcuts(IEnumerable<(string What, string Key)> keys)
    {
        var rows = keys.Select(k => new ShortcutRow(k.What, k.Key)).ToList();
        // Only rebuild the list when a key changed, so screen readers keep their place on repeat visits.
        if (ShortcutList.ItemsSource is List<ShortcutRow> shown && shown.SequenceEqual(rows)) return;
        ShortcutList.ItemsSource = rows;
    }

    public sealed record ShortcutRow(string What, string Key);
    /// <summary>A module was switched on or off (its id).</summary>
    public event Action<string>? ModuleChanged;

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings is null || !IsLoaded) return;
        var theme = ThemeLight.IsChecked == true ? "Light" : "Dark";
        if (theme == _settings.Theme) return;
        _settings.Theme = theme;
        _settings.Save();
        RestartRow.Visibility = Visibility.Visible;
    }

    void RenderAccents()
    {
        AccentSwatches.Children.Clear();
        var current = _settings?.Accent ?? Theme.Accents[0].Hex;
        foreach (var (name, hex) in Theme.Accents)
        {
            bool selected = hex.Equals(current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(6, 0, 0, 0), ToolTip = name, Cursor = System.Windows.Input.Cursors.Hand,
                Template = SwatchTemplate(Theme.Parse(hex), selected),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(swatch, "Accent" + name);
            System.Windows.Automation.AutomationProperties.SetName(swatch, name);
            swatch.Click += (_, _) =>
            {
                if (_settings is null) return;
                _settings.Accent = hex;
                _settings.Save();
                RenderAccents();
                RestartRow.Visibility = Visibility.Visible;
            };
            AccentSwatches.Children.Add(swatch);
        }
    }

    ControlTemplate SwatchTemplate(System.Windows.Media.Color color, bool selected)
    {
        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2 : 0));
        ring.SetValue(Border.BorderBrushProperty, FindResource("TextBrush"));
        ring.SetValue(Border.PaddingProperty, new Thickness(selected ? 3 : 0));
        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.SetValue(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Media.SolidColorBrush(color));
        ring.AppendChild(dot);
        return new ControlTemplate(typeof(Button)) { VisualTree = ring };
    }

    void RestartTheme_Click(object sender, RoutedEventArgs e) => App.Restart();

    /// <summary>One switch per module that can be turned off, grouped by category.</summary>
    void RenderModules()
    {
        if (_settings is null) return;
        ModuleList.Children.Clear();
        foreach (var category in Modules.Categories)
        {
            ModuleList.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Style = (Style)FindResource("CardLabel"), Margin = new Thickness(0, 14, 0, 2) });
            foreach (var m in Modules.All.Where(m => m.Category == category))
            {
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = !_settings.DisabledModules.Contains(m.Id), IsEnabled = !m.Core,
                                        ToolTip = m.Core ? "Always on" : null };
                System.Windows.Automation.AutomationProperties.SetAutomationId(sw, "Module" + m.Id);
                void Changed(object? _, RoutedEventArgs __)
                {
                    if (sw.IsChecked == true) _settings.DisabledModules.Remove(m.Id);
                    else if (!_settings.DisabledModules.Contains(m.Id)) _settings.DisabledModules.Add(m.Id);
                    _settings.Save();
                    ModuleChanged?.Invoke(m.Id);
                }
                sw.Checked += Changed;
                sw.Unchecked += Changed;
                DockPanel.SetDock(sw, Dock.Right);
                row.Children.Add(sw);
                row.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = m.Title, FontSize = 14 },
                        new TextBlock { Text = m.Description, Style = (Style)FindResource("Muted") },
                    },
                });
                ModuleList.Children.Add(row);
            }
        }
    }

    void NowPlaying_Click(object sender, RoutedEventArgs e)
    {
        _settings?.Save();
        NowPlayingChanged?.Invoke(_settings?.ShowNowPlaying == true);
    }

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"Rig {Updater.CurrentVersion.ToString()} · {(Updater.IsLite ? "lite build (uses installed .NET)" : "standalone build")}";
        UpdateText.Text = $"Version {Updater.CurrentVersion.ToString()}.";
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
            _update = await Updater.CheckAsync(_settings?.BetaUpdates == true);
            if (_update is null)
            {
                UpdateTitle.Text = "You're up to date";
                UpdateText.Text = $"Version {Updater.CurrentVersion.ToString()} is the latest. Checked {DateTime.Now:t}.";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateTitle.Text = $"Version {_update.Version.ToString()} is available";
                UpdateText.Text = $"You have {Updater.CurrentVersion.ToString()}. Download {Fmt.Bytes(_update.Size)}; the app restarts by itself.";
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
        UpdateTitle.Text = $"Downloading version {_update.Version.ToString()}…";
        var progress = new Progress<double>(p => { UpdateProgress.Value = p; UpdateText.Text = $"{p:P0} of {Fmt.Bytes(_update.Size)}"; });
        if (await Updater.InstallAsync(_update, progress) is { } problem)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateTitle.Text = "Couldn't update automatically";
            UpdateText.Text = problem + " You can download it from the release page instead.";
        }
    }

    public void Attach(AppSettings settings)
    {
        Loaded += (_, _) => RenderModules();
        DataFolderText.Text = "Data folder: " + Path.GetDirectoryName(AppSettings.DataFile("settings.json"));
        ThemeLight.IsChecked = settings.Theme == "Light";
        ThemeDark.IsChecked = settings.Theme != "Light";
        RenderAccents();
        _settings = settings;
        DataContext = settings;
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled;
        SettingsPath.Text = "Settings file: " + (Environment.GetEnvironmentVariable("RIG_SETTINGS") is { Length: > 0 } p
            ? p : @"%AppData%\Rig\settings.json");
    }

    void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try { AutoStart.Set(AutoStartSwitch.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show($"Couldn't change startup:\n{ex.Message}", "Rig"); }
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
        var preview = new ImportPreviewWindow(backup, _settings) { Owner = Window.GetWindow(this) };
        if (preview.ShowDialog() != true || preview.Choices is not { } choices) return;

        var result = Backup.Apply(backup, _settings, choices);
        var lines = new List<string> { result.SettingsSaved ? "Setup imported." : "The import couldn't be saved, so your settings were not changed on disk." };
        lines.AddRange(result.Notes);
        if (result.DataRestored.Count > 0) lines.Add($"Personal data restored: {string.Join(", ", result.DataRestored)}.");
        if (result.DataFailed.Count > 0) lines.Add($"Couldn't restore: {string.Join(", ", result.DataFailed)}.");
        if (result.SkippedTweaks.Count > 0) lines.Add($"These tweaks need administrator mode, so they weren't applied:\n  • {string.Join("\n  • ", result.SkippedTweaks)}");
        if (result.SafetyCopy is not null) lines.Add($"Your previous setup was saved as {Path.GetFileName(result.SafetyCopy)} in Rig's backups folder.");
        lines.Add("Rig will restart to load it" + (_settings.PendingInstallIds.Count > 0 ? ", then show the apps to install." : "."));
        MessageBox.Show(string.Join("\n\n", lines), "Import setup", MessageBoxButton.OK, result.SettingsSaved ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (result.SettingsSaved) App.Restart();
    }

    void TestNotification_Click(object sender, RoutedEventArgs e) =>
        TestNotificationRequested?.Invoke(this, EventArgs.Empty);
}
