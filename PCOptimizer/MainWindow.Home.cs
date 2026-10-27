using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCOptimizer.Views;

namespace PCOptimizer;

/// <summary>Pinned tools on the Dashboard, and finding any setting from the command palette.</summary>
public partial class MainWindow
{
    void SetUpPins()
    {
        foreach (var nav in _pages.Keys.Where(n => n != NavDashboard))
        {
            var item = new MenuItem();
            nav.ContextMenu = new ContextMenu { Items = { item } };
            nav.ContextMenuOpening += (_, _) => item.Header = _settings.PinnedPages.Contains(nav.Name) ? "Unpin from Dashboard" : "Pin to Dashboard";
            item.Click += (_, _) => TogglePin(nav);
        }
        RenderPins();
    }

    void TogglePin(RadioButton nav)
    {
        if (!_settings.PinnedPages.Remove(nav.Name)) _settings.PinnedPages.Add(nav.Name);
        _settings.Save();
        RenderPins();
    }

    void RenderPins()
    {
        var navs = _settings.PinnedPages.Select(n => _pages.Keys.FirstOrDefault(k => k.Name == n)).OfType<RadioButton>().ToList();
        DashboardPage.PinnedPanel.Children.Clear();
        foreach (var nav in navs)
        {
            var tile = new Button
            {
                Width = 150, Height = 86, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12, 14, 12),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = $"Open {nav.Content}",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = (string)nav.Tag, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
                                        Foreground = (Brush)FindResource("AccentBrush") },
                        new TextBlock { Text = (string)nav.Content, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold,
                                        TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            tile.Click += (_, _) => nav.IsChecked = true;
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) => TogglePin(nav);
            tile.ContextMenu = new ContextMenu { Items = { unpin } };
            DashboardPage.PinnedPanel.Children.Add(tile);
        }
        DashboardPage.PinnedHint.Visibility = navs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Every card title and switch on every page, so typing "break reminders" or "rumble" in the palette jumps
    /// straight to it. Read from the pages themselves, so new settings show up without being listed anywhere.
    /// </summary>
    List<PaletteCommand> SettingCommands()
    {
        var list = new List<PaletteCommand>();
        foreach (var (nav, page) in _pages)
        {
            var pageName = (string)nav.Content;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pageName };
            foreach (var (text, element) in Labels(page))
            {
                if (text.Length is < 4 or > 48 || !seen.Add(text)) continue;
                list.Add(new PaletteCommand
                {
                    Title = text, Detail = $"Setting · {pageName}", Glyph = "", Rank = 3, NeedsQuery = true,
                    Run = () => Reveal(nav, element),
                });
            }
        }
        return list;
    }

    /// <summary>Headings (semi-bold, 14 px or more, but not the page title) and switch/checkbox captions.</summary>
    static IEnumerable<(string Text, FrameworkElement Element)> Labels(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock { Text: { Length: > 0 } t } tb && tb.FontSize is >= 14 and < 24 &&
                (tb.FontWeight >= FontWeights.SemiBold || IsSwitchRow(tb)))
                yield return (t.Trim(), tb);
            else if (child is CheckBox { Content: string c } cb && cb.Style != (Style)Application.Current.Resources["Switch"])
                yield return (c.Trim(), cb);
            foreach (var x in Labels(child)) yield return x;
        }
    }

    /// <summary>A row's caption: the first text in a row (DockPanel) that has a switch or checkbox.</summary>
    static bool IsSwitchRow(TextBlock tb)
    {
        var holder = tb.Parent as Panel;
        if (holder is StackPanel s && s.Children.IndexOf(tb) == 0) holder = s.Parent as Panel;
        return holder is DockPanel d && d.Children.OfType<CheckBox>().Any();
    }

    void Reveal(RadioButton nav, FrameworkElement element)
    {
        ShowPage(nav);
        Dispatcher.InvokeAsync(() =>
        {
            element.BringIntoView();
            // Flash the card it's on so the eye finds it.
            DependencyObject? d = element;
            while (d is not null && !(d is Border b && b.Style == (Style)FindResource("Card"))) d = VisualTreeHelper.GetParent(d);
            if (d is not Border card) return;
            var original = card.BorderBrush;
            var flash = new SolidColorBrush(((SolidColorBrush)FindResource("AccentBrush")).Color);
            card.BorderBrush = flash;
            var fade = new ColorAnimation(((SolidColorBrush)FindResource("CardBorderBrush")).Color, TimeSpan.FromSeconds(1.6)) { BeginTime = TimeSpan.FromSeconds(0.6) };
            fade.Completed += (_, _) => card.BorderBrush = original;
            flash.BeginAnimation(SolidColorBrush.ColorProperty, fade);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
