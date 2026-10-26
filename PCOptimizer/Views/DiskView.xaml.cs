using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

/// <summary>One visible line of the folder tree (the tree is flattened so columns line up, like WizTree).</summary>
public sealed class DiskRow : ObservableObject
{
    static readonly Brush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xC4, 0x51)));
    static readonly Brush FileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA2)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public required DiskNode Node { get; init; }
    public required int Depth { get; init; }

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpandGlyph)); } }

    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Name => Depth == 0 ? Node.FullPath : Node.Name;
    public bool CanExpand => Node.IsDirectory && Node.HasChildren;
    public bool NoExpander => !CanExpand;
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public string TypeGlyph => Node.IsDirectory ? "\uE8B7" : "\uE8A5";
    public Brush TypeBrush => Node.IsDirectory ? FolderBrush : FileBrush;
    public string SizeText => Fmt.Bytes(Node.Size);
    public double Percent => Node.Parent is { Size: > 0 } p ? 100.0 * Node.Size / p.Size : 100;
    public string PercentText => $"{Percent:0.0} %";
    public string FilesText => Node.IsDirectory ? Node.FileCount.ToString("N0") : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(NoExpander));
    }
}

public partial class DiskView : UserControl
{
    readonly ObservableCollection<DiskRow> _rows = [];
    readonly ObservableCollection<FileEntry> _files = [];
    List<DuplicateGroup> _dupes = [];
    DiskNode? _root;
    CancellationTokenSource? _cts, _dupeCts;

    /// <summary>Raised after files are recycled so drive usage and health can update.</summary>
    public event EventHandler? Changed;

    public DiskView()
    {
        InitializeComponent();
        PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DriveButtons.ItemsSource = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.Name.TrimEnd('\\'))
            .ToList();
        TreeGrid.ItemsSource = _rows;
        FileGrid.ItemsSource = _files;
        Map.Hovered += n => HoverText.Text = n is null
            ? "Hover over the map to see what each block is."
            : $"{n.FullPath}   ·   {Fmt.Bytes(n.Size)}" + (n.IsDirectory ? $"   ·   {n.FileCount:N0} files" : "");
        Map.Clicked += Reveal;
    }

    IntPtr OwnerHandle => new WindowInteropHelper(Window.GetWindow(this)).Handle;

    // ---------- Scanning ----------

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        await ScanAsync(PathBox.Text.Trim());
    }

    public async Task ScanAsync(string root)
    {
        if (_cts is not null) return;
        if (!Directory.Exists(root))
        {
            StatusText.Text = $"Folder not found: {root}";
            return;
        }

        root = Path.GetFullPath(root);
        PathBox.Text = root;
        _cts = new CancellationTokenSource();
        ScanButton.Content = "Cancel";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        _rows.Clear();
        _files.Clear();
        Map.Root = null;

        var sw = Stopwatch.StartNew();
        var progress = new Progress<int>(n => StatusText.Text = $"Scanning {root}… {n:N0} files so far");
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => DiskAnalyzer.Scan(root, progress, token), token);
            _root = result.Root;
            foreach (var f in result.LargestFiles) _files.Add(f);
            var top = new DiskRow { Node = _root, Depth = 0 };
            _rows.Add(top);
            Expand(top);
            Map.Root = _root;
            StatusText.Text = $"{Fmt.Bytes(_root.Size)} in {_root.FileCount:N0} files under {root} · scanned in {sw.Elapsed.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException) { StatusText.Text = "Scan cancelled."; }
        catch (Exception ex) { StatusText.Text = $"Couldn't scan {root}: {ex.Message}"; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ScanButton.Content = "Scan";
            Progress.Visibility = Visibility.Hidden;
        }
    }

    async void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string drive) await ScanAsync(drive + "\\");
    }

    async void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(PathBox.Text.Trim().TrimEnd('\\'));
        if (parent is not null) await ScanAsync(parent.FullName);
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = PathBox.Text };
        if (dialog.ShowDialog() == true) await ScanAsync(dialog.FolderName);
    }

    async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ScanAsync(PathBox.Text.Trim());
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (TreePanel is null) return; // during InitializeComponent
        TreePanel.Visibility = TabTree.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = TabFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DupesPanel.Visibility = TabDupes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TidyPanel.Visibility = TabTidy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Tidy up ----------

    List<TidyItem> _tidy = [];

    async void FindTidy_Click(object sender, RoutedEventArgs e)
    {
        FindTidyButton.IsEnabled = false;
        TidyText.Text = "Looking…";
        var root = _root;
        _tidy = await Task.Run(() => (root is null ? [] : Tidy.EmptyFolders(root)).Concat(Tidy.BrokenShortcuts()).ToList());
        TidyList.ItemsSource = _tidy;
        int empty = _tidy.Count(t => t.Kind == "Empty folder"), broken = _tidy.Count - empty;
        TidyText.Text = (root is null ? "Scan a folder first to find empty folders. " : $"{empty} empty folder(s) under {root.FullPath}. ") +
                        $"{broken} broken shortcut(s).";
        FindTidyButton.IsEnabled = true;
        TidyCheck_Click(this, null!);
    }

    void TidyCheck_Click(object sender, RoutedEventArgs e)
    {
        int n = _tidy.Count(t => t.IsChecked);
        RecycleTidyButton.IsEnabled = n > 0;
        RecycleTidyButton.Content = n > 0 ? $"Recycle {n} ticked" : "Recycle ticked";
    }

    async void RecycleTidy_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _tidy.Where(t => t.IsChecked).ToList();
        var gone = (await RecycleAsync(ticked.Select(t => (t.FullPath, 0L)).ToList())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ticked.Where(t => gone.Contains(t.FullPath)))
        {
            _tidy.Remove(t);
            if (t.Node is { } node)
            {
                var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
                if (row is not null) { Collapse(row); _rows.Remove(row); }
                DiskAnalyzer.Remove(node);
            }
        }
        TidyList.ItemsSource = null;
        TidyList.ItemsSource = _tidy;
        TidyCheck_Click(this, null!);
        if (_root is not null) AfterTreeChanged();
    }

    // ---------- Tree ----------

    void Expand(DiskRow row)
    {
        if (row.IsExpanded || !row.CanExpand) return;
        if (row.Node.LooseCount > 0) DiskAnalyzer.LoadFiles(row.Node);
        var children = (row.Node.Folders ?? []).Concat(row.Node.Files ?? [])
            .OrderByDescending(n => n.Size)
            .Select(n => new DiskRow { Node = n, Depth = row.Depth + 1 })
            .ToList();
        int at = _rows.IndexOf(row) + 1;
        foreach (var c in children) _rows.Insert(at++, c);
        row.IsExpanded = true;
    }

    void Collapse(DiskRow row)
    {
        if (!row.IsExpanded) return;
        int i = _rows.IndexOf(row) + 1;
        while (i < _rows.Count && _rows[i].Depth > row.Depth) _rows.RemoveAt(i);
        row.IsExpanded = false;
    }

    void Toggle(DiskRow row)
    {
        if (row.IsExpanded) Collapse(row); else Expand(row);
    }

    void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is DiskRow row) Toggle(row);
    }

    void TreeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeGrid.SelectedItem is not DiskRow row) return;
        if (row.Node.IsDirectory) Toggle(row);
        else Shell.Reveal(row.Node.FullPath);
    }

    void TreeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Map.Selected = (TreeGrid.SelectedItem as DiskRow)?.Node;

    async void TreeGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TreeGrid.SelectedItem is not DiskRow row) return;
        switch (e.Key)
        {
            case Key.Delete:
                e.Handled = true;
                await DeleteNodesAsync(TreeGrid.SelectedItems.Cast<DiskRow>().Select(r => r.Node).ToList());
                break;
            case Key.Right:
                e.Handled = true;
                Expand(row);
                break;
            case Key.Left:
                e.Handled = true;
                if (row.IsExpanded) Collapse(row);
                else if (_rows.FirstOrDefault(r => ReferenceEquals(r.Node, row.Node.Parent)) is { } parent) Select(parent);
                break;
            case Key.Enter:
                e.Handled = true;
                if (row.Node.IsDirectory) Toggle(row);
                break;
        }
    }

    /// <summary>Expands every ancestor of node, then selects and scrolls to it (used by treemap clicks).</summary>
    void Reveal(DiskNode node)
    {
        var chain = new Stack<DiskNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Push(n);
        DiskRow? row = null;
        foreach (var n in chain)
        {
            row = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, n));
            if (row is null) return;
            if (!ReferenceEquals(n, node)) Expand(row);
        }
        if (row is not null) Select(row);
    }

    void Select(DiskRow row)
    {
        TreeGrid.SelectedItem = row;
        TreeGrid.ScrollIntoView(row);
        TreeGrid.Focus();
    }

    // ---------- Deleting ----------

    /// <summary>Checks protection, confirms, recycles, then updates tree, map and lists. Returns the deleted paths.</summary>
    async Task<List<string>> RecycleAsync(List<(string Path, long Size)> items)
    {
        var blocked = items.Select(i => (i.Path, Why: DiskAnalyzer.WhyProtected(i.Path))).Where(b => b.Why is not null).ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show("These can't be deleted here:\n\n" +
                            string.Join("\n", blocked.Take(6).Select(b => $"  • {Path.GetFileName(b.Path.TrimEnd('\\'))}: {b.Why}")),
                "Protected", MessageBoxButton.OK, MessageBoxImage.Information);
            items = items.Where(i => DiskAnalyzer.WhyProtected(i.Path) is null).ToList();
        }
        if (items.Count == 0) return [];

        long size = items.Sum(i => i.Size);
        string what = items.Count == 1 ? $"\"{Path.GetFileName(items[0].Path.TrimEnd('\\'))}\"" : $"{items.Count} items";
        if (MessageBox.Show($"Move {what} ({Fmt.Bytes(size)}) to the Recycle Bin?\n\n" +
                            "You can restore from the Recycle Bin. Space is freed once it's emptied.",
                "Move to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return [];

        var paths = items.Select(i => i.Path).ToList();
        StatusText.Text = $"Moving {what} to the Recycle Bin…";
        await Task.Yield();
        DiskAnalyzer.Recycle(OwnerHandle, paths); // shows Windows' own progress for big folders
        var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        StatusText.Text = gone.Count == paths.Count
            ? $"Moved {what} to the Recycle Bin."
            : $"{paths.Count - gone.Count} item(s) couldn't be moved; they may be open in another program.";
        if (gone.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return gone;
    }

    async Task DeleteNodesAsync(List<DiskNode> nodes)
    {
        // Skip the scan root and anything whose parent is also selected.
        var set = nodes.ToHashSet();
        nodes = nodes.Where(n => n.Parent is not null && !Ancestors(n).Any(set.Contains)).ToList();
        if (nodes.Count == 0) return;

        var gone = (await RecycleAsync(nodes.Select(n => (n.FullPath, n.Size)).ToList()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(n => gone.Contains(n.FullPath)))
        {
            var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
            if (row is not null) { Collapse(row); _rows.Remove(row); }
            DiskAnalyzer.Remove(node);
            var prefix = node.FullPath.TrimEnd('\\') + "\\";
            foreach (var f in _files.Where(f => f.FullPath.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase) ||
                                                f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _files.Remove(f);
        }
        AfterTreeChanged();
    }

    static IEnumerable<DiskNode> Ancestors(DiskNode n)
    {
        for (var p = n.Parent; p is not null; p = p.Parent) yield return p;
    }

    void AfterTreeChanged()
    {
        foreach (var r in _rows) r.Refresh();
        Map.Invalidate();
        if (_root is not null)
            StatusText.Text += $"   ·   {Fmt.Bytes(_root.Size)} left in {_root.FileCount:N0} files";
    }

    // ---------- Largest files ----------

    void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = FileGrid.SelectedItems.Count;
        RevealButton.IsEnabled = n == 1;
        DeleteButton.IsEnabled = n > 0;
        DeleteButton.Content = n > 1 ? $"Recycle {n} files" : "Move to Recycle Bin";
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is FileEntry f) Shell.Reveal(f.FullPath);
    }

    async void DeleteFiles_Click(object sender, RoutedEventArgs e) => await DeleteSelectedFilesAsync();

    async void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        await DeleteSelectedFilesAsync();
    }

    async Task DeleteSelectedFilesAsync()
    {
        var selected = FileGrid.SelectedItems.Cast<FileEntry>().ToList();
        if (selected.Count == 0) return;
        var gone = (await RecycleAsync(selected.Select(f => (f.FullPath, f.Size)).ToList()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in selected.Where(f => gone.Contains(f.FullPath)))
        {
            _files.Remove(f);
            RemoveFromTree(f.FullPath, f.Size);
        }
        AfterTreeChanged();
    }

    void RemoveFromTree(string path, long size)
    {
        if (_root is null) return;
        var row = _rows.FirstOrDefault(r => !r.Node.IsDirectory && r.Node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (row is not null) _rows.Remove(row);
        DiskAnalyzer.RemoveFile(_root, path, size);
    }

    // ---------- Duplicates ----------

    async void FindDupes_Click(object sender, RoutedEventArgs e)
    {
        if (_dupeCts is not null) { _dupeCts.Cancel(); return; }
        var root = PathBox.Text.Trim();
        if (!Directory.Exists(root)) { DupesText.Text = $"Folder not found: {root}"; return; }

        _dupeCts = new CancellationTokenSource();
        FindDupesButton.Content = "Cancel";
        TickExtrasButton.IsEnabled = RecycleDupesButton.IsEnabled = false;
        DupeList.ItemsSource = null;
        var progress = new Progress<string>(s => DupesText.Text = s);
        var sw = Stopwatch.StartNew();
        try
        {
            var token = _dupeCts.Token;
            _dupes = await Task.Run(() => DuplicateFinder.Find(root, 1024 * 1024, progress, token), token);
            DupeList.ItemsSource = _dupes;
            DupesText.Text = _dupes.Count == 0
                ? $"No duplicate files (1 MB+) under {root}."
                : $"{_dupes.Count:N0} sets of duplicates · {Fmt.Bytes(_dupes.Sum(g => g.Wasted))} could be freed · {sw.Elapsed.TotalSeconds:0}s";
            TickExtrasButton.IsEnabled = _dupes.Count > 0;
        }
        catch (OperationCanceledException) { DupesText.Text = "Duplicate search cancelled."; }
        catch (Exception ex) { DupesText.Text = $"Couldn't search: {ex.Message}"; }
        finally
        {
            _dupeCts.Dispose();
            _dupeCts = null;
            FindDupesButton.Content = "Find duplicates";
        }
    }

    /// <summary>Keeps the newest copy in each set (listed first) and ticks the others.</summary>
    void TickExtras_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _dupes)
            for (int i = 0; i < g.Files.Count; i++) g.Files[i].IsChecked = i > 0;
        UpdateDupeButton();
    }

    void DupeCheck_Click(object sender, RoutedEventArgs e) => UpdateDupeButton();

    void UpdateDupeButton()
    {
        var ticked = _dupes.SelectMany(g => g.Files.Select(f => (g, f))).Where(x => x.f.IsChecked).ToList();
        RecycleDupesButton.IsEnabled = ticked.Count > 0;
        RecycleDupesButton.Content = ticked.Count > 0
            ? $"Recycle {ticked.Count} ({Fmt.Bytes(ticked.Sum(x => x.g.Size))})"
            : "Recycle ticked";
    }

    async void RecycleDupes_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _dupes.SelectMany(g => g.Files.Where(f => f.IsChecked).Select(f => (g, f))).ToList();
        if (ticked.Count == 0) return;
        if (_dupes.Any(g => g.Files.All(f => f.IsChecked)) &&
            MessageBox.Show("Some sets have every copy ticked, so that file would be gone completely. Continue anyway?",
                "Every copy ticked", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var gone = (await RecycleAsync(ticked.Select(x => (x.f.FullPath, x.g.Size)).ToList()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (g, f) in ticked.Where(x => gone.Contains(x.f.FullPath)))
        {
            g.Files.Remove(f);
            RemoveFromTree(f.FullPath, g.Size);
        }
        _dupes = _dupes.Where(g => g.Files.Count > 1).ToList();
        DupeList.ItemsSource = _dupes;
        UpdateDupeButton();
        AfterTreeChanged();
    }
}
