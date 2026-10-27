using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class DownloadsView : UserControl
{
    AppSettings? _settings;
    List<PlannedMove> _plan = [];
    List<OldInstaller> _installers = [];
    FileSystemWatcher? _watcher;
    // New downloads are sorted once things have been quiet for a bit (files must be 2+ minutes old to move).
    readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMinutes(2.5) };

    public event Action<string, string>? Notify;

    public DownloadsView()
    {
        InitializeComponent();
        _autoTimer.Tick += (_, _) => { _autoTimer.Stop(); AutoOrganize(); };
    }

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        AutoSwitch.IsChecked = settings.DownloadsAutoOrganize;
        Watch(settings.DownloadsAutoOrganize);
    }

    /// <summary>Switches automatic sorting off while the Downloads module is off (the setting itself is kept).</summary>
    public void SetActive(bool on) => Watch(on && _settings?.DownloadsAutoOrganize == true);

    public async void EnsureLoaded() => await RefreshAsync();

    async Task RefreshAsync()
    {
        FolderText.Text = DownloadOrganizer.Folder;
        (_plan, _installers) = await Task.Run(() => (DownloadOrganizer.Plan(), DownloadOrganizer.OldInstallers()));
        SummaryTitle.Text = _plan.Count == 0 ? "Downloads is tidy" : $"{_plan.Count} loose file{(_plan.Count == 1 ? "" : "s")} to sort";
        CategoryChips.Children.Clear();
        foreach (var g in _plan.GroupBy(m => m.Category).OrderByDescending(g => g.Count()))
            CategoryChips.Children.Add(new Border
            {
                Style = (Style)FindResource("Badge"), Margin = new Thickness(0, 0, 8, 8),
                Child = new TextBlock { Text = $"{g.Key}  {g.Count()}", FontSize = 12 },
            });
        PlanList.ItemsSource = _plan.Take(12).ToList();
        MoreText.Text = _plan.Count > 12 ? $"…and {_plan.Count - 12} more" : "";
        OrganizeButton.IsEnabled = _plan.Count > 0;
        UndoButton.IsEnabled = await Task.Run(() => DownloadOrganizer.CanUndo);
        RenderRecovery();

        InstallerList.ItemsSource = _installers;
        InstallersTitle.Text = _installers.Count == 0 ? "Old installers: none"
            : $"Old installers: {_installers.Count} ({Fmt.Bytes(_installers.Sum(i => i.Size))})";
        UpdateRecycleButton();
    }

    async void Organize_Click(object sender, RoutedEventArgs e)
    {
        OrganizeButton.IsEnabled = UndoButton.IsEnabled = false;
        var plan = _plan;
        var result = await Task.Run(() => DownloadOrganizer.Apply(plan));
        await RefreshAsync();
        Report(result, "Sorted");
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        OrganizeButton.IsEnabled = UndoButton.IsEnabled = false;
        var result = await Task.Run(() => DownloadOrganizer.Undo());
        await RefreshAsync();
        Report(result, "Put back");
    }

    /// <summary>The real outcome, with what was skipped or in the way, and a way to recovery when something's open.</summary>
    void Report(BatchResult result, string verb)
    {
        SummaryTitle.Text = result.Summary(verb);
        SummaryTitle.ToolTip = result.Details.Count > 0 ? string.Join("\n", result.Details.Take(20)) : null;
        RenderRecovery();
    }

    void RenderRecovery()
    {
        int n = BatchMover.NeedingAttention().Count;
        RecoveryButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryButton.Content = $"Recovery ({n})";
    }

    void Recovery_Click(object sender, RoutedEventArgs e)
    {
        new RecoveryWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        _ = RefreshAsync();
    }

    void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.DownloadsAutoOrganize = AutoSwitch.IsChecked == true;
        _settings.Save();
        Watch(_settings.DownloadsAutoOrganize);
    }

    void Watch(bool on)
    {
        _watcher?.Dispose();
        _watcher = null;
        _autoTimer.Stop();
        if (!on || !Directory.Exists(DownloadOrganizer.Folder)) return;
        _watcher = new FileSystemWatcher(DownloadOrganizer.Folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        void Changed(object? s, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => { _autoTimer.Stop(); _autoTimer.Start(); });
        _watcher.Created += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        _autoTimer.Start(); // sort anything already waiting, too
    }

    async void AutoOrganize()
    {
        var plan = await Task.Run(DownloadOrganizer.Plan);
        if (plan.Count == 0) return;
        var result = await Task.Run(() => DownloadOrganizer.Apply(plan));
        int moved = result.Succeeded;
        if (moved > 0)
            Notify?.Invoke("Downloads sorted", moved == 1 ? $"{plan[0].Name} → {plan[0].Category}" : $"{moved} new downloads moved into their folders.");
        if (IsVisible) await RefreshAsync();
        if (plan.Count > moved) _autoTimer.Start(); // some were still in use; try again later
    }

    void InstallerCheck_Click(object sender, RoutedEventArgs e) => UpdateRecycleButton();

    void UpdateRecycleButton()
    {
        var ticked = _installers.Where(i => i.IsChecked).ToList();
        RecycleButton.IsEnabled = ticked.Count > 0;
        RecycleButton.Content = ticked.Count > 0 ? $"Move {ticked.Count} to Recycle Bin" : "Move to Recycle Bin";
    }

    async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _installers.Where(i => i.IsChecked).Select(i => i.Path).ToList();
        if (ticked.Count == 0) return;
        if (MessageBox.Show($"Move {ticked.Count} old installer{(ticked.Count == 1 ? "" : "s")} to the Recycle Bin?", "Old installers",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DiskAnalyzer.Recycle(new WindowInteropHelper(Window.GetWindow(this)).Handle, ticked);
        await RefreshAsync();
    }
}
