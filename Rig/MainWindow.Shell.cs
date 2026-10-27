using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rig.Views;

namespace Rig;

/// <summary>
/// The sidebar and the pages behind it. Pages are only created the first time they're opened (or when something
/// needs them), and switched-off modules leave the sidebar and stop their background work.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, RadioButton> _nav = [];
    readonly Dictionary<string, FrameworkElement> _created = [];
    Dictionary<string, (Func<FrameworkElement> Create, Action<FrameworkElement>? OnShow)> _factories = [];
    string _current = Modules.Home;
    readonly Services.RecentTools _recentTools = new();

    public bool IsOn(string id) => Modules.Get(id).Core || !_settings.DisabledModules.Contains(id);

    /// <summary>The module's page, created on first use.</summary>
    FrameworkElement Page(string id)
    {
        if (_created.TryGetValue(id, out var page)) return page;
        var (create, _) = _factories[id];
        page = create();
        page.Visibility = Visibility.Collapsed;
        _created[id] = page;
        PagesHost.Children.Add(page);
        return page;
    }

    /// <summary>
    /// A switch is a bare toggle; screen readers need the caption next to it ("Notifications", "Block sleep…") as its
    /// name. Done for every switch as it loads, including ones inside lists; a switch that sets its own name keeps it.
    /// </summary>
    internal static void NameSwitchOnLoad(object sender, RoutedEventArgs e)
    {
        // Switches, and plain checkboxes without a caption of their own (ticks in lists).
        if (sender is CheckBox cb && (cb.Style == (Style)Application.Current.Resources["Switch"] || cb.Content is null)
            && string.IsNullOrEmpty(AutomationProperties.GetName(cb)) && Caption(cb) is { Length: > 0 } name)
            AutomationProperties.SetName(cb, name);
    }

    /// <summary>The nearest text beside a switch: a heading in its row (before or after it), looking up to three levels out.</summary>
    static string? Caption(FrameworkElement element)
    {
        DependencyObject current = element;
        for (int level = 0; level < 3; level++)
        {
            if (VisualTreeHelper.GetParent(current) is not Panel panel) return null;
            var siblings = panel.Children.OfType<UIElement>().ToList();
            int at = siblings.IndexOf((UIElement)current);
            // Nearest sibling first: the text before the switch, then after it.
            foreach (var sibling in siblings.Take(at).Reverse().Concat(siblings.Skip(at + 1)))
                if (FirstText(sibling) is { Length: > 0 } text) return text;
            current = panel;
        }
        return null;

        static string? FirstText(DependencyObject d) => d switch
        {
            TextBlock { Text.Length: > 0 } t => t.Text,
            Panel p => p.Children.OfType<UIElement>().Select(FirstText).FirstOrDefault(x => x is { Length: > 0 }),
            _ => null,
        };
    }

    /// <summary>The page if it exists already, without creating it.</summary>
    T? Created<T>(string id) where T : class => _created.GetValueOrDefault(id) as T;

    void Register<T>(string id, Func<T> create, Action<T>? onShow = null) where T : FrameworkElement =>
        _factories[id] = (create, onShow is null ? null : p => onShow((T)p));

    /// <summary>Opens a module's page (from code: a dashboard action, a palette command, a notification).</summary>
    void Go(string id)
    {
        if (!IsOn(id)) return;
        if (_nav.TryGetValue(id, out var nav)) nav.IsChecked = true;
        else Show(id); // in a collapsed category: still open it
    }

    void ShowPage(string id)
    {
        ShowFromTray();
        Go(id);
    }

    void Show(string id)
    {
        Services.Perf.UntilIdle(Dispatcher, (_created.ContainsKey(id) ? "nav-repeat " : "nav-first ") + id);
        var page = Page(id);
        foreach (var (key, p) in _created) p.Visibility = key == id ? Visibility.Visible : Visibility.Collapsed;
        _current = id;
        _recentTools.Visit(id);
        Created<DashboardView>(Modules.Home)?.SetRecentTools(_recentTools.Available(IsOn));
        AnimateIn(page);
        _factories[id].OnShow?.Invoke(page);
    }

    // --- Sidebar ---

    void BuildNav()
    {
        NavPanel.Children.Clear();
        NavBottom.Children.Clear();
        _nav.Clear();
        bool slim = _settings.SidebarCollapsed;

        NavPanel.Children.Add(NavButton(Modules.Get(Modules.Home)));
        foreach (var category in Modules.Categories)
        {
            var modules = Modules.All.Where(m => m.Category == category && IsOn(m.Id)).ToList();
            if (modules.Count == 0) continue;
            bool folded = _settings.CollapsedCategories.Contains(category);
            if (slim) NavPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(22, 10, 22, 8), Background = (Brush)FindResource("CardBorderBrush") });
            else NavPanel.Children.Add(CategoryHeader(category, folded));
            foreach (var m in modules)
                if (!folded || slim || m.Id == _current) NavPanel.Children.Add(NavButton(m));
        }
        NavBottom.Children.Add(NavButton(Modules.Get(Modules.Settings)));
        if (_nav.TryGetValue(_current, out var current)) current.IsChecked = true;

        // Slim mode: icons only, with names as tooltips.
        SidebarColumn.Width = new GridLength(slim ? 76 : 232);
        foreach (var e in new FrameworkElement[] { BrandText, PaletteText, PaletteKeyText, MusicChip, AdminStatus })
            e.Visibility = slim ? Visibility.Collapsed : (e == MusicChip ? MusicChip.Visibility : Visibility.Visible);
        if (!slim) _ = UpdateMusicAsync();
        GameChipText.Visibility = BrandLogo.Visibility = GameDot.Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
        GameChipIcon.Margin = new Thickness(0, 0, slim ? 0 : 10, 0);
        GameChipIcon.HorizontalAlignment = slim ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        BrandRow.Margin = slim ? new Thickness(26, 20, 26, 16) : new Thickness(20, 20, 12, 16);
        NavScroll.ScrollToTop();
        SidebarToggle.ToolTip = slim ? "Expand the sidebar" : "Collapse the sidebar";
        SidebarToggle.Content = slim ? "" : "";
    }

    RadioButton NavButton(ModuleInfo m)
    {
        bool slim = _settings.SidebarCollapsed;
        var rb = new RadioButton
        {
            Style = (Style)FindResource("NavButton"),
            Content = slim ? null : m.Title,
            Tag = m.Glyph,
            ToolTip = slim ? m.Title : null,
            IsChecked = m.Id == _current,
        };
        AutomationProperties.SetAutomationId(rb, m.NavName);
        AutomationProperties.SetName(rb, m.Title);
        rb.Checked += (_, _) => Show(m.Id);
        if (m.Id != Modules.Home && m.Id != Modules.Settings) rb.ContextMenu = PinMenu(m);
        _nav[m.Id] = rb;
        return rb;
    }

    FrameworkElement CategoryHeader(string category, bool folded)
    {
        var row = new DockPanel { Background = Brushes.Transparent };
        var chevron = new TextBlock { Text = folded ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                      FontSize = 9, Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(new TextBlock { Text = category.ToUpperInvariant(), Style = (Style)FindResource("NavSection"), Margin = new Thickness(0) });
        // A real button (just drawn plainly), so it works with the keyboard and screen readers.
        var plain = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var header = new Button
        {
            Template = plain, Content = row, Margin = new Thickness(28, 14, 24, 4), Cursor = Cursors.Hand, Focusable = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = folded ? "Show" : "Hide",
        };
        AutomationProperties.SetAutomationId(header, "Category" + category);
        AutomationProperties.SetName(header, category);
        header.Click += (_, _) =>
        {
            if (!_settings.CollapsedCategories.Remove(category)) _settings.CollapsedCategories.Add(category);
            _settings.Save();
            BuildNav();
        };
        return header;
    }

    void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.SidebarCollapsed = !_settings.SidebarCollapsed;
        _settings.Save();
        BuildNav();
    }

    // --- Switching modules on and off ---

    /// <summary>Starts or stops a module's background work to match its switch, then redraws the sidebar.</summary>
    public void ApplyModule(string id)
    {
        bool on = IsOn(id);
        switch (id)
        {
            case "Clipboard": _clipboard.Enabled = on && _settings.ClipboardHistoryEnabled; break;
            case "ScreenTime": if (on && _settings.ScreenTimeEnabled) _screenTime.Start(); else _screenTime.Stop(); break;
            case "Input": if (on && _settings.InputStatsEnabled) _inputStats.Start(); else _inputStats.Stop(); break;
            case "Clips" or "ColorPicker":
                // A recording in progress is finished and saved first, never cut off.
                if (id == "Clips" && !on && _recorder.IsRecording)
                {
                    _recorder.StopAndWait(TimeSpan.FromSeconds(30));
                    Notify("Recording saved", "Clips & Recording was switched off, so the recording in progress was finished and saved.");
                }
                RegisterCaptureHotkeys();
                break;
            case "Timers":
                _timers.Enabled = on;
                if (!on && _timers.Items.Count > 0)
                    Notify("Reminders paused", $"{_timers.Items.Count} timer(s) and reminder(s) won't go off while Timers is switched off. Any that come due meanwhile go off when it's back on.");
                break;
            case "Photos": Created<PhotosView>("Photos")?.Cancel(); break;
            case "Crosshair":
                if (on) { RegisterCrosshairHotkey(); SyncCrosshairTimer(); }
                else { _crosshairKeys?.Dispose(); _crosshairKeys = null; _crosshairTimer.Stop(); _crosshairManual = false; _crosshair?.Hide(); }
                break;
            case "Downloads":
                if (on && _settings.DownloadsAutoOrganize) _ = Page("Downloads"); // its folder watcher lives on the page
                Created<DownloadsView>("Downloads")?.SetActive(on);
                break;
            case "Cleaner": if (on) _autoCleaner.Start(); else _autoCleaner.Stop(); break;
        }
        if (!on && _current == id) Go(Modules.Home);
        if (Created<DashboardView>(Modules.Home) is { } home)
        {
            home.Attach(_settings, IsOn);
            home.SetRecentTools(_recentTools.Available(IsOn));
        }
        _fixedCommands = []; // palette lists are rebuilt without it
        BuildNav();
        RenderPins();
    }
}
