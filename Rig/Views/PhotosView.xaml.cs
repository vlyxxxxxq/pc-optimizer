using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class PhotosView : UserControl
{
    string? _folder;
    List<PhotoGroup> _groups = [];
    CancellationTokenSource? _scan;

    public PhotosView()
    {
        InitializeComponent();
        if (Environment.GetEnvironmentVariable("RIG_TEST_PHOTOS") is { Length: > 0 } test) SetFolder(test);
        else SetFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
    }

    void SetFolder(string folder)
    {
        _folder = folder;
        FolderText.Text = folder;
        ScanButton.IsEnabled = Directory.Exists(folder);
    }

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with photos" };
        if (dialog.ShowDialog() == true) SetFolder(dialog.FolderName);
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is not null) { _scan.Cancel(); return; }
        if (_folder is null) return;
        _scan = new CancellationTokenSource();
        ScanButton.Content = "Stop";
        StatusText.Text = "Looking at photos…";
        var progress = new Progress<string>(s => StatusText.Text = s);
        var folder = _folder;
        bool sub = SubfoldersCheck.IsChecked == true;
        try
        {
            var token = _scan.Token;
            _groups = await Task.Run(() => SimilarPhotos.Find(folder, sub, progress, token));
            StatusText.Text = _groups.Count == 0 ? "No similar photos found." : "The best copy of each group is kept; the others are ticked.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped."; _groups = []; }
        catch (Exception ex) { StatusText.Text = $"Couldn't scan: {ex.Message}"; _groups = []; }
        finally { _scan = null; ScanButton.Content = "Scan"; }
        Groups.ItemsSource = _groups;
        Render();
        foreach (var p in _groups.SelectMany(g => g.Photos))
            await Dispatcher.InvokeAsync(() => p.Thumb = Thumbnails.For(p.Path, 256), DispatcherPriority.Background);
    }

    void Render()
    {
        var ticked = _groups.SelectMany(g => g.Photos).Where(p => p.Remove).ToList();
        SummaryText.Text = _groups.Count == 0 ? "" : $"{_groups.Count} group{(_groups.Count == 1 ? "" : "s")} · {ticked.Count} ticked ({Fmt.Bytes(ticked.Sum(p => p.Size))})";
        RecycleButton.IsEnabled = ticked.Count > 0;
        RecycleButton.Content = ticked.Count > 0 ? $"Move {ticked.Count} to Recycle Bin" : "Move ticked to Recycle Bin";
    }

    void Tick_Click(object sender, RoutedEventArgs e)
    {
        Render();
        Groups.Items.Refresh(); // group titles show the space to free
    }

    void Photo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string path) Shell.Open(path);
    }

    void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _groups.SelectMany(g => g.Photos).Where(p => p.Remove).ToList();
        if (ticked.Count == 0) return;
        // Never every copy: a group with nothing left unticked keeps its first photo.
        foreach (var g in _groups.Where(g => g.Photos.All(p => p.Remove))) { g.Photos[0].Remove = false; ticked.Remove(g.Photos[0]); }
        if (MessageBox.Show($"Move {ticked.Count} photo{(ticked.Count == 1 ? "" : "s")} ({Fmt.Bytes(ticked.Sum(p => p.Size))}) to the Recycle Bin?",
                "Similar Photos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DiskAnalyzer.Recycle(new WindowInteropHelper(Window.GetWindow(this)).Handle, ticked.Select(p => p.Path).ToList());
        foreach (var g in _groups) g.Photos.RemoveAll(p => !File.Exists(p.Path));
        _groups.RemoveAll(g => g.Photos.Count < 2);
        Groups.ItemsSource = null;
        Groups.ItemsSource = _groups;
        Render();
        StatusText.Text = "Moved to the Recycle Bin. You can restore them from there.";
    }
}
