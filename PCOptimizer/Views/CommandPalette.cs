using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public sealed class PaletteCommand
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "";
    public required Action Run { get; init; }
    /// <summary>Lower sorts first when scores tie (pages and actions before apps and processes).</summary>
    public int Rank { get; init; }
    /// <summary>Only listed once the user has typed something (long lists like apps).</summary>
    public bool NeedsQuery { get; init; }
    /// <summary>Shown whatever was typed (calculator result).</summary>
    public bool AlwaysMatch { get; init; }
}

/// <summary>
/// Ctrl+Alt+Space: one search box for everything. Type to filter pages, actions, games, apps and running windows;
/// math is calculated as you type. Enter runs the top match.
/// </summary>
public sealed class CommandPalette : Window
{
    readonly Func<string, IEnumerable<PaletteCommand>> _source;
    readonly TextBox _search = new() { FontSize = 16, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0) };
    readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), MaxHeight = 420 };

    public CommandPalette(Func<string, IEnumerable<PaletteCommand>> source)
    {
        _source = source;
        Title = "Command palette";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = (Brush)FindResource("TextBrush");

        _search.Background = (Brush)FindResource("InputBrush");
        _list.ItemContainerStyle = (Style)FindResource("PlainListItem");
        _list.ItemTemplate = Row();
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        Content = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 4 },
            Margin = new Thickness(20),
            Child = new StackPanel
            {
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _search },
                    new Border { Height = 8 },
                    _list,
                    new TextBlock { Text = "↑↓ to choose · Enter to run · Esc to close", FontSize = 11, Margin = new Thickness(4, 6, 0, 0),
                                    Foreground = (Brush)FindResource("MutedBrush") },
                },
            },
        };

        _search.TextChanged += (_, _) => Refresh();
        PreviewKeyDown += OnKey;
        _list.MouseLeftButtonUp += (_, _) => RunSelected();
        Deactivated += (_, _) => Hide();
    }

    DataTemplate Row()
    {
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteCommand.Glyph)));
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 16.0);
        glyph.SetValue(WidthProperty, 30.0);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(TextBlock.ForegroundProperty, FindResource("AccentBrush"));

        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteCommand.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaletteCommand.Detail)));
        detail.SetValue(DockPanel.DockProperty, Dock.Right);
        detail.SetValue(TextBlock.FontSizeProperty, 11.0);
        detail.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        detail.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        detail.SetValue(TextBlock.ForegroundProperty, FindResource("MutedBrush"));

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(MarginProperty, new Thickness(8, 7, 8, 7));
        glyph.SetValue(DockPanel.DockProperty, Dock.Left);
        dock.AppendChild(glyph);
        dock.AppendChild(detail);
        dock.AppendChild(title);
        return new DataTemplate { VisualTree = dock };
    }

    public void Open()
    {
        _search.Text = "";
        Refresh();
        // Upper third of the screen the mouse is on, like Spotlight/PowerToys Run.
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.18;
        Show();
        Activate();
        // Focus once the window is really up, or the first keystrokes can go nowhere.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            Activate();
            _search.Focus();
            Keyboard.Focus(_search);
        });
    }

    void Refresh()
    {
        var q = _search.Text.Trim();
        var scored = _source(q)
            .Where(c => !c.NeedsQuery || q.Length > 0)
            .Select(c => (Cmd: c, Score: c.AlwaysMatch ? (c.Rank < 0 ? 10 : 0.5) : q.Length == 0 ? 1 : Score(c.Title, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Cmd.Rank).ThenBy(x => x.Cmd.Title)
            .Select(x => x.Cmd).Take(40).ToList();
        _list.ItemsSource = scored;
        _list.SelectedIndex = scored.Count > 0 ? 0 : -1;
    }

    /// <summary>Prefix beats word-start beats substring beats loose letters-in-order.</summary>
    static double Score(string title, string q)
    {
        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 4;
        var words = title.Split(' ', ':', '-', '&');
        if (words.Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 3;
        if (title.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        int i = 0;
        foreach (char c in title)
            if (i < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length && q.Length >= 2 ? 1 : 0;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
        }
    }

    void Move(int by)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + by, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    void RunSelected()
    {
        if (_list.SelectedItem is not PaletteCommand cmd) return;
        Hide();
        try { cmd.Run(); }
        catch (Exception ex) { MessageBox.Show($"Couldn't do that:\n{ex.Message}", "PC Optimizer"); }
    }
}
