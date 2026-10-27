using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class ClipsView : UserControl
{
    readonly ObservableCollection<CaptureItem> _items = [];
    readonly List<ShortcutCapture> _editors = [];
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    List<CaptureItem> _all = [];
    HashSet<string> _favourites = new(StringComparer.OrdinalIgnoreCase);
    AppSettings? _settings;
    CaptureStudio? _studio;
    bool _loaded, _filling, _preview;
    int _generation;
    int _pageSize = 60, _thumbnailGeneration;
    public event EventHandler? HotkeysChanged, HotkeysPaused, ScreenshotRequested, RecordRequested, ReplayRequested, SaveClipRequested, RegionRequested;
    public ClipsView()
    {
        InitializeComponent(); Gallery.ItemsSource = _items;
        _clock.Tick += (_, _) => RenderStudio();
        IsVisibleChanged += (_, _) => { if (IsVisible && !_preview) { RenderStudio(); _clock.Start(); } else _clock.Stop(); };
        Unloaded += (_, _) => _clock.Stop();
    }
    public sealed record MonitorChoice(int Index, string Name) { public override string ToString() => Name; }
    public void Attach(AppSettings settings, CaptureStudio? studio = null, bool preview = false)
    {
        _filling = true; _settings = settings; _studio = studio; _preview = preview;
        ReplayLengthBox.ItemsSource = new[] { 15, 30, 60, 90, 120 };
        HeightBox.ItemsSource = new[] { 720, 1080, 1440, 2160 };
        FpsBox.ItemsSource = new[] { 30, 60 };
        QualityBox.ItemsSource = new[] { "High · 1080p60", "Balanced · 1080p30", "Compact · 720p30", "Custom" };
        QualityBox.SelectedIndex = (settings.CaptureHeight, settings.CaptureFps, settings.CaptureMbps) switch { (1080, 60, 16) => 0, (1080, 30, 10) => 1, (720, 30, 6) => 2, _ => 3 };
        FilterBox.ItemsSource = new[] { "All captures", "Clips", "Recordings", "Screenshots", "GIFs", "Favourites" }; FilterBox.SelectedIndex = 0;
        var monitors = new List<MonitorChoice> { new(-1, "Game's display / primary") };
        if (!preview)
        {
            int i = 0;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens) monitors.Add(new(i++, $"Display {i} · {screen.Bounds.Width} × {screen.Bounds.Height}"));
        }
        MonitorBox.ItemsSource = monitors;
        OutputDeviceBox.ItemsSource = new[] { new CaptureDevice("", "Windows default device") };
        MicDeviceBox.ItemsSource = new[] { new CaptureDevice("", "Windows default device") };
        DataContext = settings;
        OutputDeviceBox.SelectedValue = settings.CaptureOutputDevice ?? "";
        MicDeviceBox.SelectedValue = settings.CaptureMicDevice ?? "";
        if (string.IsNullOrEmpty(settings.CaptureOutputDevice)) OutputDeviceBox.SelectedIndex = 0;
        if (string.IsNullOrEmpty(settings.CaptureMicDevice)) MicDeviceBox.SelectedIndex = 0;
        HotkeySwitch.IsChecked = settings.CaptureHotkeys;
        foreach (var (button, property) in new[] { (ClipKeyButton, nameof(AppSettings.ClipHotkey)), (ShotKeyButton, nameof(AppSettings.ScreenshotHotkey)),
                     (RecordKeyButton, nameof(AppSettings.RecordHotkey)), (ReplayKeyButton, nameof(AppSettings.ReplayHotkey)) })
            _editors.Add(new ShortcutCapture(button, settings, property, () => HotkeysPaused?.Invoke(this, EventArgs.Empty), () => HotkeysChanged?.Invoke(this, EventArgs.Empty)));
        if (!preview)
        {
            try { _favourites = new(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppSettings.DataFile("capture-library.json"))) ?? [], StringComparer.OrdinalIgnoreCase); } catch { }
            _ = LoadAudioDevicesAsync();
        }
        _filling = false; RenderStudio(); ShowHotkeyState(true, true, true);
    }
    async Task LoadAudioDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(() => (Output: CaptureAudio.Devices(false).ToList(), Mic: CaptureAudio.Devices(true).ToList()));
            _filling = true;
            if (_settings?.CaptureOutputDevice is { Length: > 0 } output && devices.Output.All(d => d.Id != output)) devices.Output.Add(new(output, "Unavailable device — choose another"));
            if (_settings?.CaptureMicDevice is { Length: > 0 } mic && devices.Mic.All(d => d.Id != mic)) devices.Mic.Add(new(mic, "Unavailable device — choose another"));
            OutputDeviceBox.ItemsSource = devices.Output; MicDeviceBox.ItemsSource = devices.Mic;
            OutputDeviceBox.SelectedValue = _settings?.CaptureOutputDevice ?? ""; MicDeviceBox.SelectedValue = _settings?.CaptureMicDevice ?? "";
            if (string.IsNullOrEmpty(_settings?.CaptureOutputDevice)) OutputDeviceBox.SelectedIndex = 0;
            if (string.IsNullOrEmpty(_settings?.CaptureMicDevice)) MicDeviceBox.SelectedIndex = 0;
        }
        catch (Exception error) { CaptureError.Text = "Couldn't list audio devices: " + error.Message; CaptureError.Visibility = Visibility.Visible; }
        finally { _filling = false; }
    }
    public void EnsureLoaded()
    {
        RenderStudio(); foreach (var editor in _editors) editor.Render();
        ShowRecovery();
        if (_loaded || _preview) return; _loaded = true; Reload();
    }
    void ShowRecovery()
    {
        int count = _preview ? 0 : _studio?.Recoverable.Count ?? 0;
        RecoveryCard.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryText.Text = $"{count} interrupted recording(s) can be recovered. Completed segments are kept; the last unfinished segment may be missing. A recovered recording can include up to 2 seconds before its start.";
        RecoverButton.IsEnabled = _studio?.Busy != true;
    }
    async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (_studio is null) return;
        RecoverButton.IsEnabled = false; await _studio.RecoverAsync(); ShowRecovery(); Reload();
    }
    void RenderStudio()
    {
        if (_settings is null) return;
        bool busy = _studio?.Busy == true, replay = _studio?.ReplayEnabled == true, recording = _studio?.IsRecording == true;
        LiveBadge.Text = busy ? "WORKING" : recording ? "● RECORDING" : replay ? "● REPLAY ON" : "READY";
        StudioTitle.Text = recording ? $"Recording · {_studio!.Elapsed:mm\\:ss}" : replay ? $"{_studio!.BufferedSeconds:0}s ready to save" : "Never miss the good part.";
        StudioStatus.Text = _studio?.Status ?? "Enable replay before the moment happens. Then save the last few seconds with one key.";
        ReplayButton.Content = replay ? "Turn replay off" : "Enable replay";
        RecordAreaButton.Content = recording ? "Stop & save recording" : "Start recording";
        SaveClipButton.Content = $"Save last {_settings.ReplaySeconds} seconds";
        ReplayButton.IsEnabled = RecordAreaButton.IsEnabled = !busy;
        RegionButton.IsEnabled = !busy && _studio?.Active != true;
        SaveClipButton.IsEnabled = !busy && replay && _studio!.BufferedSeconds >= .5;
        CaptureSettings.IsEnabled = !busy && _studio?.Active != true;
        CaptureBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CaptureError.Text = _studio?.LastError ?? "";
        CaptureError.Visibility = string.IsNullOrEmpty(CaptureError.Text) ? Visibility.Collapsed : Visibility.Visible;
        SessionInfo.Text = _studio?.Active == true ? $"{_settings.CaptureHeight}p maximum · {_settings.CaptureFps} FPS · {_settings.CaptureMbps} Mbps · {_studio.Encoder}" : "Replay stays off until enabled. Microphone is optional. Saved videos stay on this PC.";
        CaptureFolderText.Text = "Save to: " + (_preview ? @"%Videos%\Rig" : _studio?.Folder ?? _settings.CaptureFolder ?? ScreenRecorder.Folder);
    }
    public void ShowRecording(bool recording) => RenderStudio();
    public void ShowHotkeyState(bool clipOk, bool shotOk, bool recordOk, bool replayOk = true)
    {
        foreach (var editor in _editors) editor.Render();
        HotkeyText.Text = _settings?.CaptureHotkeys != true ? "Capture shortcuts are off." : clipOk && shotOk && recordOk && replayOk
            ? "Shortcuts work in games. Click any key to change it; right-click to unbind or reset."
            : "A shortcut is unavailable. Another app may own it; click the key to choose a different combination.";
    }
    void Replay_Click(object sender, RoutedEventArgs e) => ReplayRequested?.Invoke(this, EventArgs.Empty);
    void SaveClip_Click(object sender, RoutedEventArgs e) => SaveClipRequested?.Invoke(this, EventArgs.Empty);
    void RecordArea_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, EventArgs.Empty);
    void Region_Click(object sender, RoutedEventArgs e) => RegionRequested?.Invoke(this, EventArgs.Empty);
    void CaptureOption_Click(object sender, RoutedEventArgs e) => SaveOptions();
    void CaptureOption_Changed(object sender, SelectionChangedEventArgs e) => SaveOptions();
    void CaptureLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveOptions();
    void SaveOptions() { if (_filling || _settings is null) return; _settings.Save(); RenderStudio(); }
    void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _settings is null || QualityBox.SelectedIndex == 3) return;
        var (height, fps, mbps) = QualityBox.SelectedIndex switch { 0 => (1080, 60, 16), 1 => (1080, 30, 10), _ => (720, 30, 6) };
        _filling = true; _settings.CaptureHeight = height; _settings.CaptureFps = fps; _settings.CaptureMbps = mbps;
        DataContext = null; DataContext = _settings; _filling = false; SaveOptions();
    }
    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where Rig saves captures", UseDescriptionForTitle = true, SelectedPath = _studio?.Folder ?? ScreenRecorder.Folder };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || _settings is null) return;
        string previous = _studio?.Folder ?? _settings.CaptureFolder ?? ScreenRecorder.Folder;
        if (!_settings.CaptureFolders.Contains(previous, StringComparer.OrdinalIgnoreCase)) _settings.CaptureFolders.Add(previous);
        _settings.CaptureFolder = dialog.SelectedPath; SaveOptions(); Reload();
    }
    void HotkeySwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return; _settings.CaptureHotkeys = HotkeySwitch.IsChecked == true; _settings.Save(); HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }
    async void Shot_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; var original = button.Content; button.IsEnabled = false;
        try { for (int s = 3; s > 0; s--) { button.Content = $"{s}…"; await Task.Delay(1000); } ScreenshotRequested?.Invoke(this, EventArgs.Empty); }
        finally { button.Content = original; button.IsEnabled = true; }
    }
    public async void Reload()
    {
        if (_preview) return;
        int generation = ++_generation;
        try
        {
            var list = await Task.Run(() => Captures.List(_studio?.Folder ?? _settings?.CaptureFolder, _settings?.CaptureFolders));
            if (generation != _generation) return;
            _all = list; foreach (var item in list) item.Favourite = _favourites.Contains(item.Path); Filter();
            ShowRecovery();
        }
        catch (Exception error) { GalleryText.Text = "Couldn't refresh captures: " + error.Message; }
    }
    void Filter(bool resetPage = true)
    {
        if (SearchBox is null || FilterBox is null) return;
        if (resetPage) _pageSize = 60;
        string search = SearchBox.Text.Trim(); int filter = FilterBox.SelectedIndex;
        var shown = _all.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && (filter switch
        { 1 => i.IsVideo && !i.IsRecording, 2 => i.IsRecording, 3 => !i.IsVideo && !i.IsGif, 4 => i.IsGif, 5 => i.Favourite, _ => true })).ToList();
        _items.Clear(); foreach (var item in shown.Take(_pageSize)) _items.Add(item);
        GalleryText.Text = shown.Count == 1 ? "1 capture" : shown.Count > _pageSize ? $"{_pageSize} of {shown.Count} captures" : $"{shown.Count} captures";
        MoreCapturesButton.Visibility = shown.Count > _pageSize ? Visibility.Visible : Visibility.Collapsed;
        EmptyGallery.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadThumbnailsAsync(_items.ToList(), ++_thumbnailGeneration);
    }
    async Task LoadThumbnailsAsync(List<CaptureItem> items, int generation)
    {
        foreach (var item in items)
        {
            if (generation != _thumbnailGeneration) return;
            if (item.Thumb is null) item.Thumb = await Task.Run(() => Thumbnails.For(item.Path, 512));
        }
    }
    void MoreCaptures_Click(object sender, RoutedEventArgs e) { _pageSize += 60; Filter(resetPage: false); }
    void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    void Filter_Changed(object sender, SelectionChangedEventArgs e) => Filter();
    static CaptureItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as CaptureItem;
    void Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        item.Favourite = !item.Favourite; if (item.Favourite) _favourites.Add(item.Path); else _favourites.Remove(item.Path);
        try
        {
            var path = AppSettings.DataFile("capture-library.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_favourites)); File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) { GalleryText.Text = "Couldn't save favourites: " + error.Message; }
        if (FilterBox.SelectedIndex == 5) Filter();
    }
    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!item.IsVideo) { Shell.Open(item.Path); return; }
        var editor = new ClipEditorWindow(item.Path, _studio?.Folder ?? ScreenRecorder.Folder) { Owner = Window.GetWindow(this) };
        editor.Saved += _ => Reload(); editor.Show();
    }
    void ItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        var reveal = new MenuItem { Header = "Show in folder" }; reveal.Click += (_, _) => Shell.Reveal(item.Path);
        var external = new MenuItem { Header = "Open in default player" }; external.Click += (_, _) => Shell.Open(item.Path);
        var delete = new MenuItem { Header = "Move to Recycle Bin" }; delete.Click += (_, _) =>
        {
            var owner = new WindowInteropHelper(Window.GetWindow(this)).Handle;
            if (DiskAnalyzer.Recycle(owner, [item.Path]) && !File.Exists(item.Path)) { _all.Remove(item); Filter(); }
        };
        menu.Items.Add(reveal); menu.Items.Add(external); menu.Items.Add(delete); menu.IsOpen = true;
    }
    void RecordFolder_Click(object sender, RoutedEventArgs e) { var folder = _studio?.Folder ?? ScreenRecorder.Folder; Directory.CreateDirectory(folder); Shell.Open(folder); }
    void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
}
