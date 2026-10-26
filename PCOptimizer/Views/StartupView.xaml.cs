using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class StartupView : UserControl
{
    List<StartupItem> _items = [];
    bool _loaded;

    /// <summary>Raised when an item is enabled or disabled so the health score can update.</summary>
    public event EventHandler? Changed;

    public int EnabledCount => _items.Count(i => i.IsEnabled);

    public StartupView() => InitializeComponent();

    public void EnsureLoaded()
    {
        if (!_loaded) Reload();
    }

    async void Reload()
    {
        try
        {
            _items = StartupManager.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read startup items:\n{ex.Message}", "PC Optimizer");
        }
        Grid.ItemsSource = _items;
        _loaded = true;
        UpdateCount();

        // Scheduled tasks take a second or two to query, so they're added once ready.
        var tasks = await StartupManager.LoadTasksAsync();
        if (tasks.Count > 0)
        {
            _items = _items.Concat(tasks).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Grid.ItemsSource = _items;
            UpdateCount();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        var all = _items.ToList();
        await Task.Run(() => StartupManager.MeasureUsage(all));
        var heavy = all.Count(i => i.IsEnabled && i.Impact == "High");
        if (heavy > 0) CountText.Text += $" · {heavy} high-impact (sort by Impact to see them)";
    }

    void UpdateCount() =>
        CountText.Text = $"{_items.Count} startup items · {EnabledCount} enabled";

    void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    void Enable_Click(object sender, RoutedEventArgs e) => Toggle(true);
    void Disable_Click(object sender, RoutedEventArgs e) => Toggle(false);

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is StartupItem { FilePath: { } path }) Shell.Reveal(path);
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is StartupItem item) Toggle(!item.IsEnabled);
    }

    void Toggle(bool enable)
    {
        if (Grid.SelectedItem is not StartupItem item) return;
        try
        {
            StartupManager.SetEnabled(item, enable);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show($"Couldn't change \"{item.Name}\":\n{ex.Message}", "PC Optimizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            MessageBox.Show($"\"{item.Name}\" starts for all users, so changing it needs administrator rights.\n\n" +
                            "Use Tools → Restart as administrator, then try again.",
                "Administrator required", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        UpdateButtons();
        UpdateCount();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void UpdateButtons()
    {
        var item = Grid.SelectedItem as StartupItem;
        EnableButton.IsEnabled = item is { IsEnabled: false };
        DisableButton.IsEnabled = item is { IsEnabled: true };
        RevealButton.IsEnabled = item?.FilePath is not null;
    }
}
