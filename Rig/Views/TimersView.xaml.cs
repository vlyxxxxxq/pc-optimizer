using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class TimersView : UserControl
{
    Timers? _timers;
    readonly Stopwatch _watch = new();
    readonly DispatcherTimer _watchTick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    TimeSpan _lastLap;

    public TimersView()
    {
        InitializeComponent();
        _watchTick.Tick += (_, _) => StopwatchText.Text = Format(_watch.Elapsed);
    }

    public void Attach(Timers timers)
    {
        _timers = timers;
        ItemList.ItemsSource = timers.Items;
        timers.Items.CollectionChanged += (_, _) => RenderEmpty();
        RenderEmpty();
    }

    void RenderEmpty() => EmptyText.Visibility = _timers?.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        var minutes = int.Parse((string)((Button)sender).Tag);
        _timers?.StartTimer(TimeSpan.FromMinutes(minutes), TimerName.Text.Trim());
        TimerName.Clear();
    }

    void StartTimer_Click(object sender, RoutedEventArgs e)
    {
        if (Timers.ParseDuration(TimerLength.Text) is not { } d) { TimerError.Text = "Try something like 15m, 1h 30m or 90s."; return; }
        TimerError.Text = "";
        _timers?.StartTimer(d, TimerName.Text.Trim());
        TimerName.Clear();
    }

    void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        var text = ReminderText.Text.Trim();
        if (text.Length == 0) { ReminderError.Text = "What should I remind you about?"; return; }
        DateTime at;
        if (RemindIn.IsChecked == true)
        {
            if (Timers.ParseDuration(RemindInBox.Text) is not { } d) { ReminderError.Text = "Try something like 30m or 2h."; return; }
            at = DateTime.Now + d;
        }
        else
        {
            if (!DateTime.TryParse(RemindAtBox.Text.Replace(" ", ""), CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var clock))
            { ReminderError.Text = "Try a time like 18:30 or 6:30pm."; return; }
            at = DateTime.Today + clock.TimeOfDay;
            if (at <= DateTime.Now) at = at.AddDays(1);
        }
        ReminderError.Text = "";
        _timers?.Remind(at, text);
        ReminderText.Clear();
    }

    void PauseButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is TimerItem { IsReminder: true }) ((Button)sender).Visibility = Visibility.Collapsed;
    }

    void Pause_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is TimerItem t) _timers?.TogglePause(t); }
    void Remove_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is TimerItem t) _timers?.Remove(t); }

    void StopwatchStart_Click(object sender, RoutedEventArgs e)
    {
        if (_watch.IsRunning) { _watch.Stop(); _watchTick.Stop(); StopwatchStart.Content = "Start"; }
        else { _watch.Start(); _watchTick.Start(); StopwatchStart.Content = "Stop"; }
        StopwatchText.Text = Format(_watch.Elapsed);
    }

    void Lap_Click(object sender, RoutedEventArgs e)
    {
        if (!_watch.IsRunning) return;
        var now = _watch.Elapsed;
        LapList.Items.Insert(0, new TextBlock
        {
            Text = $"Lap {LapList.Items.Count + 1}   {Format(now - _lastLap)}   (total {Format(now)})",
            Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0),
        });
        _lastLap = now;
    }

    void StopwatchReset_Click(object sender, RoutedEventArgs e)
    {
        _watch.Reset();
        _watchTick.Stop();
        _lastLap = TimeSpan.Zero;
        LapList.Items.Clear();
        StopwatchStart.Content = "Start";
        StopwatchText.Text = Format(TimeSpan.Zero);
    }
}
