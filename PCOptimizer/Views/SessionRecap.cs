using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

/// <summary>
/// A card in the bottom-right corner after a game closes: how long you played, how it ran, and what you captured.
/// Fades away after 20 seconds; click it to open Game Mode's history.
/// </summary>
public sealed class SessionRecap : Window
{
    public event EventHandler? OpenRequested;

    public SessionRecap(GameSession s, int captures)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = (Brush)FindResource("TextBrush");
        Cursor = System.Windows.Input.Cursors.Hand;

        var stats = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        stats.Children.Add(Stat("PLAYED", Fmt.Duration(s.Duration).Replace("0h ", "")));
        if (s.AvgFps is { } fps) stats.Children.Add(Stat("AVG FPS", $"{fps:0}", s.LowFps is { } low ? $"{low:0} 1% low" : null));
        stats.Children.Add(Stat("CPU", $"{s.AvgCpu:0}%", $"{s.PeakCpu:0}% peak"));
        stats.Children.Add(Stat("GPU", $"{s.AvgGpu:0}%", $"{s.PeakGpu:0}% peak"));
        stats.Children.Add(Stat("GAME RAM", Fmt.Bytes(s.PeakGameRam), "peak"));
        if (captures > 0) stats.Children.Add(Stat("CAPTURED", $"{captures}", captures == 1 ? "clip/screenshot" : "clips/screenshots"));

        var close = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 1), FontSize = 11, VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, e) => { e.Handled = true; FadeOut(); };
        DockPanel.SetDock(close, Dock.Right);

        Content = new Border
        {
            Width = 380,
            Margin = new Thickness(16),
            Padding = new Thickness(18, 14, 14, 16),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, Opacity = 0.5, ShadowDepth = 3 },
            Child = new StackPanel
            {
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            close,
                            new StackPanel
                            {
                                Children =
                                {
                                    new TextBlock { Text = "SESSION RECAP", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush") },
                                    new TextBlock { Text = s.Game, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                                },
                            },
                        },
                    },
                    stats,
                },
            },
        };

        // A real click (press and release on the card), not a stray release from a drag that ended here.
        bool pressed = false;
        MouseLeftButtonDown += (_, _) => pressed = true;
        MouseLeftButtonUp += (_, _) => { if (!pressed) return; OpenRequested?.Invoke(this, EventArgs.Empty); Close(); };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => { timer.Stop(); if (!IsMouseOver) FadeOut(); else timer.Start(); };
        timer.Start();
    }

    StackPanel Stat(string label, string value, string? sub = null)
    {
        var panel = new StackPanel { Width = 110, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("MutedBrush") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold });
        if (sub is not null) panel.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush") });
        return panel;
    }

    void FadeOut()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
