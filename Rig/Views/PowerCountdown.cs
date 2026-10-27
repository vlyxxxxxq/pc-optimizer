using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

/// <summary>The last minute before the power timer acts: Cancel, or do it now.</summary>
public sealed class PowerCountdown : Window
{
    int _left = 60;

    public PowerCountdown(PowerAction action)
    {
        Title = "Power timer";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = (Brush)FindResource("TextBrush");

        var verb = PowerTimer.Verb(action);
        var text = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 4, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsCancel = true };
        var now = new Button { Content = $"{verb} now" };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void Render() => text.Text = $"{verb} in {_left} seconds. Save anything you're working on.";
        void Go() { timer.Stop(); Close(); PowerTimer.Execute(action); }
        cancel.Click += (_, _) => { timer.Stop(); Close(); };
        now.Click += (_, _) => Go();
        timer.Tick += (_, _) => { if (--_left <= 0) Go(); else Render(); };
        Render();

        Content = new Border
        {
            Width = 380, Margin = new Thickness(16), Padding = new Thickness(22, 18, 22, 18), CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, Opacity = 0.5, ShadowDepth = 3 },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "POWER TIMER", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush") },
                    new TextBlock { Text = $"{verb} soon", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) },
                    text,
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, now } },
                },
            },
        };
        Loaded += (_, _) => { Activate(); timer.Start(); };
    }
}
