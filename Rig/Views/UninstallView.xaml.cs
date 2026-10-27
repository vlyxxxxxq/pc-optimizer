using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Rig.Services;

namespace Rig.Views;

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
        // Registry read and icons both off the UI thread (icons on an STA thread, which the shell icon API needs;
        // they come back frozen so the UI can use them).
        var programs = await Task.Run(InstalledApps.LoadPrograms);
        var done = new TaskCompletionSource();
        var sta = new Thread(() => { InstalledApps.LoadIcons(programs); done.SetResult(); }) { IsBackground = true };
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        await done.Task;
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

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateButtons();

    /// <summary>Header checkbox: tick or untick every program currently shown (respects the search filter).</summary>
    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = SelectAllBox.IsChecked == true;
        foreach (InstalledApp a in _view) a.IsChecked = on;
        UpdateButtons();
    }

    List<InstalledApp> Targets()
    {
        var ticked = _programs.Where(p => p.IsChecked).ToList();
        return ticked.Count > 0 ? ticked : Grid.SelectedItem is InstalledApp a ? [a] : [];
    }

    void UpdateButtons()
    {
        int ticked = _programs.Count(p => p.IsChecked);
        UninstallButton.Content = ticked > 1 ? $"Uninstall {ticked} selected" : "Uninstall";
        UninstallButton.IsEnabled = !_busy && Targets().Count > 0;
        var app = Grid.SelectedItem as InstalledApp;
        LocationButton.IsEnabled = app?.InstallLocation is { Length: > 0 } loc && Directory.Exists(loc);
    }

    void Location_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is InstalledApp { InstallLocation: { } loc }) Shell.Open(loc);
    }

    async void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is InstalledApp a) await UninstallAsync([a]);
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e) => await UninstallAsync(Targets());

    /// <summary>Runs each uninstaller in turn (installers can't run in parallel) and drops the ones that are gone.</summary>
    async Task UninstallAsync(List<InstalledApp> apps)
    {
        if (_busy || apps.Count == 0) return;
        string question = apps.Count == 1
            ? $"Uninstall {apps[0].Name}?"
            : $"Uninstall these {apps.Count} programs?\n\n" + string.Join("\n", apps.Take(15).Select(a => "  • " + a.Name)) +
              (apps.Count > 15 ? $"\n  …and {apps.Count - 15} more" : "");
        if (MessageBox.Show(question + "\n\nEach program's own uninstaller opens in turn; follow its steps to finish.",
                "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _busy = true;
        UpdateButtons();
        int removed = 0, left = 0;
        try
        {
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                SummaryText.Text = apps.Count > 1 ? $"Uninstalling {i + 1} of {apps.Count}: {app.Name}…" : $"Uninstalling {app.Name}…";
                app.Status = "Uninstaller running…";
                try
                {
                    await InstalledApps.UninstallAsync(app);
                    await Task.Delay(1500); // let the uninstaller finish removing its registry entry
                    if (!InstalledApps.StillInstalled(app))
                    {
                        _programs.Remove(app);
                        removed++;
                    }
                    else
                    {
                        app.Status = "Still installed? Refresh when done";
                        left++;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    app.Status = "Cancelled";
                    left++;
                }
                catch (Exception ex)
                {
                    app.Status = "Couldn't start uninstaller";
                    left++;
                    if (apps.Count == 1)
                        MessageBox.Show($"Couldn't start the uninstaller for {app.Name}:\n{ex.Message}\n\nTry Settings → Apps instead.",
                            "Uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        finally
        {
            _busy = false;
            SelectAllBox.IsChecked = false;
            UpdateSummary();
            if (removed > 0 || left > 0)
                SummaryText.Text = $"Removed {removed}" + (left > 0 ? $", {left} still installed (see Status)" : "") + " · " + SummaryText.Text;
            UpdateButtons();
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
