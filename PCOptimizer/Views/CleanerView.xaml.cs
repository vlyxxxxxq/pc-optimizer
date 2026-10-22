using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class CleanerView : UserControl
{
    readonly ObservableCollection<JunkCategory> _categories = new(JunkCleaner.CreateCategories());
    Task? _scan;
    bool _busy;

    /// <summary>Raised after a clean so the health score can update.</summary>
    public event EventHandler? Changed;

    /// <summary>Removable junk in the categories that are ticked by default-safe selection.</summary>
    public long JunkBytes => _categories.Where(c => c.IsSelected).Sum(c => c.SizeBytes);

    AppSettings? _settings;
    AutoCleaner? _auto;

    public CleanerView()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;
    }

    public void Attach(AppSettings settings, AutoCleaner auto)
    {
        _settings = settings;
        _auto = auto;
        auto.Ran += (_, _) => RenderAuto();
        RenderAuto();
    }

    void RenderAuto()
    {
        if (_settings is null || _auto is null) return;
        AutoSwitch.IsChecked = _settings.AutoClean;
        EveryDay.IsChecked = _settings.AutoCleanDays == 1;
        EveryWeek.IsChecked = _settings.AutoCleanDays == 7;
        EveryMonth.IsChecked = _settings.AutoCleanDays == 30;
        var last = _settings.LastAutoClean is { } l ? $"Last run {l:g}." : "Hasn't run yet.";
        AutoText.Text = _settings.AutoClean
            ? $"{last} Next run {(_auto.NextRun is { } n && n > DateTime.Now ? n.ToString("g") : "within the hour")}. Skipped while you're gaming."
            : "Runs in the background on the categories ticked by default (never the Recycle Bin), and never during a game.";
    }

    void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.AutoClean = AutoSwitch.IsChecked == true;
        _settings.Save();
        RenderAuto();
    }

    void Interval_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || ((FrameworkElement)sender).Tag is not string days) return;
        _settings.AutoCleanDays = int.Parse(days);
        _settings.Save();
        RenderAuto();
    }

    async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    async void Clean_Click(object sender, RoutedEventArgs e) => await CleanAsync();

    /// <summary>Scans every category. Concurrent callers share the scan already in progress.</summary>
    public Task ScanAsync()
    {
        if (_scan is { IsCompleted: false }) return _scan;
        if (_busy) return Task.CompletedTask; // cleaning
        return _scan = RunScanAsync();
    }

    async Task RunScanAsync()
    {
        SetBusy(true);
        TotalText.Text = "Scanning…";
        Progress.Maximum = _categories.Count;
        Progress.Value = 0;

        foreach (var c in _categories)
        {
            c.Status = "Scanning…";
            c.ApplyScan(await Task.Run(() => JunkCleaner.Scan(c)));
            Progress.Value++;
        }

        long total = _categories.Sum(c => c.SizeBytes);
        int items = _categories.Sum(c => c.ItemCount);
        TotalText.Text = $"{Fmt.Bytes(total)} found";
        StatusText.Text = $"{items:N0} items across {_categories.Count(c => c.ItemCount > 0)} categories. " +
                          "Choose what to remove, then click Clean selected.";
        SetBusy(false);
    }

    public async Task CleanAsync()
    {
        if (_scan is { IsCompleted: false }) await _scan;
        if (_busy) return;
        if (!_categories.Any(c => c.IsScanned)) await ScanAsync();

        var selected = _categories.Where(c => c.IsSelected && c.IsScanned && c.ItemCount > 0).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Nothing selected to clean. Tick at least one category that has junk in it.",
                "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long size = selected.Sum(c => c.SizeBytes);
        var names = string.Join("\n", selected.Select(c => $"  • {c.Name} ({c.SizeText})"));
        if (MessageBox.Show($"Permanently delete {Fmt.Bytes(size)} from:\n\n{names}\n\nThis can't be undone.",
                "Confirm cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        TotalText.Text = "Cleaning…";
        Progress.Maximum = selected.Count;
        Progress.Value = 0;

        long freed = 0;
        int skipped = 0;
        foreach (var c in selected)
        {
            c.Status = "Cleaning…";
            var result = await Task.Run(() => JunkCleaner.Clean(c));
            c.ApplyClean(result);
            freed += result.Freed;
            skipped += result.Failed.Count;
            Progress.Value++;
        }

        TotalText.Text = $"{Fmt.Bytes(freed)} freed";
        StatusText.Text = skipped == 0
            ? "All selected junk was removed."
            : $"{skipped:N0} items were skipped because they're in use or need administrator rights.";
        SetBusy(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && _categories.Any(c => c.ItemCount > 0);
    }
}
