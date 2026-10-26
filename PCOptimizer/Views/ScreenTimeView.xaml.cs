using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public sealed record AppTime(string Name, ImageSource? Icon, TimeSpan Time, double BarWidth)
{
    public string TimeText => ScreenTimeView.Short(Time);
}

public partial class ScreenTimeView : UserControl
{
    ScreenTime? _tracker;
    AppSettings? _settings;
    DateTime _day = DateTime.Today;
    readonly Dictionary<string, ImageSource?> _icons = [];

    public ScreenTimeView() => InitializeComponent();

    public void Attach(AppSettings settings, ScreenTime tracker)
    {
        _settings = settings;
        _tracker = tracker;
        TrackSwitch.IsChecked = settings.ScreenTimeEnabled;
        BreakSwitch.IsChecked = settings.BreakReminders;
        foreach (var rb in new[] { Break30, Break45, Break60, Break90, Break120 })
            if ((string)rb.Tag == settings.BreakMinutes.ToString()) rb.IsChecked = true;
        // Refresh at most every 30 s while the page is open.
        DateTime last = default;
        tracker.Updated += (_, _) =>
        {
            if (IsVisible && DateTime.Now - last > TimeSpan.FromSeconds(30)) { last = DateTime.Now; Render(); }
        };
    }

    public void EnsureLoaded() => Render();

    public static string Short(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : "<1m";

    void Render()
    {
        if (_tracker is null) return;
        var apps = _tracker.Day(_day);
        var total = TimeSpan.FromSeconds(apps.Values.Sum());
        bool today = _day == DateTime.Today;
        DayLabel.Text = today ? "TODAY" : _day.ToString("dddd d MMMM").ToUpperInvariant();
        DayTotal.Text = Short(total);
        AppsTitle.Text = today ? "Apps today" : $"Apps on {_day:dddd}";

        // Compare with the average of the previous 7 days that have data.
        var previous = Enumerable.Range(1, 7).Select(i => _tracker.Day(_day.AddDays(-i)).Values.Sum()).Where(s => s > 0).ToList();
        DayCompare.Text = previous.Count == 0 ? (_settings?.ScreenTimeEnabled == true ? "Tracking. Check back later." : "Tracking is off.")
            : $"{(total.TotalSeconds >= previous.Average() ? "Above" : "Below")} your daily average of {Short(TimeSpan.FromSeconds(previous.Average()))}";

        int max = Math.Max(1, apps.Values.DefaultIfEmpty(0).Max());
        var list = apps.OrderByDescending(kv => kv.Value).Take(15)
            .Select(kv => new AppTime(kv.Key, Icon(kv.Key), TimeSpan.FromSeconds(kv.Value), 400.0 * kv.Value / max)).ToList();
        AppList.ItemsSource = list;
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderWeek();
    }

    ImageSource? Icon(string app)
    {
        if (_icons.TryGetValue(app, out var icon)) return icon;
        return _icons[app] = _tracker!.Data.Paths.TryGetValue(app, out var path) ? FileIcons.For(path) : null;
    }

    void RenderWeek()
    {
        WeekBars.Children.Clear();
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
        var totals = days.Select(d => _tracker!.Day(d).Values.Sum()).ToList();
        double max = Math.Max(1, totals.Max());
        for (int i = 0; i < 7; i++)
        {
            var day = days[i];
            bool selected = day == _day;
            var bar = new Border
            {
                Height = Math.Max(3, 80 * totals[i] / max), CornerRadius = new CornerRadius(4, 4, 0, 0), Width = 26,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = (Brush)FindResource(selected ? "AccentBrush" : "CardBorderBrush"),
            };
            var column = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                                         ToolTip = $"{day:dddd}: {Short(TimeSpan.FromSeconds(totals[i]))}" };
            var label = new TextBlock { Text = day == DateTime.Today ? "Today" : day.ToString("ddd"), FontSize = 11,
                                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                                        Foreground = (Brush)FindResource(selected ? "TextBrush" : "MutedBrush") };
            DockPanel.SetDock(label, Dock.Bottom);
            column.Children.Add(label);
            column.Children.Add(bar);
            column.MouseLeftButtonUp += (_, _) => { _day = day; Render(); };
            WeekBars.Children.Add(column);
        }
    }

    void TrackSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _tracker is null) return;
        _settings.ScreenTimeEnabled = TrackSwitch.IsChecked == true;
        _settings.Save();
        if (_settings.ScreenTimeEnabled) _tracker.Start(); else _tracker.Stop();
        Render();
    }

    void BreakSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.BreakReminders = BreakSwitch.IsChecked == true;
        _settings.Save();
    }

    void BreakLength_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.BreakMinutes = int.Parse((string)((RadioButton)sender).Tag);
        _settings.Save();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker is null) return;
        if (MessageBox.Show("Delete all recorded screen time?", "Screen Time", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _tracker.Clear();
        Render();
    }
}
