using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class ClipsView : UserControl
{
    readonly ObservableCollection<CaptureItem> _items = [];
    AppSettings? _settings;
    bool _loaded;

    /// <summary>The hotkeys were switched on or off.</summary>
    public event EventHandler? HotkeysChanged;
    /// <summary>"Take one in 3 s" was clicked; the main window takes it so the game's name is used.</summary>
    public event EventHandler? ScreenshotRequested;

    public ClipsView()
    {
        InitializeComponent();
        Gallery.ItemsSource = _items;
    }

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        HotkeySwitch.IsChecked = settings.CaptureHotkeys;
    }

    /// <summary>Shows whether the hotkeys could be registered (another app may own them).</summary>
    public void ShowHotkeyState(bool clipOk, bool shotOk)
    {
        HotkeyText.Text = _settings?.CaptureHotkeys != true
            ? "Hotkeys are off."
            : clipOk && shotOk ? "Hotkeys work everywhere, even while a game has focus. A notification confirms each save."
            : $"Another app is already using {(clipOk ? "" : "Ctrl+Shift+F9 ")}{(shotOk ? "" : "Ctrl+Shift+F10")}. Close it or use the buttons above.";
    }

    public void EnsureLoaded()
    {
        RenderRecording();
        if (_loaded) return;
        _loaded = true;
        Reload();
    }

    void RenderRecording()
    {
        bool on = Captures.BackgroundRecording;
        ClipStatus.Text = on
            ? "Windows Game Bar is recording in the background, so the hotkey saves the last 30 seconds. Clips go to your Captures folder."
            : "Uses Windows Game Bar's \"Record what happened\". Turn on background recording first. It costs a few percent of GPU, like any recorder.";
        RecordButton.Content = on ? "Turn off background recording" : "Turn on background recording";
        RecordButton.Style = (Style)FindResource(on ? typeof(Button) : "PrimaryButton");
    }

    /// <summary>Reloads the gallery; thumbnails fill in afterwards at low priority.</summary>
    public async void Reload()
    {
        var list = await Task.Run(Captures.List);
        _items.Clear();
        foreach (var i in list) _items.Add(i);
        GalleryText.Text = list.Count == 0 ? "Nothing yet. Press a hotkey in a game and it shows up here." : list.Count == 1 ? "1 item" : $"{list.Count} items";
        foreach (var i in list)
        {
            await Dispatcher.InvokeAsync(() => i.Thumb = Thumbnails.For(i.Path, 256), DispatcherPriority.Background);
        }
    }

    void Record_Click(object sender, RoutedEventArgs e)
    {
        try { Captures.BackgroundRecording = !Captures.BackgroundRecording; }
        catch (Exception ex) { MessageBox.Show($"Couldn't change the setting:\n{ex.Message}", "PC Optimizer"); }
        RenderRecording();
    }

    void HotkeySwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.CaptureHotkeys = HotkeySwitch.IsChecked == true;
        _settings.Save();
        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    async void Shot_Click(object sender, RoutedEventArgs e)
    {
        // Give the user time to switch to the window they want.
        var button = (Button)sender;
        button.IsEnabled = false;
        for (int s = 3; s > 0; s--) { button.Content = $"{s}…"; await Task.Delay(1000); }
        button.Content = "Take one in 3 s";
        button.IsEnabled = true;
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    void ClipFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Captures.ClipFolder);
    void ShotFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Captures.ScreenshotFolder);
    void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Shell.Open(folder);
    }

    static CaptureItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as CaptureItem;

    void Open_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) Shell.Open(i.Path); }
    void Reveal_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) Shell.Reveal(i.Path); }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } i) return;
        var owner = new WindowInteropHelper(Window.GetWindow(this)).Handle;
        if (DiskAnalyzer.Recycle(owner, [i.Path]) && !File.Exists(i.Path)) _items.Remove(i);
    }
}
