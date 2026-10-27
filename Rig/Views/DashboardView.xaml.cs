using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using Rig.Services;

namespace Rig.Views;

public sealed record DriveRow(string Name, string Label, string Detail, double Percent, Brush Brush);

/// <summary>Holds one Home card and its edit bar; shows up to screen readers (and tests) as a named group.</summary>
public sealed class CardSlot : Grid
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new SlotPeer(this);

    sealed class SlotPeer(CardSlot owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Group;
        protected override string GetClassNameCore() => "HomeCard";
    }
}

/// <summary>A card that can go on Home: wide cards take a whole row, the rest a third of it.</summary>
public sealed record HomeCard(string Id, string Title, bool Wide, string? Module = null);

public partial class DashboardView : UserControl
{
    const int HistoryLength = 60;

    readonly CpuSampler _cpu = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Queue<double> _cpuHistory = new(), _memHistory = new();
    int _tick;

    /// <summary>Raised with a page key ("clean", "startup", "disk", "processes") when a finding's button is clicked.</summary>
    public event EventHandler<string>? ActionRequested;
    public event EventHandler? RecheckRequested;

    public static readonly HomeCard[] Catalog =
    [
        new("pins", "Pinned tools", true), new("health", "Maintenance check", true),
        new("cpu", "CPU", false), new("memory", "Memory", false), new("system", "System", false),
        new("gamemode", "Game Mode", true), new("music", "Now playing", false),
        new("clips", "Clips & Recording", true, "Clips"),
        new("recent", "Recent tools", true),
        new("screentime", "Screen time today", false, "ScreenTime"), new("input", "Input today", false, "Input"),
        new("timers", "Timers & reminders", false, "Timers"), new("power", "Power timer", false, "Tools"), new("freegames", "Free games", false, "Library"),
        new("storage", "Storage", true),
    ];

    AppSettings? _settings;
    Func<string, bool> _moduleOn = _ => true;
    readonly Dictionary<string, Grid> _slots = [];
    bool _editing;
    HealthReport? _health;
    bool _allFindings;
    GameModeState _gameState;
    Point _dragOrigin;
    string? _dragCard;
    const string CardDragFormat = "Rig.HomeCard";
    public event EventHandler? SearchRequested;
    public event Action<string>? PinRequested;

    /// <summary>A card's button wants a module's page (its id).</summary>
    public event Action<string>? OpenRequested;
    /// <summary>"prev", "play" or "next" from the music card.</summary>
    public event Action<string>? MusicCommand;
    /// <summary>The cards on show changed (e.g. free games was added and needs loading).</summary>
    public event EventHandler? CardsChanged;

    public DashboardView() : this(false) { }

    internal DashboardView(bool preview)
    {
        InitializeComponent();
        WrapCards();
        CardHost.SizeChanged += (_, _) => SizeCards();
        Loaded += (_, _) => SizeCards();
        if (preview) return;
        OsText.Text = SystemInfo.OsName();
        OsText.ToolTip = $"{SystemInfo.CpuName()} · {Environment.ProcessorCount} logical processors";

        _cpu.Sample(); // prime the delta
        _timer.Tick += (_, _) => Update();
        // Pages stay loaded when you switch away (they're only hidden), so follow visibility, not Loaded/Unloaded.
        IsVisibleChanged += (_, _) => { if (IsVisible) { Update(); _timer.Start(); } else _timer.Stop(); };
    }

    internal void SetPreviewMetrics()
    {
        CpuText.Text = "12%";
        MemText.Text = "38%";
        MemDetail.Text = "12.2 GB of 32 GB in use";
        UptimeText.Text = "2h 14m";
        OsText.Text = "Windows 11";
        DrivesList.ItemsSource = new[] { new DriveRow("C:", "Local Disk", "312 GB free of 1 TB", 69, (Brush)FindResource("AccentBrush")) };
        for (int i = 0; i < 60; i++) { Push(_cpuHistory, 12 + 5 * Math.Sin(i * .45)); Push(_memHistory, 38 + Math.Sin(i * .12)); }
        CpuChart.SizeChanged += (_, _) => Draw(CpuLine, CpuFill, CpuChart, _cpuHistory);
        MemChart.SizeChanged += (_, _) => Draw(MemLine, MemFill, MemChart, _memHistory);
    }

    public void ShowChecking()
    {
        RecheckButton.IsEnabled = false;
        MaintenanceTitle.Text = "Checking your PC…";
        HealthSummary.Text = "Checking storage, startup apps, and current load.";
    }

    public void ShowHealth(HealthReport report)
    {
        RecheckButton.IsEnabled = true;
        _health = report;
        var summary = MaintenanceOverview.From(report);
        MaintenanceTitle.Text = summary.Title;
        MaintenanceTitle.Foreground = (Brush)FindResource(summary.Attention ? "TextBrush" : "GoodBrush");
        HealthSummary.Text = summary.Detail;
        HealthSummary.ToolTip = HealthReport.Basis;
        RenderFindings();
    }

    sealed record HomeFinding(Finding Finding, bool Enabled)
    {
        public string Title => Finding.Title;
        public string Detail => Finding.Detail;
        public string Glyph => Finding.Glyph;
        public Brush Brush => Finding.Brush;
        public string? ActionLabel => Finding.ActionLabel;
        public string? Action => Finding.Action;
        public bool HasAction => Enabled && Finding.HasAction;
    }

    void RenderFindings()
    {
        if (_health is null) return;
        var important = _health.Findings.Where(f => f.Severity != Severity.Good).ToList();
        var shown = _allFindings ? _health.Findings : important.Take(3);
        FindingsList.ItemsSource = shown.Select(f => new HomeFinding(f,
            f.Action is not { } a || HomeLayout.ModuleForAction(a) is not { } module || _moduleOn(module))).ToList();
        MoreFindingsButton.Visibility = _health.Findings.Count > 0 && (_allFindings || important.Count > 3 || _health.Findings.Count > important.Count)
            ? Visibility.Visible : Visibility.Collapsed;
        MoreFindingsButton.Content = _allFindings ? "Show fewer checks" : $"Show all {_health.Findings.Count} checks";
    }

    void MoreFindings_Click(object sender, RoutedEventArgs e) { _allFindings = !_allFindings; RenderFindings(); }

    void Update()
    {
        double cpu = _cpu.Sample();
        CpuText.Text = $"{cpu:0}%";
        Push(_cpuHistory, cpu);
        Draw(CpuLine, CpuFill, CpuChart, _cpuHistory);

        var (total, available) = SystemInfo.Memory();
        if (total > 0)
        {
            double used = total - available;
            double pct = used / total * 100;
            MemText.Text = $"{pct:0}%";
            MemDetail.Text = $"{Fmt.Bytes(used)} of {Fmt.Bytes(total)} in use";
            Push(_memHistory, pct);
            Draw(MemLine, MemFill, MemChart, _memHistory);
        }

        UptimeText.Text = Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));

        if (_tick++ % 10 == 0) RefreshDrives();
    }

    void RefreshDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed || d.TotalSize == 0) continue;
                double pct = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize * 100;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                rows.Add(new DriveRow(d.Name.TrimEnd('\\'), label,
                    $"{Fmt.Bytes(d.TotalFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", pct, Fmt.LoadBrush(pct)));
            }
            catch { /* drive went away */ }
        }
        DrivesList.ItemsSource = rows;
    }

    static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
    }

    static void Draw(Polyline line, Polygon fill, Canvas canvas, Queue<double> history)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0 || history.Count == 0) return;
        double step = w / (HistoryLength - 1);
        int offset = HistoryLength - history.Count; // right-align so new samples enter from the right
        var points = new PointCollection(history.Count);
        int i = 0;
        foreach (var v in history)
            points.Add(new Point((offset + i++) * step, h - 1 - v / 100 * (h - 2)));
        line.Points = points;

        var area = new PointCollection(points) { new Point(points[^1].X, h), new Point(points[0].X, h) };
        fill.Points = area;
    }

    public void Attach(AppSettings settings, Func<string, bool> moduleOn)
    {
        _settings = settings;
        _moduleOn = moduleOn;
        var upgraded = HomeLayout.Upgrade(settings.HomeCards);
        if (!upgraded.SequenceEqual(settings.HomeCards)) { settings.HomeCards = upgraded; settings.Save(); }
        RenderCards();
        RenderFindings();
    }

    public bool Shows(string id) => _settings?.HomeCards.Contains(id) == true && Available(id);

    bool Available(string id) => Catalog.First(c => c.Id == id).Module is not { } m || _moduleOn(m);

    /// <summary>Puts each card in a slot with an edit bar (move left/right, remove) shown while customizing.</summary>
    void WrapCards()
    {
        foreach (var card in CardHost.Children.OfType<Border>().ToList())
        {
            var id = (string)card.Tag;
            CardHost.Children.Remove(card);
            card.Margin = new Thickness(0);
            // While customizing, a soft overlay covers the card with its name and the move / remove buttons.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(EditButton("", "Remove from Home", () => Remove(id)));
            var bar = new Border
            {
                CornerRadius = new CornerRadius(12), Visibility = Visibility.Collapsed, Focusable = true,
                BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new Thickness(1.5),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = Catalog.First(c => c.Id == id).Title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "Drag to rearrange", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
                        buttons,
                    },
                },
            };
            bar.SetResourceReference(Border.BackgroundProperty, "EditVeilBrush");
            var slot = new CardSlot { Margin = new Thickness(8), Tag = id, Children = { card, bar } };
            slot.AllowDrop = true;
            slot.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (!_editing || e.OriginalSource is DependencyObject target && Ancestor<Button>(target) is not null) return;
                _dragOrigin = e.GetPosition(this); _dragCard = id;
            };
            slot.PreviewMouseMove += (_, e) =>
            {
                if (!_editing || _dragCard != id || e.LeftButton != MouseButtonState.Pressed) return;
                var position = e.GetPosition(this);
                if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                try { slot.Opacity = .55; DragDrop.DoDragDrop(slot, new DataObject(CardDragFormat, id), DragDropEffects.Move); }
                finally { slot.Opacity = 1; _dragCard = null; }
            };
            slot.DragOver += (_, e) =>
            {
                e.Effects = _editing && e.Data.GetDataPresent(CardDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
                if (Ancestor<ScrollViewer>(slot) is { } scroll)
                {
                    double y = e.GetPosition(scroll).Y;
                    if (y < 48) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 18);
                    else if (y > scroll.ActualHeight - 48) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 18);
                }
                bar.BorderThickness = new Thickness(3); e.Handled = true;
            };
            slot.DragLeave += (_, _) => bar.BorderThickness = new Thickness(1.5);
            slot.Drop += (_, e) =>
            {
                bar.BorderThickness = new Thickness(1.5);
                if (!_editing || _settings is null || e.Data.GetData(CardDragFormat) is not string source || source == id) return;
                var point = e.GetPosition(slot);
                bool after = point.Y > slot.ActualHeight / 2;
                _settings.HomeCards = HomeLayout.Reorder(_settings.HomeCards, source, id, after);
                _settings.Save(); RenderCards(); e.Handled = true;
            };
            // Keyboard access without visible arrow buttons.
            var orderMenu = new ContextMenu();
            foreach (var (caption, step) in new[] { ("Move earlier", -1), ("Move later", 1) })
            {
                var item = new MenuItem { Header = caption };
                item.Click += (_, _) => Move(id, step); orderMenu.Items.Add(item);
            }
            bar.ContextMenu = orderMenu;
            System.Windows.Automation.AutomationProperties.SetName(bar, Catalog.First(c => c.Id == id).Title + ". Drag to rearrange, or open the context menu to move with the keyboard.");
            System.Windows.Automation.AutomationProperties.SetAutomationId(slot, "HomeCard_" + id);
            System.Windows.Automation.AutomationProperties.SetName(slot, Catalog.First(c => c.Id == id).Title);
            _slots[id] = slot;
        }
    }

    Button EditButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
                             Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(4, 0, 4, 0), ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    void RenderCards()
    {
        if (_settings is null) return;
        CardHost.Children.Clear();
        foreach (var id in _settings.HomeCards.Where(id => _slots.ContainsKey(id) && Available(id)))
        {
            var slot = _slots[id];
            slot.Children.OfType<Border>().Last().Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            CardHost.Children.Add(slot);
        }
        EditTools.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        AddCardButton.IsEnabled = Catalog.Any(c => !_settings.HomeCards.Contains(c.Id) && Available(c.Id));
        CustomizeButton.Content = _editing ? "Done" : "Customize";
        CustomizeButton.Style = (Style)FindResource(_editing ? "PrimaryButton" : typeof(Button));
        Subtitle.Text = _editing ? "Drag cards to rearrange them. Drop near the top or bottom of a card to place it before or after." : "A clear view. A few useful next steps.";
        SizeCards();
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>Three small cards per row (two on a narrow window); wide cards take the whole row.</summary>
    void SizeCards()
    {
        double w = CardHost.ActualWidth;
        if (w <= 0) return;
        int perRow = w >= 840 ? 3 : w >= 520 ? 2 : 1;
        foreach (var (id, slot) in _slots)
            slot.Width = Math.Max(120, Math.Floor(Catalog.First(c => c.Id == id).Wide ? w - 16 : w / perRow - 16));
        PinnedPanel.Columns = RecentPanel.Columns = Math.Clamp((int)(w / 180), 1, 4);
    }

    void Move(string id, int by)
    {
        if (_settings is null) return;
        var shown = _settings.HomeCards.Where(c => _slots.ContainsKey(c) && Available(c)).ToList();
        int i = shown.IndexOf(id), j = i + by;
        if (i < 0 || j < 0 || j >= shown.Count) return;
        // Swap with the neighbouring visible card in the saved order.
        int a = _settings.HomeCards.IndexOf(id), b = _settings.HomeCards.IndexOf(shown[j]);
        (_settings.HomeCards[a], _settings.HomeCards[b]) = (_settings.HomeCards[b], _settings.HomeCards[a]);
        _settings.Save();
        RenderCards();
    }

    void Remove(string id)
    {
        if (_settings is null) return;
        _settings.HomeCards.Remove(id);
        _settings.Save();
        RenderCards();
    }

    /// <summary>The essentials only: pinned tools, Game Mode and the maintenance check (customizable as usual).</summary>
    public static readonly List<string> CompactPreset = ["gamemode", "pins", "health"];

    void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.HomeCards = [.. CompactPreset];
        _settings.Save();
        RenderCards();
    }

    void Customize_Click(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        RenderCards();
    }

    void AddCard_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var menu = new ContextMenu { PlacementTarget = AddCardButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var card in Catalog.Where(c => !_settings.HomeCards.Contains(c.Id) && Available(c.Id)))
        {
            var item = new MenuItem { Header = card.Title };
            item.Click += (_, _) => { _settings.HomeCards.Add(card.Id); _settings.Save(); RenderCards(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // --- Card contents, pushed in by the main window ---

    public void SetGameMode(bool on, string? game, GameSession? last, GameModeState state = GameModeState.Off, bool automatic = true)
    {
        _gameState = state;
        var summary = GameModeSummary.Create(state, automatic, game);
        GameStateText.Text = summary.Title;
        GameStateText.Foreground = (Brush)FindResource("TextBrush");
        GameStateDetail.Text = summary.Detail;
        GameBadgeText.Text = summary.Badge;
        GameBadge.Background = (Brush)FindResource(summary.Tone == StatusTone.Attention ? "WarnTintBrush" : summary.Tone == StatusTone.Active ? "GoodTintBrush" : "InfoTintBrush");
        GameBadgeText.Foreground = (Brush)FindResource(summary.Tone == StatusTone.Attention ? "WarnBrush" : summary.Tone == StatusTone.Active ? "GoodBrush" : "AccentLightBrush");
        GameToggleButton.Content = summary.Action;
        GameToggleButton.IsEnabled = !summary.Busy;
        LastSessionPanel.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        LastSessionText.Text = last is null ? "" : $"Last session · {last.Game}";
        LastSessionDetail.Text = last is null ? "" : $"{Fmt.Duration(last.Duration).Replace("0h ", "")} · {last.Start:ddd d MMM}" +
            (last.AvgFps is { } fps ? $" · {fps:0} FPS" : "");
    }

    void GameToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_gameState == GameModeState.RecoveryNeeded) OpenRequested?.Invoke("Game");
        else ActionRequested?.Invoke(this, "game-toggle");
    }

    void Search_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke(this, EventArgs.Empty);

    void EditPins_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender };
        foreach (var category in Modules.Categories)
        {
            var group = new MenuItem { Header = category };
            foreach (var module in Modules.All.Where(m => m.Category == category && _moduleOn(m.Id)))
            {
                var item = new MenuItem { Header = module.Title, IsCheckable = true, IsChecked = _settings.PinnedPages.Contains(module.NavName) };
                item.Click += (_, _) => PinRequested?.Invoke(module.Id);
                group.Items.Add(item);
            }
            if (group.Items.Count > 0) menu.Items.Add(group);
        }
        menu.IsOpen = true;
    }

    internal static Button ToolButton(ModuleInfo module, Action action)
    {
        var label = new TextBlock { Text = module.Title, MaxWidth = 134, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Height = 48, Margin = new Thickness(4), Padding = new Thickness(10, 8, 10, 8), ToolTip = module.Title,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = module.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                    Foreground = (Brush)Application.Current.FindResource("AccentBrush"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center }, label,
            } },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, module.Title);
        button.Click += (_, _) => action();
        return button;
    }

    public void SetRecentTools(IReadOnlyList<ModuleInfo> tools)
    {
        RecentPanel.Children.Clear();
        foreach (var tool in tools) RecentPanel.Children.Add(ToolButton(tool, () => OpenRequested?.Invoke(tool.Id)));
        RecentEmpty.Visibility = tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetMusic(Track? track)
    {
        // Nothing to control without a player: the buttons say so instead of sending keys into the void.
        foreach (var b in MusicButtons.Children.OfType<Button>()) b.IsEnabled = track is not null;
        SongText.Text = track?.Title ?? "Nothing playing";
        ArtistText.Text = track is null ? "Spotify, Apple Music, YouTube and more" : track.Artist is null ? track.App : $"{track.Artist} · {track.App}";
    }

    public void SetCapture(CaptureStudio? studio)
    {
        CaptureTitle.Text = studio?.IsRecording == true ? $"Recording · {studio.Elapsed:mm\\:ss}" : studio?.ReplayEnabled == true ? $"Replay is on · {studio.BufferedSeconds:0}s buffered" : "Keep the moment.";
        CaptureDetail.Text = studio?.LastError ?? "Save a replay clip, start a recording, or open your capture library.";
        HomeReplayButton.Content = studio?.ReplayEnabled == true ? "Turn replay off" : "Enable replay";
        HomeReplayButton.IsEnabled = HomeRecordButton.IsEnabled = studio?.Busy != true;
        HomeRecordButton.Content = studio?.IsRecording == true ? "Stop & save" : "Record";
        HomeSaveClipButton.IsEnabled = studio?.ReplayEnabled == true && studio.BufferedSeconds > .5 && !studio.Busy;
    }
    void CaptureAction_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string action) ActionRequested?.Invoke(this, action); }

    public void SetScreenTime(TimeSpan total, string? topApp)
    {
        ScreenTimeText.Text = ScreenTimeView.Short(total);
        ScreenTimeDetail.Text = topApp is null ? "Nothing recorded yet today" : $"Most used: {topApp}";
    }

    public void SetInput(InputDay? today, bool counting)
    {
        InputText.Text = counting ? $"{(today?.Keys ?? 0):N0}" : "Off";
        InputDetail.Text = counting ? $"keys · {(today?.Clicks ?? 0):N0} clicks" : "Switch on counting in Input Stats";
    }

    public void SetPower(string status) => PowerText.Text = status;

    public void SetTimers(string? next, string? when, int count)
    {
        TimersText.Text = next ?? "Nothing running";
        TimersDetail.Text = next is null ? "Start one here or from the palette" : $"{when}{(count > 1 ? $" · {count - 1} more" : "")}";
    }

    public void SetFreeGames(IReadOnlyList<Giveaway>? games)
    {
        FreeText.Text = games is null ? "Couldn't check (offline?)" : games.Count == 0 ? "Nothing free right now." : $"{games.Count} free to keep:";
        FreeList.ItemsSource = games?.Take(4).ToList();
    }

    void Open_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string id) OpenRequested?.Invoke(id); }
    void Music_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string cmd) MusicCommand?.Invoke(cmd); }

    void Finding_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string action) ActionRequested?.Invoke(this, action);
    }

    void Recheck_Click(object sender, RoutedEventArgs e) => RecheckRequested?.Invoke(this, EventArgs.Empty);
}
