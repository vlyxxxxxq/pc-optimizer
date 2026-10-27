using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rig.Views;

namespace Rig;

/// <summary>Pinned tools on the Dashboard, and finding any setting from the command palette.</summary>
public partial class MainWindow
{
    void SetUpPins() => RenderPins();

    /// <summary>Right-click menu on a sidebar entry.</summary>
    ContextMenu PinMenu(ModuleInfo m)
    {
        var item = new MenuItem();
        var menu = new ContextMenu { Items = { item } };
        menu.Opened += (_, _) => item.Header = _settings.PinnedPages.Contains(m.NavName) ? "Unpin from Home" : "Pin to Home";
        item.Click += (_, _) => TogglePin(m);
        return menu;
    }

    void TogglePin(ModuleInfo m)
    {
        if (!_settings.PinnedPages.Remove(m.NavName)) _settings.PinnedPages.Add(m.NavName);
        _settings.Save();
        RenderPins();
    }

    void RenderPins()
    {
        if (Created<DashboardView>(Modules.Home) is not { } home) return;
        // Pins are stored by nav name ("NavGame"), as in 1.x.
        var pinned = _settings.PinnedPages.Select(n => Modules.All.FirstOrDefault(m => m.NavName == n)).OfType<ModuleInfo>().Where(m => IsOn(m.Id)).ToList();
        home.PinnedPanel.Children.Clear();
        foreach (var m in pinned)
        {
            var tile = DashboardView.ToolButton(m, () => Go(m.Id));
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) => TogglePin(m);
            tile.ContextMenu = new ContextMenu { Items = { unpin } };
            home.PinnedPanel.Children.Add(tile);
        }
        home.PinnedHint.Visibility = pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Every card title and switch on every page, so typing "break reminders" or "rumble" in the palette jumps
    /// straight to it. Read from the pages themselves, so new settings show up without being listed anywhere.
    /// </summary>
    /// <summary>
    /// Every heading and switch on every page, from the static index (no page is built until you pick one).
    /// </summary>
    List<PaletteCommand> SettingCommands() =>
        SettingsIndex.All.Where(e => IsOn(e.Module)).Select(e => new PaletteCommand
        {
            Title = e.Title, Detail = $"Setting · {Modules.Get(e.Module).Title}", Glyph = "\uE713", Rank = 3, NeedsQuery = true,
            Module = e.Module, Keywords = e.Keywords,
            Run = () => RevealTitle(e.Module, e.Title),
        }).ToList();

    /// <summary>Opens the page and brings the heading with this text into view.</summary>
    void RevealTitle(string module, string title)
    {
        ShowPage(module);
        Dispatcher.InvokeAsync(() =>
        {
            if (Find(_created.GetValueOrDefault(module), title) is { } element) Reveal(module, element);
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        static FrameworkElement? Find(DependencyObject? root, string text)
        {
            if (root is null) return null;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is TextBlock tb && tb.Text.Trim() == text) return tb;
                if (Find(child, text) is { } hit) return hit;
            }
            return null;
        }
    }

    void Reveal(string id, FrameworkElement element)
    {
        ShowPage(id);
        Dispatcher.InvokeAsync(() =>
        {
            // Search results can live in a collapsed advanced section. Open it before scrolling to the setting.
            for (DependencyObject? parent = element; parent is not null;)
            {
                if (parent is Expander expander) expander.IsExpanded = true;
                parent = LogicalTreeHelper.GetParent(parent) ?? (parent is Visual ? VisualTreeHelper.GetParent(parent) : null);
            }
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
