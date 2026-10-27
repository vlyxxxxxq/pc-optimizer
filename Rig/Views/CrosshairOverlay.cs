using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// A click-through crosshair pinned to the centre of the screen the game is on. Like the performance overlay it
/// never takes focus and only reads nothing from the game (it's a separate window drawn on top).
/// </summary>
public sealed class CrosshairOverlay : Window
{
    readonly Canvas _canvas = new();

    public CrosshairOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = 200;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
    }

    public void Apply(CrosshairStyle style)
    {
        _canvas.Children.Clear();
        Draw(_canvas, style, 100, 100);
    }

    /// <summary>Draws the crosshair centred on (cx, cy). Shared with the designer's preview.</summary>
    public static void Draw(Canvas canvas, CrosshairStyle s, double cx, double cy)
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(s.Color); } catch { color = Colors.Lime; }
        var fill = new SolidColorBrush(color) { Opacity = s.Opacity };
        var outline = new SolidColorBrush(Colors.Black) { Opacity = s.Opacity };
        double t = s.Thickness, len = s.Size, gap = s.Gap, o = s.Outline ? 1 : 0;

        void Rect(double x, double y, double w, double h)
        {
            if (o > 0) Add(new Rectangle { Width = w + 2 * o, Height = h + 2 * o, Fill = outline }, x - o, y - o);
            Add(new Rectangle { Width = w, Height = h, Fill = fill }, x, y);
        }
        void Add(FrameworkElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); canvas.Children.Add(e); }

        bool lines = s.Shape is CrosshairShape.Cross or CrosshairShape.CrossDot or CrosshairShape.T;
        if (lines)
        {
            Rect(cx - gap - len, cy - t / 2, len, t);               // left
            Rect(cx + gap, cy - t / 2, len, t);                     // right
            Rect(cx - t / 2, cy + gap, t, len);                     // bottom
            if (s.Shape != CrosshairShape.T) Rect(cx - t / 2, cy - gap - len, t, len); // top (a T-shape has none)
        }
        if (s.Shape is CrosshairShape.Dot or CrosshairShape.CrossDot)
        {
            double d = Math.Max(2, t + 1);
            if (o > 0) Add(new Ellipse { Width = d + 2 * o, Height = d + 2 * o, Fill = outline }, cx - d / 2 - o, cy - d / 2 - o);
            Add(new Ellipse { Width = d, Height = d, Fill = fill }, cx - d / 2, cy - d / 2);
        }
        if (s.Shape == CrosshairShape.Circle)
        {
            double r = Math.Max(4, len);
            if (o > 0) Add(new Ellipse { Width = 2 * r + 2, Height = 2 * r + 2, Stroke = outline, StrokeThickness = t + 2 }, cx - r - 1, cy - r - 1);
            Add(new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = fill, StrokeThickness = t }, cx - r, cy - r);
            Add(new Ellipse { Width = 2, Height = 2, Fill = fill }, cx - 1, cy - 1);
        }
    }

    /// <summary>Centres on the monitor showing the given window (the game), or the primary screen.</summary>
    public void ShowOn(IntPtr gameWindow)
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        var hwndSelf = new WindowInteropHelper(this).EnsureHandle();
        if (Native.GetMonitorInfo(Native.MonitorFromWindow(gameWindow == IntPtr.Zero ? Native.GetForegroundWindow() : gameWindow, 2), ref info))
        {
            var m = info.rcMonitor;
            int cx = (m.Left + m.Right) / 2, cy = (m.Top + m.Bottom) / 2;
            // Physical pixels, so the centre is exact at any display scaling.
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)(200 * dpi.DpiScaleX), h = (int)(200 * dpi.DpiScaleY);
            Native.SetWindowPos(hwndSelf, Native.HWND_TOPMOST, cx - w / 2, cy - h / 2, w, h, Native.SWP_NOACTIVATE);
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(hwndSelf, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
