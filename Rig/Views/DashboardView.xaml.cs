using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        new("pins", "Pinned tools", true), new("health", "PC health", true),
        new("cpu", "CPU", false), new("memory", "Memory", false), new("system", "System", false),
        new("gamemode", "Game Mode", false), new("music", "Now playing", false),
        new("screentime", "Screen time today", false, "ScreenTime"), new("input", "Input today", false, "Input"),
        new("timers", "Timers & reminders", false, "Timers"), new("power", "Power timer", false, "Tools"), new("freegames", "Free games", false, "Library"),
        new("storage", "Storage", true),
    ];

    AppSettings? _settings;
    Func<string, bool> _moduleOn = _ => true;
    readonly Dictionary<string, Grid> _slots = [];
    bool _editing;

    /// <summary>A card's button wants a module's page (its id).</summary>
    public event Action<string>? OpenRequested;
    /// <summary>"prev", "play" or "next" from the music card.</summary>
    public event Action<string>? MusicCommand;
    /// <summary>The cards on show changed (e.g. free games was added and needs loading).</summary>
    public event EventHandler? CardsChanged;

    public DashboardView()
    {
        InitializeComponent();
        WrapCards();
        CardHost.SizeChanged += (_, _) => SizeCards();
        OsText.Text = SystemInfo.OsName();
        CpuNameText.Text = SystemInfo.CpuName();
        CoresText.Text = $"{Environment.ProcessorCount} logical processors";

        _cpu.Sample(); // prime the delta
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { Update(); _timer.Start(); SizeCards(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void ShowChecking()
    {
        RecheckButton.IsEnabled = false;
        ScoreText.Text = "…";
        ScoreLabel.Text = "Checking";
        HealthSummary.Text = "Checking for junk files, startup load and disk space…";
        ScoreArc.Data = null;
    }

    public void ShowHealth(HealthReport report)
    {
        RecheckButton.IsEnabled = true;
        ScoreText.Text = report.Score.ToString();
        ScoreLabel.Text = report.Label;
        ScoreArc.Stroke = report.Brush;
        ScoreArc.Data = Arc(report.Score / 100.0, 96, 9);
        int issues = report.Findings.Count(f => f.Severity != Severity.Good);
        HealthSummary.Text = issues == 0
            ? "Everything looks great. Nothing needs your attention."
            : $"{issues} {(issues == 1 ? "thing" : "things")} could make your PC faster.";
        FindingsList.ItemsSource = report.Findings;
    }

    /// <summary>A clockwise ring segment starting at 12 o'clock, sized to sit on the track ellipse.</summary>
    internal static Geometry Arc(double fraction, double size, double thickness)
    {
        if (fraction <= 0) return Geometry.Empty;
        double r = (size - thickness) / 2, c = size / 2;
        double angle = Math.Min(fraction, 0.9999) * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

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
        RenderCards();
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
            buttons.Children.Add(EditButton("", "Move earlier", () => Move(id, -1)));
            buttons.Children.Add(EditButton("", "Move later", () => Move(id, +1)));
            buttons.Children.Add(EditButton("", "Remove from Home", () => Remove(id)));
            var bar = new Border
            {
                CornerRadius = new CornerRadius(12), Visibility = Visibility.Collapsed,
                BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new Thickness(1.5),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = Catalog.First(c => c.Id == id).Title, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        buttons,
                    },
                },
            };
            bar.SetResourceReference(Border.BackgroundProperty, "EditVeilBrush");
            var slot = new CardSlot { Margin = new Thickness(8), Tag = id, Children = { card, bar } };
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
        AddCardButton.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        AddCardButton.IsEnabled = Catalog.Any(c => !_settings.HomeCards.Contains(c.Id) && Available(c.Id));
        CustomizeButton.Content = _editing ? "Done" : "Customize";
        CustomizeButton.Style = (Style)FindResource(_editing ? "PrimaryButton" : typeof(Button));
        Subtitle.Text = _editing ? "Arrange your cards: move them with the arrows, remove what you don't need, and add more." : "Your tools and your PC at a glance.";
        SizeCards();
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Three small cards per row (two on a narrow window); wide cards take the whole row.</summary>
    void SizeCards()
    {
        double w = CardHost.ActualWidth;
        if (w <= 0) return;
        int perRow = w >= 840 ? 3 : 2;
        foreach (var (id, slot) in _slots)
            slot.Width = Math.Max(120, Math.Floor(Catalog.First(c => c.Id == id).Wide ? w - 16 : w / perRow - 16));
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

    public void SetGameMode(bool on, string? game, GameSession? last)
    {
        GameStateText.Text = on ? "On" : "Off";
        GameStateText.Foreground = (Brush)FindResource(on ? "GoodBrush" : "TextBrush");
        GameStateDetail.Text = on ? game ?? "Turned on by hand" : "Turns on when you start a game";
        LastSessionText.Text = last is null ? "No games played yet" : $"Last: {last.Game}";
        LastSessionDetail.Text = last is null ? "" : $"{Fmt.Duration(last.Duration).Replace("0h ", "")} · {last.Start:ddd d MMM}" +
            (last.AvgFps is { } fps ? $" · {fps:0} FPS" : "");
    }

    public void SetMusic(Track? track)
    {
        SongText.Text = track?.Title ?? "Nothing playing";
        ArtistText.Text = track is null ? "Spotify, Apple Music, YouTube and more" : track.Artist is null ? track.App : $"{track.Artist} · {track.App}";
    }

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
