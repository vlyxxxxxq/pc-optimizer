using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rig.Services;

namespace Rig.Views;

public sealed record GameInputRow(string Name, string Played, string Keys, string Clicks, string Apm, string TopKeys);

public partial class InputStatsView : UserControl
{
    InputStats? _stats;
    AppSettings? _settings;
    InputStatsData? _data;
    readonly System.Windows.Threading.DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    // A 75% (TKL-style) layout: label, virtual-key code, width in key units. A 0 code is a gap.
    static readonly (string Label, int Vk, double W)[][] Layout =
    [
        [("Esc", 0x1B, 1), ("", 0, 0.5), ("F1", 0x70, 1), ("F2", 0x71, 1), ("F3", 0x72, 1), ("F4", 0x73, 1), ("", 0, 0.5),
         ("F5", 0x74, 1), ("F6", 0x75, 1), ("F7", 0x76, 1), ("F8", 0x77, 1), ("", 0, 0.5), ("F9", 0x78, 1), ("F10", 0x79, 1),
         ("F11", 0x7A, 1), ("F12", 0x7B, 1), ("", 0, 0.25), ("PrtSc", 0x2C, 1), ("Del", 0x2E, 1), ("Home", 0x24, 1)],
        [("`", 0xC0, 1), ("1", 0x31, 1), ("2", 0x32, 1), ("3", 0x33, 1), ("4", 0x34, 1), ("5", 0x35, 1), ("6", 0x36, 1),
         ("7", 0x37, 1), ("8", 0x38, 1), ("9", 0x39, 1), ("0", 0x30, 1), ("-", 0xBD, 1), ("=", 0xBB, 1), ("Backspace", 0x08, 2),
         ("", 0, 0.25), ("Ins", 0x2D, 1), ("End", 0x23, 1), ("PgUp", 0x21, 1)],
        [("Tab", 0x09, 1.5), ("Q", 0x51, 1), ("W", 0x57, 1), ("E", 0x45, 1), ("R", 0x52, 1), ("T", 0x54, 1), ("Y", 0x59, 1),
         ("U", 0x55, 1), ("I", 0x49, 1), ("O", 0x4F, 1), ("P", 0x50, 1), ("[", 0xDB, 1), ("]", 0xDD, 1), ("\\", 0xDC, 1.5),
         ("", 0, 2.25), ("PgDn", 0x22, 1)],
        [("Caps", 0x14, 1.75), ("A", 0x41, 1), ("S", 0x53, 1), ("D", 0x44, 1), ("F", 0x46, 1), ("G", 0x47, 1), ("H", 0x48, 1),
         ("J", 0x4A, 1), ("K", 0x4B, 1), ("L", 0x4C, 1), (";", 0xBA, 1), ("'", 0xDE, 1), ("Enter", 0x0D, 2.25)],
        [("Shift", 0xA0, 2.25), ("Z", 0x5A, 1), ("X", 0x58, 1), ("C", 0x43, 1), ("V", 0x56, 1), ("B", 0x42, 1), ("N", 0x4E, 1),
         ("M", 0x4D, 1), (",", 0xBC, 1), (".", 0xBE, 1), ("/", 0xBF, 1), ("Shift", 0xA1, 2.75), ("", 0, 1.25), ("↑", 0x26, 1)],
        [("Ctrl", 0xA2, 1.25), ("Win", 0x5B, 1.25), ("Alt", 0xA4, 1.25), ("Space", 0x20, 6.25), ("Alt", 0xA5, 1.25),
         ("Fn", 0, 1.25), ("Ctrl", 0xA3, 1.25), ("", 0, 0.25), ("←", 0x25, 1), ("↓", 0x28, 1), ("→", 0x27, 1)],
    ];

    static readonly Dictionary<int, string> KeyNames = Layout.SelectMany(r => r).Where(k => k.Vk != 0)
        .GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First().Label switch
        {
            "Shift" => g.Key == 0xA0 ? "Left Shift" : "Right Shift",
            "Ctrl" => g.Key == 0xA2 ? "Left Ctrl" : "Right Ctrl",
            "Alt" => g.Key == 0xA4 ? "Left Alt" : "Right Alt",
            var l => l,
        });

    public InputStatsView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Render();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Render(); _refresh.Start(); } else _refresh.Stop(); };
        Unloaded += (_, _) => _refresh.Stop();
    }

    public void Attach(AppSettings settings, InputStats stats)
    {
        _settings = settings;
        _stats = stats;
        TrackSwitch.IsChecked = settings.InputStatsEnabled;
    }

    public void EnsureLoaded() => Render();

    public static string KeyName(int vk) => KeyNames.TryGetValue(vk, out var n) ? n
        : System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk) is var k && k != System.Windows.Input.Key.None ? k.ToString() : $"Key {vk}";

    static string Count(double n) => n >= 1_000_000 ? $"{n / 1_000_000:0.##}M" : n >= 10_000 ? $"{n / 1000:0.#}k" : $"{n:N0}";

    string Distance(double pixels)
    {
        double m = pixels * (_data?.MmPerPixel ?? 0.25) / 1000;
        return m >= 1000 ? $"{m / 1000:0.##} km" : m >= 10 ? $"{m:0} m" : $"{m:0.#} m";
    }

    void Render()
    {
        if (_stats is null) return;
        _data = _stats.Snapshot();
        var today = _data.Days.GetValueOrDefault(DateTime.Today.ToString("yyyy-MM-dd")) ?? new InputDay();
        var all = _data.Days.Values;
        KeysToday.Text = Count(today.Keys);
        KeysAll.Text = $"{Count(_data.KeyCounts.Values.Sum())} all time";
        ClicksToday.Text = Count(today.Clicks);
        ClicksDetail.Text = $"{Count(today.LeftClicks)} left · {Count(today.RightClicks)} right · {Count(today.MiddleClicks + today.SideClicks)} other";
        DistanceToday.Text = Distance(today.Distance);
        double total = all.Sum(d => d.Distance);
        DistanceAll.Text = $"{Distance(total)} all time" + (total * _data.MmPerPixel / 1000 >= 400 ? " (taller than the Empire State Building)" : "");
        ScrollToday.Text = Count(today.Scrolls);
        ScrollAll.Text = $"{Count(all.Sum(d => d.Scrolls))} notches all time";
        SinceText.Text = _settings?.InputStatsEnabled == true ? $"Counting since {_data.Since:d MMMM yyyy}." : "Counting is off.";

        var games = _data.Games.OrderByDescending(g => g.Value.Seconds).ToList();
        var selected = GamePicker.SelectedItem as string;
        var names = games.Select(g => g.Key).ToList();
        if (GamePicker.ItemsSource is not List<string> oldNames || !oldNames.SequenceEqual(names)) GamePicker.ItemsSource = names;
        GamePicker.SelectedItem = selected is not null && _data.Games.ContainsKey(selected) ? selected : games.FirstOrDefault().Key;
        GameList.ItemsSource = games.Select(g => new GameInputRow(g.Key, ScreenTimeView.Short(TimeSpan.FromSeconds(g.Value.Seconds)),
            Count(g.Value.Keys), Count(g.Value.Clicks), g.Value.Apm > 0 ? $"{g.Value.Apm:0}" : "–",
            string.Join(" · ", g.Value.KeyCounts.OrderByDescending(k => k.Value).Take(5).Select(k => KeyName(k.Key))))).ToList();
        NoGamesText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesHeader.Visibility = games.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        RenderKeyboard();
        RenderHeat();
        RenderWeek();
    }

    void RenderKeyboard()
    {
        if (_data is null) return;
        var counts = KeysGame.IsChecked == true && GamePicker.SelectedItem is string game && _data.Games.TryGetValue(game, out var g)
            ? g.KeyCounts : _data.KeyCounts;
        GamePicker.Visibility = KeysGame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var top = counts.OrderByDescending(k => k.Value).Take(5).ToList();
        TopKeysText.Text = top.Count == 0 ? "No key presses counted yet."
            : "Most pressed: " + string.Join(", ", top.Select(k => $"{KeyName(k.Key)} ({Count(k.Value)})"));

        const double unit = 46, gap = 4;
        double max = Math.Max(1, counts.Values.DefaultIfEmpty(0).Max());
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        Keyboard.Children.Clear();
        double y = 0, width = 0;
        foreach (var row in Layout)
        {
            double x = 0;
            foreach (var (label, vk, w) in row)
            {
                if (label == "") { x += w * unit; continue; }
                long n = vk == 0 ? 0 : counts.GetValueOrDefault(vk);
                // Square root so the long tail of rarely used keys still shows some colour.
                double heat = n == 0 ? 0 : 0.12 + 0.88 * Math.Sqrt(n / max);
                var key = new Border
                {
                    Width = w * unit - gap, Height = unit - gap, CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Heat(heat, accent)),
                    BorderBrush = (Brush)FindResource("CardBorderBrush"), BorderThickness = new Thickness(1),
                    ToolTip = vk == 0 ? null : $"{KeyName(vk)}: {n:N0}",
                    Child = new TextBlock
                    {
                        Text = label, FontSize = label.Length > 3 ? 10 : 12, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = heat > 0.7 ? Brushes.Black : (Brush)FindResource("TextBrush"),
                    },
                };
                Canvas.SetLeft(key, x);
                Canvas.SetTop(key, y + (row == Layout[0] ? 0 : 8)); // a little space under the F-row
                Keyboard.Children.Add(key);
                x += w * unit;
            }
            width = Math.Max(width, x);
            y += unit;
        }
        Keyboard.Width = width;
        Keyboard.Height = y + 8;
    }

    /// <summary>Dark key → accent blue → amber → red as heat goes from 0 to 1.</summary>
    static Color Heat(double t, Color accent)
    {
        var dark = ((SolidColorBrush)Application.Current.Resources["DeepBrush"]).Color;
        var amber = Color.FromRgb(0xF5, 0xA5, 0x24);
        var red = Color.FromRgb(0xF2, 0x5F, 0x5C);
        static Color Mix(Color a, Color b, double f) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
        if (t <= 0) return dark;
        if (t < 0.5) return Mix(dark, accent, t / 0.5);
        if (t < 0.8) return Mix(accent, amber, (t - 0.5) / 0.3);
        return Mix(amber, red, (t - 0.8) / 0.2);
    }

    void RenderHeat()
    {
        if (_data is null) return;
        var cells = HeatClick.IsChecked == true ? _data.ClickHeat : _data.MoveHeat;
        int w = InputStatsData.HeatW, h = InputStatsData.HeatH;
        var density = Heatmaps.Smooth(cells, w, h);
        double max = Math.Max(0.000001, density.DefaultIfEmpty(0).Max());
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < cells.Length; i++)
        {
            // Log scale: the area around the taskbar would otherwise drown out everything else.
            double t = density[i] == 0 ? 0 : Math.Log(1 + density[i]) / Math.Log(1 + max);
            var c = Heat(t, accent);
            pixels[i * 4] = c.B; pixels[i * 4 + 1] = c.G; pixels[i * 4 + 2] = c.R; pixels[i * 4 + 3] = 255;
        }
        HeatImage.Source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        // Same shape as the screens; fit to the card's width.
        double vw = _data.ScreenWidth, vh = _data.ScreenHeight;
        double width = Math.Max(160, Math.Min(800, ActualWidth > 100 ? (ActualWidth - 112) / 2 : 440));
        HeatFrame.Width = width;
        HeatFrame.Height = Math.Clamp(width * vh / Math.Max(1, vw), 100, 450);
    }

    void RenderWeek()
    {
        if (_data is null) return;
        WeekBars.Children.Clear();
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
        var totals = days.Select(d => _data.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd")) is { } x ? x.Keys + x.Clicks : 0).ToList();
        double max = Math.Max(1, totals.Max());
        for (int i = 0; i < 7; i++)
        {
            var bar = new Border
            {
                Height = Math.Max(3, 100 * totals[i] / max), CornerRadius = new CornerRadius(4, 4, 0, 0), Width = 22,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = (Brush)FindResource(i == 6 ? "AccentBrush" : "CardBorderBrush"),
            };
            var column = new DockPanel { Background = Brushes.Transparent, ToolTip = $"{days[i]:dddd}: {totals[i]:N0}" };
            var label = new TextBlock { Text = i == 6 ? "Today" : days[i].ToString("ddd"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                                        Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)FindResource("MutedBrush") };
            DockPanel.SetDock(label, Dock.Bottom);
            column.Children.Add(label);
            column.Children.Add(bar);
            WeekBars.Children.Add(column);
        }
    }

    void Scope_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) RenderKeyboard(); }
    void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RenderKeyboard(); }
    void Heat_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) RenderHeat(); }

    void TrackSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _stats is null) return;
        _settings.InputStatsEnabled = TrackSwitch.IsChecked == true;
        _settings.Save();
        if (_settings.InputStatsEnabled) _stats.Start(); else _stats.Stop();
        Render();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_stats is null) return;
        if (MessageBox.Show("Delete all input stats?", "Input Stats", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _stats.Clear();
        Render();
    }
}
