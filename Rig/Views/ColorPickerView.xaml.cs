using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rig.Services;

namespace Rig.Views;

public sealed record PickedColor(string Hex)
{
    public Color Color => (Color)ColorConverter.ConvertFromString(Hex);
    public Brush Brush => new SolidColorBrush(Color);
    public string Rgb => $"rgb({Color.R}, {Color.G}, {Color.B})";
    public string Hsl
    {
        get
        {
            double r = Color.R / 255.0, g = Color.G / 255.0, b = Color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
            if (max != min)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                h *= 60;
            }
            return $"hsl({h:0}, {s * 100:0}%, {l * 100:0}%)";
        }
    }
}

public partial class ColorPickerView : UserControl
{
    AppSettings? _settings;

    /// <summary>A colour was picked from this page (the main window also picks from the hotkey).</summary>
    public event EventHandler? PickRequested;
    public event EventHandler? MeasureRequested;
    public event EventHandler? HotkeysPaused, HotkeysChanged;

    public ColorPickerView() => InitializeComponent();

    public void Attach(AppSettings settings, string hotkey)
    {
        _settings = settings;
        HotkeyButton.Content = hotkey;
        _ = new ShortcutCapture(HotkeyButton, settings, nameof(AppSettings.ColorHotkey), () => HotkeysPaused?.Invoke(this, EventArgs.Empty), () => HotkeysChanged?.Invoke(this, EventArgs.Empty));
        Render();
    }

    public void Render()
    {
        if (_settings is null) return;
        History.ItemsSource = _settings.ColorHistory.Select(h => new PickedColor(h)).ToList();
        EmptyText.Visibility = _settings.ColorHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowMeasure((int W, int H) size) => MeasureText.Text = $"{size.W} × {size.H} px";

    void Pick_Click(object sender, RoutedEventArgs e) => PickRequested?.Invoke(this, EventArgs.Empty);
    void Measure_Click(object sender, RoutedEventArgs e) => MeasureRequested?.Invoke(this, EventArgs.Empty);

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string text) try { Clipboard.SetText(text); } catch { }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.ColorHistory.Clear();
        _settings.Save();
        Render();
    }
}
