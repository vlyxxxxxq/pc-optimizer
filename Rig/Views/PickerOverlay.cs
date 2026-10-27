using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// Freezes the screen the cursor is on and lets you pick a pixel's colour (with a magnifier) or measure a
/// distance by dragging. Esc or right-click cancels.
/// </summary>
public sealed class PickerOverlay : Window
{
    public enum Mode { Color, Ruler }

    readonly Mode _mode;
    readonly BitmapSource _shot;
    readonly int[] _pixels;
    readonly int _w, _h;
    readonly double _scale;
    readonly Rectangle _loupe = new() { Width = 132, Height = 132, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Rectangle _cross = new() { Width = 12, Height = 12, Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };
    readonly Border _label = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false,
                                     Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x15, 0x18, 0x21)) };
    readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Consolas, Segoe UI") };
    readonly Border _swatch = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0),
                                      BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
    readonly Rectangle _measure = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)), StrokeThickness = 1.5,
                                          Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4F, 0x8C, 0xFF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly Canvas _canvas = new();
    Point? _dragStart;
    readonly TaskCompletionSource<object?> _result = new();

    PickerOverlay(Mode mode, Native.RECT monitor, double scale)
    {
        _mode = mode;
        _scale = scale;
        _w = monitor.Right - monitor.Left;
        _h = monitor.Bottom - monitor.Top;
        using (var bmp = new System.Drawing.Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, bmp.Size);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, _w, _h), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
            _pixels = new int[_w * _h];
            Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
            _shot = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * _h, data.Stride);
            bmp.UnlockBits(data);
        }
        _shot.Freeze();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = monitor.Left / scale; Top = monitor.Top / scale;
        Width = _w / scale; Height = _h / scale;
        Background = new ImageBrush(_shot) { Stretch = Stretch.Fill };

        _loupe.Fill = new ImageBrush(_shot) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_loupe, BitmapScalingMode.NearestNeighbor);
        _label.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatch, _labelText } };
        if (mode == Mode.Color) { _canvas.Children.Add(_loupe); _canvas.Children.Add(_cross); }
        else { _canvas.Children.Add(_measure); _swatch.Visibility = Visibility.Collapsed; }
        _canvas.Children.Add(_label);
        var hint = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1B, 0x1F, 0x2A)),
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 13,
                Text = mode == Mode.Color ? "Click to pick a colour  ·  arrow keys nudge  ·  Esc cancels" : "Drag to measure  ·  Esc cancels",
            },
        };
        Canvas.SetTop(hint, 24);
        hint.Loaded += (_, _) => Canvas.SetLeft(hint, (Width - hint.ActualWidth) / 2);
        _canvas.Children.Add(hint);
        Content = _canvas;

        KeyDown += OnKey;
        MouseMove += (_, e) => Update(e.GetPosition(this));
        MouseRightButtonUp += (_, _) => Finish(null);
        MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            var p = e.GetPosition(this);
            if (_mode == Mode.Color) Finish(ColorAt(p));
            else if (_dragStart is { } s) Finish(Measure(s, p));
        };
        Loaded += (_, _) => { Activate(); Focus(); Update(Mouse.GetPosition(this)); };
        Deactivated += (_, _) => Finish(null);
    }

    /// <summary>Shows the overlay on the screen the cursor is on. Returns a Color, a (width, height) in pixels, or null.</summary>
    public static Task<object?> ShowAsync(Mode mode)
    {
        Native.GetCursorPos(out var pt);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(pt, 2), ref info);
        double scale = PresentationSource.FromVisual(Application.Current.MainWindow!)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var overlay = new PickerOverlay(mode, info.rcMonitor, scale);
        overlay.Show();
        return overlay._result.Task;
    }

    (int X, int Y) Pixel(Point p) => (Math.Clamp((int)(p.X * _scale), 0, _w - 1), Math.Clamp((int)(p.Y * _scale), 0, _h - 1));

    Color ColorAt(Point p)
    {
        var (x, y) = Pixel(p);
        int argb = _pixels[y * _w + x];
        return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    static (int W, int H) Size(Point a, Point b, double scale) =>
        ((int)Math.Round(Math.Abs(b.X - a.X) * scale), (int)Math.Round(Math.Abs(b.Y - a.Y) * scale));

    object Measure(Point a, Point b) => Size(a, b, _scale);

    void Update(Point p)
    {
        if (_mode == Mode.Color)
        {
            var (x, y) = Pixel(p);
            var c = ColorAt(p);
            ((ImageBrush)_loupe.Fill).Viewbox = new Rect(x - 5.5, y - 5.5, 11, 11);
            double lx = p.X + 24, ly = p.Y + 24;
            if (lx + 140 > Width) lx = p.X - 156;
            if (ly + 170 > Height) ly = p.Y - 186;
            Canvas.SetLeft(_loupe, lx); Canvas.SetTop(_loupe, ly);
            Canvas.SetLeft(_cross, lx + 60); Canvas.SetTop(_cross, ly + 60);
            Canvas.SetLeft(_label, lx); Canvas.SetTop(_label, ly + 138);
            _swatch.Background = new SolidColorBrush(c);
            _labelText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}   {x}, {y}";
        }
        else if (_dragStart is { } s && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            var r = new Rect(s, p);
            _measure.Visibility = Visibility.Visible;
            Canvas.SetLeft(_measure, r.X); Canvas.SetTop(_measure, r.Y);
            _measure.Width = r.Width; _measure.Height = r.Height;
            var (w, h) = Size(s, p, _scale);
            _labelText.Text = $"{w} × {h} px   ·   {Math.Sqrt(w * w + h * h):0} px diagonal";
            Canvas.SetLeft(_label, r.Right + 10); Canvas.SetTop(_label, r.Bottom + 10);
        }
        else
        {
            _labelText.Text = "Drag to measure";
            Canvas.SetLeft(_label, p.X + 16); Canvas.SetTop(_label, p.Y + 16);
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(null); return; }
        if (_mode == Mode.Color && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter)
        {
            Native.GetCursorPos(out var pt);
            if (e.Key == Key.Enter) { Finish(ColorAt(Mouse.GetPosition(this))); return; }
            Native.SetCursorPos(pt.X + (e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0), pt.Y + (e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0));
            e.Handled = true;
        }
    }

    void Finish(object? result)
    {
        if (_result.Task.IsCompleted) return;
        _result.SetResult(result);
        Close();
    }
}
