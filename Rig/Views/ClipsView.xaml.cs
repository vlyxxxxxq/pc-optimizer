using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class ClipsView : UserControl
{
    readonly ObservableCollection<CaptureItem> _items = [];
    AppSettings? _settings;
    bool _loaded;

    /// <summary>The hotkeys were switched on or off.</summary>
    public event EventHandler? HotkeysChanged;
    /// <summary>"Take one in 3 s" was clicked; the main window takes it so the game's name is used.</summary>
    public event EventHandler? ScreenshotRequested;
    /// <summary>"Record an area" was clicked (or Stop, while recording).</summary>
    public event EventHandler? RecordRequested;

    public ClipsView()
    {
        InitializeComponent();
        Gallery.ItemsSource = _items;
    }

    public void Attach(AppSettings settings)
    {
        // Before _settings is set, so filling these in doesn't save anything.
        FormatMp4.IsChecked = !settings.RecordGif;
        FormatGif.IsChecked = settings.RecordGif;
        CursorCheck.IsChecked = settings.RecordCursor;
        _settings = settings;
        HotkeySwitch.IsChecked = settings.CaptureHotkeys;
    }

    public void ShowRecording(bool recording) =>
        RecordAreaButton.Content = recording ? "Stop recording" : "Record an area";

    void RecordArea_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, EventArgs.Empty);
    void RecordFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(ScreenRecorder.Folder);

    void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.RecordGif = FormatGif.IsChecked == true;
        _settings.RecordCursor = CursorCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>Shows whether the hotkeys could be registered (another app may own them).</summary>
    public void ShowHotkeyState(bool clipOk, bool shotOk, bool recordOk)
    {
        RenderKeys();
        var taken = new[] { clipOk ? null : _settings?.ClipHotkey, shotOk ? null : _settings?.ScreenshotHotkey,
                            recordOk ? null : _settings?.RecordHotkey }.OfType<string>().ToList();
        HotkeyText.Text = _settings?.CaptureHotkeys != true
            ? "Hotkeys are off."
            : taken.Count == 0 ? "Hotkeys work everywhere, even while a game has focus. Click a key to change it."
            : $"Another app is already using {string.Join(" and ", taken)}. Click it above to pick a different key.";
        HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource(taken.Count == 0 ? "MutedBrush" : "WarnBrush");
    }

    // --- Changing a hotkey: click the key button, press the new combination (Esc cancels) ---
    Button? _capturing;

    /// <summary>Raised when capture starts, so the current hotkeys are released and the keypress reaches this page.</summary>
    public event EventHandler? HotkeysPaused;

    void RenderKeys()
    {
        if (_settings is null) return;
        ClipKeyButton.Content = _settings.ClipHotkey.Replace("+", " + ");
        ShotKeyButton.Content = _settings.ScreenshotHotkey.Replace("+", " + ");
        RecordKeyButton.Content = _settings.RecordHotkey.Replace("+", " + ");
    }

    void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing is not null) return;
        _capturing = (Button)sender;
        _capturing.Content = "Press keys…  (Esc cancels)";
        _capturing.Focus();
        HotkeysPaused?.Invoke(this, EventArgs.Empty);
    }

    void KeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturing is null || _settings is null) return;
        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftShift
                or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
            return; // wait for the actual key
        if (key == System.Windows.Input.Key.Escape) { EndCapture(); return; }

        var hotkey = new Hotkey(System.Windows.Input.Keyboard.Modifiers & ~System.Windows.Input.ModifierKeys.Windows, key);
        if (!hotkey.IsUsable)
        {
            _capturing.Content = "Add Ctrl or Alt (or use an F-key)";
            return;
        }
        var others = new[] { (ClipKeyButton, _settings.ClipHotkey), (ShotKeyButton, _settings.ScreenshotHotkey), (RecordKeyButton, _settings.RecordHotkey) }
            .Where(o => o.Item1 != _capturing).Select(o => o.Item2);
        if (others.Contains(hotkey.ToString()))
        {
            _capturing.Content = "Already used for another one";
            return;
        }
        if (_capturing == ClipKeyButton) _settings.ClipHotkey = hotkey.ToString();
        else if (_capturing == ShotKeyButton) _settings.ScreenshotHotkey = hotkey.ToString();
        else _settings.RecordHotkey = hotkey.ToString();
        _settings.Save();
        EndCapture();
    }

    void KeyCapture_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_capturing == sender) EndCapture();
    }

    void EndCapture()
    {
        _capturing = null;
        HotkeysChanged?.Invoke(this, EventArgs.Empty); // re-registers and re-renders
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
        catch (Exception ex) { MessageBox.Show($"Couldn't change the setting:\n{ex.Message}", "Rig"); }
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
