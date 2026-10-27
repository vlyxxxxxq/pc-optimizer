using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class AppsView : UserControl
{
    readonly List<PackageGroup> _catalog = Winget.Catalog();
    List<Package> _updates = [];
    bool _loaded, _busy;

    /// <summary>(title, message) when a long batch finishes, so it can show as a notification.</summary>
    public event Action<string, string>? Notify;

    public AppsView()
    {
        InitializeComponent();
        CatalogList.ItemsSource = _catalog;
        foreach (var p in _catalog.SelectMany(g => g.Packages)) p.Status = "Checking…";
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!await Winget.IsAvailableAsync())
        {
            NoWinget.Visibility = Visibility.Visible;
            CheckButton.IsEnabled = false;
            foreach (var p in _catalog.SelectMany(g => g.Packages)) p.Status = "";
            return;
        }
        await Task.WhenAll(CheckUpdatesAsync(), RefreshInstalledAsync());
        if (_preselect.Count > 0)
        {
            foreach (var p in _catalog.SelectMany(g => g.Packages).Where(p => !p.IsInstalled && _preselect.Contains(p.Id)))
                p.IsSelected = true;
            InstallSummary.Text = $"{_catalog.SelectMany(g => g.Packages).Count(p => p.IsSelected)} apps from the imported setup are ticked. Click Install when ready.";
            _preselect.Clear();
            UpdateInstallButton();
        }
    }

    readonly HashSet<string> _preselect = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ticks these catalog apps (if not installed) once the page has loaded, e.g. after importing a setup.</summary>
    public void Preselect(IEnumerable<string> ids)
    {
        foreach (var id in ids) _preselect.Add(id);
    }

    async Task RefreshInstalledAsync()
    {
        var inventory = await Winget.InventoryAsync();
        foreach (var p in _catalog.SelectMany(g => g.Packages))
        {
            p.IsInstalled = Winget.IsInstalled(p, inventory);
            if (p.IsInstalled) p.IsSelected = false;
            p.Status = p.IsInstalled ? "Installed" : "";
        }
        UpdateInstallButton();
    }

    async void Check_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    async Task CheckUpdatesAsync()
    {
        if (_busy) return;
        _busy = true;
        CheckButton.IsEnabled = UpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateSummary.Text = "Checking for updates…";
        try
        {
            _updates = await Winget.UpgradesAsync();
            UpdateList.ItemsSource = _updates;
            UpdateSummary.Text = _updates.Count == 0
                ? "Everything winget knows about is up to date."
                : $"{_updates.Count} app{(_updates.Count == 1 ? "" : "s")} can be updated.";
        }
        catch (Exception ex) { UpdateSummary.Text = $"Couldn't check: {ex.Message}"; }
        finally
        {
            _busy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
            UpdateButton.IsEnabled = _updates.Count > 0;
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        var selected = _updates.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0 || _busy) return;
        var (ok, failed) = await RunBatchAsync(selected, Winget.UpgradeAsync, "Updating…", "Updated");
        UpdateSummary.Text = $"Updated {ok} app{(ok == 1 ? "" : "s")}" + (failed > 0 ? $", {failed} failed (see status)." : ".");
        Notify?.Invoke("App updates finished", UpdateSummary.Text);
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var selected = _catalog.SelectMany(g => g.Packages).Where(p => p.IsSelected && !p.IsInstalled).ToList();
        if (selected.Count == 0 || _busy) return;
        if (MessageBox.Show($"Install {selected.Count} app{(selected.Count == 1 ? "" : "s")}?\n\n" +
                            string.Join("\n", selected.Select(p => "  • " + p.Name)) +
                            "\n\nSome installers may ask for administrator permission.",
                "Install apps", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var (ok, failed) = await RunBatchAsync(selected, Winget.InstallAsync, "Installing…", "Installed");
        foreach (var p in selected.Where(p => p.Status == "Installed")) { p.IsInstalled = true; p.IsSelected = false; }
        InstallSummary.Text = $"Installed {ok}" + (failed > 0 ? $", {failed} failed." : ".");
        Notify?.Invoke("App installs finished", InstallSummary.Text);
        UpdateInstallButton();
    }

    async Task<(int Ok, int Failed)> RunBatchAsync(List<Package> packages,
        Func<string, Task<(bool Ok, string Output)>> action, string working, string done)
    {
        _busy = true;
        CheckButton.IsEnabled = UpdateButton.IsEnabled = InstallButton.IsEnabled = false;
        int ok = 0, failed = 0;
        foreach (var p in packages)
        {
            p.Status = working;
            var (success, output) = await action(p.Id);
            p.Status = success ? done : (output.Length > 0 ? output : "Failed");
            if (success) ok++; else failed++;
        }
        _busy = false;
        CheckButton.IsEnabled = true;
        UpdateButton.IsEnabled = _updates.Any(u => u.Status != "Updated");
        UpdateInstallButton();
        return (ok, failed);
    }

    void Package_Click(object sender, RoutedEventArgs e) => UpdateInstallButton();

    void UpdateInstallButton()
    {
        int n = _catalog.SelectMany(g => g.Packages).Count(p => p.IsSelected && !p.IsInstalled);
        InstallButton.IsEnabled = n > 0 && !_busy;
        InstallButton.Content = n > 0 ? $"Install {n} selected" : "Install selected";
    }
}
