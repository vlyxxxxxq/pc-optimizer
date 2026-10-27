using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rig.Services;

namespace Rig.Views;

/// <summary>Shows what a setup file contains and lets the person pick which parts to import.</summary>
public sealed class ImportPreviewWindow : Window
{
    readonly CheckBox _prefs = new() { IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _tracking = new() { IsChecked = false, Margin = new Thickness(24, 6, 0, 0) };
    readonly CheckBox _data = new() { IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _tweaks = new() { IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _apps = new() { IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public ImportChoices? Choices { get; private set; }

    public ImportPreviewWindow(BackupFile backup, AppSettings current)
    {
        Title = "Import setup";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var tracking = SettingsPolicy.TrackingItWouldTurnOn(backup.Settings, current);
        _prefs.Content = "Preferences: Game Mode, look, Home, modules, hotkeys and more";
        _tracking.Content = tracking.Count > 0 ? $"Also turn on {string.Join(", ", tracking).ToLowerInvariant()} (on in this setup, off here)" : "";
        _tracking.Visibility = tracking.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _data.Content = backup.Data.Count > 0 ? $"Personal data: {string.Join(", ", backup.Data.Keys.Select(Path.GetFileNameWithoutExtension))} (replaces yours)" : "Personal data: none in this file";
        _data.IsEnabled = backup.Data.Count > 0;
        _tweaks.Content = $"Switch on {backup.Tweaks.Count} Windows tweak{(backup.Tweaks.Count == 1 ? "" : "s")} (some need administrator mode)";
        _tweaks.IsEnabled = backup.Tweaks.Count > 0;
        _apps.Content = $"Suggest {backup.Apps.Count} app{(backup.Apps.Count == 1 ? "" : "s")} to install (you choose on the Apps page)";
        _apps.IsEnabled = backup.Apps.Count > 0;
        foreach (var (box, id) in new[] { (_prefs, "ImportPreferences"), (_tracking, "ImportTracking"), (_data, "ImportData"), (_tweaks, "ImportTweaks"), (_apps, "ImportApps") })
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
        _prefs.Checked += (_, _) => _tracking.IsEnabled = true;
        _prefs.Unchecked += (_, _) => { _tracking.IsEnabled = false; _tracking.IsChecked = false; };

        var import = new Button { Content = "Import", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(import, "ImportButton");
        import.Click += (_, _) =>
        {
            Choices = new ImportChoices(_prefs.IsChecked == true, _tracking.IsChecked == true, _data.IsChecked == true, _tweaks.IsChecked == true, _apps.IsChecked == true);
            DialogResult = true;
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "Import setup", FontSize = 20, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = Backup.Describe(backup), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "Choose what to bring in:", Margin = new Thickness(0, 16, 0, 0), FontWeight = FontWeights.SemiBold },
                _prefs, _tracking, _data, _tweaks, _apps,
                new TextBlock
                {
                    Text = "Your current setup is saved first (Rig's backups folder), so you can go back. This PC's schedules and anything Game Mode still has to restore are never replaced.",
                    TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 16, 0, 0),
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Children = { import, cancel } },
            },
        };
    }
}
