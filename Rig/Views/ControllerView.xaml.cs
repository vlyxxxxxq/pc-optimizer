using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class ControllerView : UserControl
{
    // Polled at ~60 Hz only while the page is on screen.
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Dictionary<PadButton, Border> _buttons = [];
    readonly HashSet<PadButton> _pressedOnce = [];
    readonly Ellipse _leftDot = Dot(), _rightDot = Dot();
    int _pad;
    bool _wasConnected;
    (double Max, int Samples)? _driftLeft, _driftRight;

    static readonly (PadButton Button, string Label)[] Layout =
    [
        (PadButton.A, "A"), (PadButton.B, "B"), (PadButton.X, "X"), (PadButton.Y, "Y"),
        (PadButton.LB, "LB"), (PadButton.RB, "RB"), (PadButton.LeftStick, "L3"), (PadButton.RightStick, "R3"),
        (PadButton.Up, "↑"), (PadButton.Down, "↓"), (PadButton.Left, "←"), (PadButton.Right, "→"),
        (PadButton.Back, "View"), (PadButton.Start, "Menu"), (PadButton.Guide, "Xbox"),
    ];

    public ControllerView()
    {
        InitializeComponent();
        foreach (var (button, label) in Layout)
        {
            var b = new Border
            {
                Width = 62, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("InputBrush"),
                Child = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold },
            };
            _buttons[button] = b;
            ButtonGrid.Children.Add(b);
        }
        DrawStickFrame(LeftStick, _leftDot);
        DrawStickFrame(RightStick, _rightDot);
        _timer.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else { _timer.Stop(); Controllers.Rumble(_pad, 0, 0); } };
    }

    static Ellipse Dot() => new() { Width = 18, Height = 18, Fill = (Brush)Application.Current.Resources["AccentBrush"] };

    void DrawStickFrame(Canvas c, Ellipse dot)
    {
        var line = (Brush)FindResource("CardBorderBrush");
        c.Children.Add(new Ellipse { Width = 140, Height = 140, Stroke = line, StrokeThickness = 2, Fill = (Brush)FindResource("InputBrush") });
        // 10% dead-zone ring: drift inside it is usually ignored by games.
        var dz = new Ellipse { Width = 14, Height = 14, Stroke = line, StrokeDashArray = [2, 2] };
        Canvas.SetLeft(dz, 63); Canvas.SetTop(dz, 63);
        c.Children.Add(dz);
        c.Children.Add(new Line { X1 = 70, X2 = 70, Y1 = 0, Y2 = 140, Stroke = line });
        c.Children.Add(new Line { X1 = 0, X2 = 140, Y1 = 70, Y2 = 70, Stroke = line });
        c.Children.Add(dot);
        Place(dot, 0, 0);
    }

    static void Place(Ellipse dot, double x, double y)
    {
        Canvas.SetLeft(dot, 70 + x * 61 - 9);
        Canvas.SetTop(dot, 70 - y * 61 - 9);
    }

    void Slot_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Controllers.Rumble(_pad, 0, 0);
        _pad = int.Parse((string)((RadioButton)sender).Tag);
        _pressedOnce.Clear();
        _wasConnected = false;
    }

    void Poll()
    {
        var s = Controllers.Read(_pad);
        if (!s.Connected)
        {
            StatusText.Text = "Not connected. Plug in or pair a controller (PlayStation pads need Steam Input or DS4Windows).";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            _wasConnected = false;
            return;
        }
        if (!_wasConnected) { _wasConnected = true; StatusText.Text = $"Connected · {Controllers.Battery(_pad)}".TrimEnd(' ', '·'); }
        StatusText.Foreground = (Brush)FindResource("GoodBrush");

        Place(_leftDot, s.LX, s.LY);
        Place(_rightDot, s.RX, s.RY);
        LeftText.Text = $"X {s.LX,6:+0.00;-0.00}  Y {s.LY,6:+0.00;-0.00}";
        RightText.Text = $"X {s.RX,6:+0.00;-0.00}  Y {s.RY,6:+0.00;-0.00}";
        LTBar.Height = 140 * s.LT;
        RTBar.Height = 140 * s.RT;
        TriggerText.Text = $"LT {s.LT * 100,3:0}%     RT {s.RT * 100,3:0}%";

        var accent = (Brush)FindResource("AccentBrush");
        var tested = (Brush)Application.Current.Resources["SelectedBrush"];
        var idle = (Brush)FindResource("InputBrush");
        foreach (var (button, border) in _buttons)
        {
            bool down = s.Has(button);
            if (down) _pressedOnce.Add(button);
            border.Background = down ? accent : _pressedOnce.Contains(button) ? tested : idle;
        }
        ButtonsTested.Text = _pressedOnce.Count == _buttons.Count
            ? "Every button works."
            : $"{_pressedOnce.Count} of {_buttons.Count} buttons tested. Pressed ones stay highlighted.";

        if (_driftLeft is { } l) _driftLeft = (Math.Max(l.Max, Math.Sqrt(s.LX * s.LX + s.LY * s.LY)), l.Samples + 1);
        if (_driftRight is { } r) _driftRight = (Math.Max(r.Max, Math.Sqrt(s.RX * s.RX + s.RY * s.RY)), r.Samples + 1);
    }

    async void Drift_Click(object sender, RoutedEventArgs e)
    {
        if (!Controllers.Read(_pad).Connected) { DriftText.Text = "Connect a controller first."; return; }
        DriftButton.IsEnabled = false;
        DriftText.Foreground = (Brush)FindResource("MutedBrush");
        DriftText.Text = "Hands off the sticks…";
        _driftLeft = (0, 0);
        _driftRight = (0, 0);
        await Task.Delay(5000);
        var (left, right) = (_driftLeft!.Value.Max, _driftRight!.Value.Max);
        _driftLeft = _driftRight = null;
        DriftButton.IsEnabled = true;

        string Verdict(string name, double v) => v switch
        {
            < 0.05 => $"{name}: {v:P0} off-centre, perfect.",
            < 0.10 => $"{name}: {v:P0} off-centre, fine (inside a normal dead zone).",
            < 0.20 => $"{name}: {v:P0} off-centre, slight drift. Games with small dead zones may notice it.",
            _ => $"{name}: {v:P0} off-centre, noticeable drift. Cleaning or replacing the stick usually fixes it.",
        };
        DriftText.Text = Verdict("Left stick", left) + "\n" + Verdict("Right stick", right);
        DriftText.Foreground = (Brush)FindResource(Math.Max(left, right) >= 0.10 ? "WarnBrush" : "GoodBrush");
    }

    async void Rumble_Click(object sender, RoutedEventArgs e)
    {
        var which = (string)((Button)sender).Tag;
        Controllers.Rumble(_pad, which is "L" or "B" ? 1 : 0, which is "R" or "B" ? 1 : 0);
        await Task.Delay(1000);
        Controllers.Rumble(_pad, 0, 0);
    }
}
