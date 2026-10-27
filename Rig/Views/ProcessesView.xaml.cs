using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class ProcessesView : UserControl
{
    readonly ProcessSampler _sampler = new();
    readonly ObservableCollection<ProcessRow> _rows = [];
    readonly Dictionary<string, ProcessRow> _byKey = [];
    readonly ListCollectionView _view;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    List<ProcessSample> _last = [];
    bool _sampling;

    public ProcessesView()
    {
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.MemoryBytes), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRow.MemoryBytes), nameof(ProcessRow.Cpu), nameof(ProcessRow.Count) })
            _view.LiveSortingProperties.Add(p);
        _view.Filter = o => SearchBox.Text.Length == 0 ||
                            ((ProcessRow)o).Name.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        Grid.ItemsSource = _view;

        _timer.Tick += async (_, _) => await RefreshAsync();
        // Only sample while the page is on screen.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { await RefreshAsync(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    public void EnsureLoaded() { /* sampling starts when the page becomes visible */ }

    async Task RefreshAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            _last = await Task.Run(_sampler.Sample);
            Apply();
        }
        finally { _sampling = false; }
    }

    void Apply()
    {
        bool grouped = GroupBox.IsChecked == true;
        var groups = grouped
            ? _last.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                   .Select(g => (Key: g.Key, Name: g.First().Name, Ids: (IReadOnlyList<int>)g.Select(s => s.Id).ToList(),
                                 Cpu: g.Sum(s => s.Cpu), Mem: g.Sum(s => s.Memory)))
            : _last.Select(s => (Key: s.Id.ToString(), s.Name, Ids: (IReadOnlyList<int>)[s.Id], s.Cpu, Mem: s.Memory));

        var seen = new HashSet<string>();
        foreach (var g in groups)
        {
            seen.Add(g.Key);
            if (!_byKey.TryGetValue(g.Key, out var row))
            {
                row = new ProcessRow { Key = g.Key, Name = g.Name };
                _byKey[g.Key] = row;
                _rows.Add(row);
            }
            row.Update(g.Ids, g.Cpu, g.Mem);
        }
        foreach (var gone in _rows.Where(r => !seen.Contains(r.Key)).ToList())
        {
            _rows.Remove(gone);
            _byKey.Remove(gone.Key);
        }

        SummaryText.Text = $"{_last.Count} processes · {_last.Sum(s => s.Cpu):0}% CPU · " +
                           $"{Fmt.Bytes(_last.Sum(s => s.Memory))} memory";
        UpdateEndButton();
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        _byKey.Clear();
        Apply();
    }

    void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEndButton();

    void UpdateEndButton()
    {
        var row = Grid.SelectedItem as ProcessRow;
        EndButton.IsEnabled = row is not null && !ProcessMonitor.IsProtected(row);
        EndButton.Content = row is { Count: > 1 } ? $"End {row.Count} processes" : "End task";
    }

    async void End_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ProcessRow row || ProcessMonitor.IsProtected(row)) return;
        string what = row.Count > 1 ? $"all {row.Count} \"{row.Name}\" processes" : $"\"{row.Name}\" (PID {row.Ids[0]})";
        if (MessageBox.Show($"End {what}?\n\nUnsaved work in this program will be lost.",
                "End task", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int failed = ProcessMonitor.Kill(row);
        if (failed > 0)
            MessageBox.Show($"{failed} process(es) couldn't be ended. They may need administrator rights.",
                "Rig", MessageBoxButton.OK, MessageBoxImage.Information);
        await Task.Delay(300);
        await RefreshAsync();
    }
}
