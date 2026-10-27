using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

/// <summary>The developer's latest announcements and patch notes for one Steam game.</summary>
public sealed class NewsWindow : Window
{
    readonly StackPanel _list = new();

    public NewsWindow(LibraryGame game)
    {
        Title = $"What's new · {game.Name}";
        Width = 620;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BgBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = new DockPanel
        {
            Margin = new Thickness(24, 20, 12, 20),
            Children =
            {
                Docked(new TextBlock { Text = game.Name, FontSize = 22, FontWeight = FontWeights.SemiBold }),
                Docked(new TextBlock { Text = "Updates and announcements from the developer", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 2, 0, 14) }),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(0, 0, 12, 0) },
            },
        };
        _list.Children.Add(new TextBlock { Text = "Loading…", Foreground = (Brush)FindResource("MutedBrush") });
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
        Loaded += async (_, _) => await LoadAsync(game.AppId!);
    }

    static UIElement Docked(UIElement e) { DockPanel.SetDock(e, Dock.Top); return e; }

    async Task LoadAsync(string appId)
    {
        List<NewsItem> news;
        try { news = await GameFeeds.SteamNewsAsync(appId, 10); }
        catch { _list.Children.Clear(); _list.Children.Add(new TextBlock { Text = "Couldn't load news. Are you offline?" }); return; }
        _list.Children.Clear();
        if (news.Count == 0) _list.Children.Add(new TextBlock { Text = "No announcements yet.", Foreground = (Brush)FindResource("MutedBrush") });
        foreach (var n in news)
        {
            var open = new Button { Content = "Read more", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            var url = n.Url;
            open.Click += (_, _) => { if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Shell.Open(url); };
            _list.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = n.When + (n.IsRecent ? "  ·  NEW" : ""), FontSize = 11,
                                        Foreground = (Brush)FindResource(n.IsRecent ? "AccentBrush" : "MutedBrush") },
                        new TextBlock { Text = n.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) },
                        new TextBlock { Text = n.Summary, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush") },
                        open,
                    },
                },
            });
        }
    }
}
