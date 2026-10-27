using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// Small always-on-top, click-through box with live CPU / GPU / RAM / ping for use while gaming.
/// It never takes focus. Shows over borderless and windowed games; exclusive fullscreen hides it.
/// </summary>
public sealed class Overlay : Window
{
    readonly CpuSampler _cpu = new();
    readonly GpuLoad _gpu = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TextBlock _fpsText = Value(), _cpuText = Value(), _gpuText = Value(), _ramText = Value(), _pingText = Value();
    static FpsMonitor _fps => FpsMonitor.Shared;
    bool _fpsHeld;
    readonly Func<int?> _gamePid;
    int _tick;
    bool _pinging;
    int _corner;

    /// <param name="gamePid">The game Game Mode is boosting, if any; otherwise the busiest DirectX app is shown.</param>
    public Overlay(Func<int?> gamePid)
    {
        _gamePid = gamePid;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x0F, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x4F, 0x8C, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 12, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Item("FPS", _fpsText), Item("CPU", _cpuText), Item("GPU", _gpuText), Item("RAM", _ramText), Item("PING", _pingText, last: true) },
            },
        };
        _timer.Tick += async (_, _) => await TickAsync();
        SourceInitialized += (_, _) =>
        {
            // Click-through, no taskbar/alt-tab entry, never activates.
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        SizeChanged += (_, _) => Place();
    }

    static TextBlock Value() => new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Text = "–" };

    static StackPanel Item(string label, TextBlock value, bool last = false) => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 0, last ? 0 : 14, 0),
        Children =
        {
            new TextBlock { Text = label + " ", Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xAA, 0xFF)), FontSize = 11,
                            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center },
            value,
        },
    };

    public void ShowAt(int corner)
    {
        _corner = corner;
        if (!IsVisible) { Show(); _cpu.Sample(); _gpu.Sample(); _fps.Acquire(); _fpsHeld = true; _timer.Start(); }
        Place();
    }

    public void HideOverlay()
    {
        _timer.Stop();
        if (_fpsHeld) { _fps.Release(); _fpsHeld = false; }
        Hide();
    }

    /// <summary>Pins to a corner of the primary screen (full screen, not the work area, so it sits over games).</summary>
    void Place()
    {
        const double m = 12;
        double w = SystemParameters.PrimaryScreenWidth, h = SystemParameters.PrimaryScreenHeight;
        Left = _corner is 1 or 3 ? w - ActualWidth - m : m;
        Top = _corner is 2 or 3 ? h - ActualHeight - m - 40 : m;
    }

    async Task TickAsync()
    {
        double cpu = _cpu.Sample(), gpu = _gpu.Sample();
        var (total, available) = SystemInfo.Memory();
        bool needsAdmin = _fps.Problem == "admin";
        _fpsText.Text = needsAdmin ? "admin" : _fps.Read(_gamePid() ?? _fps.Busiest() ?? 0) is { } f ? $"{f.Fps:0} ({f.Low1:0} low)" : "–";
        _fpsText.Foreground = needsAdmin ? Brushes.Gray : Brushes.White;
        _cpuText.Text = $"{cpu:0}%";
        _gpuText.Text = $"{gpu:0}%";
        _ramText.Text = total > 0 ? $"{100.0 * (total - available) / total:0}%" : "–";
        _cpuText.Foreground = cpu > 90 ? Brushes.OrangeRed : Brushes.White;
        _gpuText.Foreground = gpu > 97 ? Brushes.Orange : Brushes.White;

        // Games can push themselves above other topmost windows; re-assert without stealing focus.
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        if (_tick++ % 2 == 0 && !_pinging)
        {
            _pinging = true;
            var ms = await NetworkTools.PingAsync("1.1.1.1");
            _pingText.Text = ms is { } v ? $"{v} ms" : "lost";
            _pingText.Foreground = ms is null or > 100 ? Brushes.OrangeRed : Brushes.White;
            _pinging = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_fpsHeld) { _fps.Release(); _fpsHeld = false; }
        _gpu.Dispose();
        base.OnClosed(e);
    }
}
