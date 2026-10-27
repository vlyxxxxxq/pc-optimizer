using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Rig.Services;

public sealed class TimerItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"timer" counts down a duration; "reminder" fires at a time.</summary>
    public string Kind { get; set; } = "timer";
    public string Text { get; set; } = "";
    public TimeSpan Duration { get; set; }
    /// <summary>When it goes off; null while a timer is paused.</summary>
    public DateTime? EndsAt { get; set; }
    /// <summary>Time left on a paused timer.</summary>
    public TimeSpan Left { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsReminder => Kind == "reminder";
    [System.Text.Json.Serialization.JsonIgnore] public bool Paused => EndsAt is null;
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan Remaining => EndsAt is { } e ? (e - DateTime.Now) is var r && r > TimeSpan.Zero ? r : TimeSpan.Zero : Left;
    [System.Text.Json.Serialization.JsonIgnore]
    public string RemainingText => IsReminder && EndsAt is { } at
        ? (at.Date == DateTime.Today ? $"at {at:t}" : $"{at:ddd d MMM, t}") + $" · in {Timers.Format(Remaining)}"
        : Timers.Format(Remaining) + (Paused ? " · paused" : "");
    [System.Text.Json.Serialization.JsonIgnore] public string Title => Text.Length > 0 ? Text : IsReminder ? "Reminder" : $"{Timers.Format(Duration)} timer";
    [System.Text.Json.Serialization.JsonIgnore] public string PauseLabel => Paused ? "Resume" : "Pause";

    public void Refresh() { OnPropertyChanged(nameof(RemainingText)); OnPropertyChanged(nameof(PauseLabel)); }
}

/// <summary>
/// Countdown timers and reminders. Running ones are kept in timers.json, so a reminder set for tomorrow still
/// goes off after a restart (or as soon as Rig starts, if it was closed at the time).
/// </summary>
public sealed class Timers
{
    static string FilePath => AppSettings.DataFile("timers.json");
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public System.Collections.ObjectModel.ObservableCollection<TimerItem> Items { get; } = [];
    /// <summary>A timer finished or a reminder is due.</summary>
    public event Action<TimerItem>? Fired;

    public Timers()
    {
        foreach (var t in Load()) Items.Add(t);
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
    }

    public TimerItem StartTimer(TimeSpan duration, string text = "")
    {
        var t = new TimerItem { Kind = "timer", Text = text, Duration = duration, EndsAt = DateTime.Now + duration };
        Items.Add(t);
        Save();
        return t;
    }

    public TimerItem Remind(DateTime at, string text)
    {
        var t = new TimerItem { Kind = "reminder", Text = text, EndsAt = at };
        Items.Add(t);
        Save();
        return t;
    }

    public void TogglePause(TimerItem t)
    {
        if (t.IsReminder) return;
        if (t.EndsAt is { } e) { t.Left = e - DateTime.Now; t.EndsAt = null; }
        else t.EndsAt = DateTime.Now + t.Left;
        t.Refresh();
        Save();
    }

    public void Remove(TimerItem t) { Items.Remove(t); Save(); }

    /// <summary>Off while the Timers module is switched off: nothing goes off, and anything due meanwhile goes off when it's back on.</summary>
    public bool Enabled { get; set; } = true;

    void Tick()
    {
        if (!Enabled) return;
        foreach (var t in Items.ToList())
        {
            if (t.EndsAt is { } e && e <= DateTime.Now)
            {
                Items.Remove(t);
                Save();
                Fired?.Invoke(t);
            }
            else t.Refresh();
        }
    }

    public static string Format(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    static readonly Regex DurationPart = new(@"(\d+(?:\.\d+)?)\s*(h|hr|hrs|hours?|m|min|mins|minutes?|s|sec|secs|seconds?)\b", RegexOptions.IgnoreCase);

    /// <summary>"1h 30m", "25 min", "90s" → a duration.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var total = TimeSpan.Zero;
        bool any = false;
        foreach (Match m in DurationPart.Matches(text))
        {
            any = true;
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            total += unit.StartsWith('h') ? TimeSpan.FromHours(n) : unit.StartsWith('s') ? TimeSpan.FromSeconds(n) : TimeSpan.FromMinutes(n);
        }
        return any && total > TimeSpan.Zero ? total : null;
    }

    static readonly Regex RemindIn = new(@"^remind me in (.+?)(?: to (.+))?$", RegexOptions.IgnoreCase);
    static readonly Regex RemindAt = new(@"^remind me (?:at|@) (\d{1,2}(?::\d{2})?\s*(?:am|pm)?)(?: (?:tomorrow))?(?: to (.+))?$", RegexOptions.IgnoreCase);
    static readonly Regex TimerFor = new(@"^timer (?:for )?(.+?)(?: (?:for|called|named) (.+))?$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Palette phrases: "remind me in 20 min to stretch", "remind me at 6:30pm to call mum", "timer 25m focus".
    /// Returns a description and what to do, or null.
    /// </summary>
    public (string Title, string Detail, Action Run)? Parse(string q)
    {
        q = q.Trim();
        var m = RemindIn.Match(q);
        if (m.Success && ParseDuration(m.Groups[1].Value) is { } inTime)
        {
            var text = m.Groups[2].Success ? m.Groups[2].Value : "Reminder";
            return ($"Remind me in {Format(inTime)}: {text}", $"At {DateTime.Now + inTime:t} · Enter sets it", () => Remind(DateTime.Now + inTime, text));
        }
        m = RemindAt.Match(q);
        if (m.Success && DateTime.TryParse(m.Groups[1].Value.Replace(" ", ""), CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var clock))
        {
            var at = DateTime.Today + clock.TimeOfDay;
            if (at <= DateTime.Now || q.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) at = at.AddDays(1);
            var text = m.Groups[2].Success ? m.Groups[2].Value : "Reminder";
            return ($"Remind me at {at:t}: {text}", $"{(at.Date == DateTime.Today ? "Today" : "Tomorrow")} · Enter sets it", () => Remind(at, text));
        }
        m = TimerFor.Match(q);
        if (m.Success)
        {
            // "timer 25m focus": the duration words, then anything else is the name.
            var rest = m.Groups[1].Value;
            if (ParseDuration(rest) is { } d)
            {
                var name = m.Groups[2].Success ? m.Groups[2].Value : DurationPart.Replace(rest, "").Trim();
                return ($"Start a {Format(d)} timer{(name.Length > 0 ? $": {name}" : "")}", "Enter starts it", () => StartTimer(d, name));
            }
        }
        return null;
    }

    static List<TimerItem> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<TimerItem>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList()));
        }
        catch { }
    }
}
