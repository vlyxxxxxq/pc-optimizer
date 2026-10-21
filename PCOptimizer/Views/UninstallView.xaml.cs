using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class UninstallView : UserControl
{
    readonly ObservableCollection<InstalledApp> _programs = [];
    readonly ObservableCollection<InstalledApp> _extras = [];
    readonly ICollectionView _view;
    bool _loaded, _busy;

    public UninstallView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_programs);
        _view.Filter = o => SearchBox.Text.Trim() is not { Length: > 0 } q ||
                            ((InstalledApp)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            ((InstalledApp)o).Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
        Grid.ItemsSource = _view;
        ExtrasList.ItemsSource = _extras;
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        await ReloadAsync();
    }

    async Task ReloadAsync()
    {
        SummaryText.Text = "Loading…";
        var programs = await Task.Run(InstalledApps.LoadPrograms);
        InstalledApps.LoadIcons(programs); // shell icon APIs want the UI thread
        _programs.Clear();
        foreach (var p in programs) _programs.Add(p);
        UpdateSummary();

        var extras = await InstalledApps.LoadStoreExtrasAsync();
        _extras.Clear();
        foreach (var e in extras) _extras.Add(e);
        UpdateExtras();
    }

    void UpdateSummary() =>
        SummaryText.Text = $"{_programs.Count} programs · {Fmt.Bytes(_programs.Sum(p => p.SizeBytes))} reported";

    void UpdateExtras()
    {
        ExtrasCard.Visibility = _extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtrasTitle.Text = $"Windows extras you can remove ({_extras.Count})";
    }

    void ExtrasToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ExtrasList.Visibility != Visibility.Visible;
        ExtrasList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ExtrasToggle.Content = show ? "Hide" : "Show";
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var app = Grid.SelectedItem as InstalledApp;
        UninstallButton.IsEnabled = app is not null && !_busy;
        LocationButton.IsEnabled = app?.InstallLocation is { Length: > 0 } loc && Directory.Exists(loc);
    }

    void Location_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is InstalledApp { InstallLocation: { } loc }) Shell.Open(loc);
    }

    async void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await UninstallSelectedAsync();
    async void Uninstall_Click(object sender, RoutedEventArgs e) => await UninstallSelectedAsync();

    async Task UninstallSelectedAsync()
    {
        if (_busy || Grid.SelectedItem is not InstalledApp app) return;
        if (MessageBox.Show($"Uninstall {app.Name}?\n\nIts own uninstaller will open; follow its steps to finish.",
                "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _busy = true;
        UninstallButton.IsEnabled = false;
        app.Status = "Uninstaller running…";
        try
        {
            await InstalledApps.UninstallAsync(app);
            await Task.Delay(1500); // let the uninstaller finish removing its registry entry
            if (!InstalledApps.StillInstalled(app))
            {
                _programs.Remove(app);
                UpdateSummary();
                SummaryText.Text = $"Uninstalled {app.Name}. " + SummaryText.Text;
            }
            else
            {
                app.Status = "Still installed? Refresh when done";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            app.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            app.Status = "Couldn't start uninstaller";
            MessageBox.Show($"Couldn't start the uninstaller for {app.Name}:\n{ex.Message}\n\nTry Settings → Apps instead.",
                "Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Grid_SelectionChanged(this, null!);
        }
    }

    async void RemoveExtra_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not InstalledApp app) return;
        if (MessageBox.Show($"Remove {app.Name}?\n\nYou can reinstall it from the Microsoft Store any time.",
                "Remove app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ((Button)sender).IsEnabled = false;
        app.Status = "Removing…";
        var (ok, output) = await InstalledApps.RemoveStoreAppAsync(app);
        if (ok)
        {
            _extras.Remove(app);
            UpdateExtras();
        }
        else
        {
            app.Status = output.Length > 0 ? output.Split('\n')[0].Trim() : "Couldn't remove it";
            ((Button)sender).IsEnabled = true;
        }
    }
}
