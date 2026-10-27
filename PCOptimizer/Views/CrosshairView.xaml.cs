using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class CrosshairView : UserControl
{
    AppSettings? _settings;
    bool _loading = true, _gamesLoaded;
    bool _capturing;

    static readonly string[] Colors = ["#00FF66", "#00E5FF", "#FFFF00", "#FF3B3B", "#FF00FF", "#FFFFFF", "#FF9F1A"];

    /// <summary>Style changed (the overlay redraws), "Show now" toggled, or the game list changed.</summary>
    public event EventHandler? Changed;
    public event Action<bool>? ShowRequested;
    public event EventHandler? HotkeyPaused, HotkeyChanged;

    public CrosshairView() => InitializeComponent();

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        var s = settings.Crosshair;
        foreach (var rb in new[] { ShapeCrossDot, ShapeCross, ShapeDot, ShapeCircle, ShapeT })
            rb.IsChecked = (string)rb.Tag == s.Shape.ToString();
        SizeSlider.Value = s.Size;
        ThickSlider.Value = s.Thickness;
        GapSlider.Value = s.Gap;
        OpacitySlider.Value = s.Opacity;
        OutlineSwitch.IsChecked = s.Outline;
        foreach (var c in Colors)
        {
            var swatch = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0), Tag = c, Cursor = Cursors.Hand,
                Template = SwatchTemplate((Color)ColorConverter.ConvertFromString(c)),
            };
            swatch.Click += (_, _) => { settings.Crosshair.Color = c; Save(); };
            Swatches.Children.Add(swatch);
        }
        HotkeyButton.Content = settings.CrosshairHotkey.Replace("+", " + ");
        _loading = false;
        Redraw();
    }

    static ControlTemplate SwatchTemplate(Color c)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    public void SetShowing(bool on) => ShowSwitch.IsChecked = on;

    public async void EnsureLoaded()
    {
        if (_gamesLoaded || _settings is null) return;
        _gamesLoaded = true;
        GamesHint.Text = "Looking for games…";
        var games = await Task.Run(InstalledGames.Find);
        GamesHint.Text = "The crosshair appears while one of these games is the window you're playing, and disappears when you alt-tab out or close it.";
        foreach (var g in games.OrderBy(g => g.Name)) AddGameToggle(g.Name, g.Folder, null);
        // Games added by hand that aren't in a launcher library.
        foreach (var c in _settings.CrosshairGames.Where(c => c.Exe is not null)) AddGameToggle(c.Name, null, c.Exe);
    }

    void AddGameToggle(string name, string? folder, string? exe)
    {
        if (_settings is null) return;
        bool on = _settings.CrosshairGames.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var box = new CheckBox { Content = name, IsChecked = on, Margin = new Thickness(0, 0, 18, 10), Foreground = (Brush)FindResource("TextBrush") };
        box.Click += (_, _) =>
        {
            _settings.CrosshairGames.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (box.IsChecked == true) _settings.CrosshairGames.Add(new CrosshairGame { Name = name, Folder = folder, Exe = exe });
            _settings.Save();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        GameList.Children.Add(box);
    }

    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Games (*.exe)|*.exe", Title = "Pick the game's .exe" };
        if (dialog.ShowDialog() != true) return;
        var exe = Path.GetFileName(dialog.FileName);
        var name = Path.GetFileNameWithoutExtension(exe);
        if (_settings.CrosshairGames.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _settings.CrosshairGames.Add(new CrosshairGame { Name = name, Exe = exe });
        _settings.Save();
        AddGameToggle(name, null, exe);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.Crosshair.Shape = Enum.Parse<CrosshairShape>((string)((FrameworkElement)sender).Tag);
        Save();
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _settings is null) return;
        var s = _settings.Crosshair;
        s.Size = SizeSlider.Value; s.Thickness = ThickSlider.Value; s.Gap = GapSlider.Value; s.Opacity = Math.Round(OpacitySlider.Value, 2);
        Save();
    }

    void Outline_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.Crosshair.Outline = OutlineSwitch.IsChecked == true;
        Save();
    }

    void ShowSwitch_Click(object sender, RoutedEventArgs e) => ShowRequested?.Invoke(ShowSwitch.IsChecked == true);

    void Save()
    {
        _settings?.Save();
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Redraw()
    {
        if (_settings is null) return;
        Preview.Children.Clear();
        // Drawn at 2x so the details are easy to see; the real one is 1:1.
        Preview.RenderTransform = new ScaleTransform(2, 2, 150, 150);
        CrosshairOverlay.Draw(Preview, _settings.Crosshair, 150, 150);
    }

    // --- Hotkey capture (same as on Clips & Screenshots) ---
    void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) return;
        _capturing = true;
        HotkeyButton.Content = "Press keys…  (Esc cancels)";
        HotkeyButton.Focus();
        HotkeyPaused?.Invoke(this, EventArgs.Empty);
    }

    void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing || _settings is null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { EndCapture(); return; }
        var hotkey = new Hotkey(Keyboard.Modifiers & ~ModifierKeys.Windows, key);
        if (!hotkey.IsUsable) { HotkeyButton.Content = "Add Ctrl or Alt (or use an F-key)"; return; }
        _settings.CrosshairHotkey = hotkey.ToString();
        _settings.Save();
        EndCapture();
    }

    void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_capturing) EndCapture(); }

    void EndCapture()
    {
        _capturing = false;
        if (_settings is not null) HotkeyButton.Content = _settings.CrosshairHotkey.Replace("+", " + ");
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowHotkeyState(bool ok)
    {
        HotkeyText.Text = ok ? "Show or hide the crosshair from inside any game." : $"Another app is already using {_settings?.CrosshairHotkey}. Click it to pick a different key.";
        HotkeyText.Foreground = (Brush)FindResource(ok ? "MutedBrush" : "WarnBrush");
    }
}
