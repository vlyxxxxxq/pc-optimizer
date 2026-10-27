using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

/// <summary>
/// Dims every screen and lets you drag out the area to record. A click without dragging picks the whole
/// screen under the cursor; Esc or right-click cancels.
/// </summary>
public sealed class RegionPicker : Window
{
    readonly TaskCompletionSource<ScreenArea?> _result = new();
    readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)) };
    readonly Rectangle _box = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)), StrokeThickness = 2, Visibility = Visibility.Collapsed };
    readonly Border _size = new() { Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x0F, 0x11, 0x17)), CornerRadius = new CornerRadius(4),
                                    Padding = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed };
    readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    Point? _start;
    Rect _selection;

    public RegionPicker()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // almost clear, but still catches the mouse
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _size.Child = _sizeText;
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1B, 0x1F, 0x2A)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 13,
                Text = "Drag to pick an area  ·  Click to record the whole screen  ·  Esc to cancel",
            },
        };
        var canvas = new Canvas();
        canvas.Children.Add(_dim);
        canvas.Children.Add(_box);
        canvas.Children.Add(_size);
        canvas.Children.Add(hint);
        Content = canvas;
        UpdateDim();

        Loaded += (_, _) =>
        {
            // Hint at the top of the screen the cursor is on.
            var cursor = Mouse.GetPosition(this);
            var screen = ScreenAt(cursor);
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, screen.Left + (screen.Width - hint.DesiredSize.Width) / 2);
            Canvas.SetTop(hint, screen.Top + 24);
            Activate();
            Focus();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        MouseRightButtonUp += (_, _) => Finish(null);
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_start is not { } s) return;
            _selection = new Rect(s, e.GetPosition(this));
            _box.Visibility = _size.Visibility = Visibility.Visible;
            Canvas.SetLeft(_box, _selection.X); Canvas.SetTop(_box, _selection.Y);
            _box.Width = _selection.Width; _box.Height = _selection.Height;
            var px = ToPixels(_selection);
            _sizeText.Text = $"{px.Width} × {px.Height}";
            Canvas.SetLeft(_size, _selection.X);
            Canvas.SetTop(_size, _selection.Bottom + 6);
            UpdateDim();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            if (_start is null) return;
            _start = null;
            // A tiny drag is a click: record the whole screen it's on.
            var area = _selection.Width < 8 || _selection.Height < 8 ? ScreenAt(e.GetPosition(this)) : _selection;
            Finish(ToPixels(area));
        };
        Deactivated += (_, _) => Finish(null);
    }

    /// <summary>Shows the picker; null if cancelled.</summary>
    public Task<ScreenArea?> PickAsync()
    {
        Show();
        return _result.Task;
    }

    void Finish(ScreenArea? area)
    {
        if (_result.Task.IsCompleted) return;
        _result.SetResult(area);
        Close();
    }

    void UpdateDim()
    {
        var all = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _dim.Data = _selection.IsEmpty || _box.Visibility != Visibility.Visible ? all
            : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_selection));
    }

    double Scale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>Window coordinates (DIPs) to screen pixels.</summary>
    ScreenArea ToPixels(Rect r)
    {
        double s = Scale;
        return new ScreenArea((int)Math.Round((Left + r.X) * s), (int)Math.Round((Top + r.Y) * s),
            (int)Math.Round(r.Width * s), (int)Math.Round(r.Height * s));
    }

    /// <summary>The monitor under a point, in window coordinates.</summary>
    Rect ScreenAt(Point p)
    {
        double s = Scale;
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = (int)((Left + p.X) * s), Y = (int)((Top + p.Y) * s) }, 2);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return new Rect(0, 0, Width, Height);
        var m = info.rcMonitor;
        return new Rect(m.Left / s - Left, m.Top / s - Top, (m.Right - m.Left) / s, (m.Bottom - m.Top) / s);
    }
}

/// <summary>
/// While recording: a dashed frame around the area and a small bar with the time and a Stop button.
/// Both are hidden from screen capture, so they never end up in the video.
/// </summary>
public sealed class RecordingBar : Window
{
    readonly Window _frame;
    readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                       Margin = new Thickness(8, 0, 12, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public event EventHandler? StopRequested;

    public RecordingBar(ScreenArea area, Func<TimeSpan> elapsed, string hotkey)
    {
        double s = 1;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var stop = new Button { Content = "Stop", Padding = new Thickness(12, 3, 12, 4), ToolTip = $"Stop and save ({hotkey})" };
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1B, 0x1F, 0x2A)), CornerRadius = new CornerRadius(18),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x38)), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 5, 5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0x5F, 0x5C)), VerticalAlignment = VerticalAlignment.Center }, _time, stop },
            },
        };

        _frame = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Content = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xF2, 0x5F, 0x5C)), StrokeThickness = 2, StrokeDashArray = [4, 3] },
        };

        _timer.Tick += (_, _) => { var t = elapsed(); _time.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}"; };
        _time.Text = "0:00";
        Loaded += (_, _) =>
        {
            s = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            // Frame just outside the area (so it's never in the shot), bar centred above it, or inside the top if there's no room.
            _frame.Left = area.X / s - 3; _frame.Top = area.Y / s - 3;
            _frame.Width = area.Width / s + 6; _frame.Height = area.Height / s + 6;
            _frame.Show();
            MakeUncapturable(_frame, clickThrough: true);
            MakeUncapturable(this, clickThrough: false);
            Left = area.X / s + (area.Width / s - ActualWidth) / 2;
            Top = area.Y / s - ActualHeight - 8 >= SystemParameters.VirtualScreenTop ? area.Y / s - ActualHeight - 8 : area.Y / s + 8;
            _timer.Start();
        };
        Closed += (_, _) => { _timer.Stop(); _frame.Close(); };
    }

    static void MakeUncapturable(Window w, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Native.SetWindowDisplayAffinity(hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+)
        var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }
}
