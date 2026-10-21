using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class DiskView : UserControl
{
    readonly ObservableCollection<FileEntry> _files = [];
    CancellationTokenSource? _cts;

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
        FileGrid.ItemsSource = _files;
    }

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
        Progress.Value = 0;
        FolderList.ItemsSource = null;
        _files.Clear();

        var sw = Stopwatch.StartNew();
        var progress = new Progress<int>(n => StatusText.Text = $"Scanning {root}… {n:N0} files so far");
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => DiskAnalyzer.Scan(root, progress, token), token);
            FolderList.ItemsSource = result.Folders;
            foreach (var f in result.Files) _files.Add(f);
            StatusText.Text = $"{Fmt.Bytes(result.TotalSize)} in {result.FileCount:N0} files under {root} · " +
                              $"scanned in {sw.Elapsed.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't scan {root}: {ex.Message}";
        }
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

    async void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is FolderEntry { IsFolder: true } f && f.FullPath != PathBox.Text)
            await ScanAsync(f.FullPath);
    }

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

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileGrid.SelectedItems.Cast<FileEntry>().ToList();
        if (selected.Count == 0) return;

        var blocked = selected.Where(f => SafeIsProtected(f.FullPath)).ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show("These are Windows or installed-program files and can't be deleted here:\n\n" +
                            string.Join("\n", blocked.Take(5).Select(f => "  • " + f.Name)) +
                            "\n\nUninstall the program from Settings → Apps instead.",
                "Protected files", MessageBoxButton.OK, MessageBoxImage.Information);
            selected = selected.Except(blocked).ToList();
            if (selected.Count == 0) return;
        }

        long size = selected.Sum(f => f.Size);
        string what = selected.Count == 1 ? $"\"{selected[0].Name}\"" : $"{selected.Count} files";
        if (MessageBox.Show($"Move {what} ({Fmt.Bytes(size)}) to the Recycle Bin?\n\n" +
                            "You can restore from the Recycle Bin. Space is freed once it's emptied.",
                "Move to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int failed = 0;
        foreach (var f in selected)
        {
            if (DiskAnalyzer.Recycle(f.FullPath)) _files.Remove(f);
            else failed++;
        }
        StatusText.Text = failed == 0
            ? $"Moved {what} to the Recycle Bin."
            : $"{failed} file(s) couldn't be moved. They may be open in another program.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    static bool SafeIsProtected(string path)
    {
        try { return DiskAnalyzer.IsProtected(path); }
        catch { return true; }
    }
}
