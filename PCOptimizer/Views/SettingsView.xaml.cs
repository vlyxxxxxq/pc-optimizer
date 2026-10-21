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
        VersionText.Text = $"PC Optimizer {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)}";
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
