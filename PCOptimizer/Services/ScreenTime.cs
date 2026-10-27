using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace PCOptimizer.Services;

public sealed class ScreenTimeData
{
    /// <summary>"yyyy-MM-dd" -> app name -> seconds in front.</summary>
    public Dictionary<string, Dictionary<string, int>> Days { get; set; } = [];
    /// <summary>App name -> exe path, for icons.</summary>
    public Dictionary<string, string> Paths { get; set; } = [];
}

/// <summary>
/// Counts how long each app is the one you're using (foreground and not idle for 2+ minutes).
/// Stays on this PC in screentime.json; 60 days are kept.
/// </summary>
public sealed class ScreenTime
{
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    const int TickSeconds = 5;
    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2), BreakResetsAfter = TimeSpan.FromMinutes(5);

    static string FilePath => AppSettings.DataFile("screentime.json");

    readonly AppSettings _settings;
    readonly Func<bool> _gaming;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(TickSeconds) };
    readonly Dictionary<int, (string Name, string? Path)> _names = [];
    public ScreenTimeData Data { get; }
    int _unsaved;
    TimeSpan _activeStretch;
    DateTime _lastActive = DateTime.Now;

    public event Action<string, string>? Notify;
    public event EventHandler? Updated;

    public ScreenTime(AppSettings settings, Func<bool> gaming)
    {
        _settings = settings;
        _gaming = gaming;
        Data = Load();
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() { if (_settings.ScreenTimeEnabled) _timer.Start(); }
    public void Stop() { _timer.Stop(); Save(); }

    static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    void Tick()
    {
        // Gaming with a controller sends no keyboard/mouse input, so time in Game Mode always counts.
        bool idle = IdleTime() > IdleAfter && !_gaming();
        if (idle)
        {
            if (DateTime.Now - _lastActive > BreakResetsAfter) _activeStretch = TimeSpan.Zero; // a real break
            return;
        }
        _lastActive = DateTime.Now;

        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Native.GetShellWindow()) return;
        Native.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0) return;
        var (name, path) = NameFor(pid);
        if (name is "" or "Idle") return;

        var day = DateTime.Today.ToString("yyyy-MM-dd");
        if (!Data.Days.TryGetValue(day, out var apps)) Data.Days[day] = apps = [];
        apps[name] = apps.GetValueOrDefault(name) + TickSeconds;
        if (path is not null) Data.Paths[name] = path;
        if (++_unsaved >= 12) Save(); // about once a minute

        _activeStretch += TimeSpan.FromSeconds(TickSeconds);
        if (_settings.BreakReminders && _activeStretch >= TimeSpan.FromMinutes(Math.Max(15, _settings.BreakMinutes)) && !_gaming())
        {
            _activeStretch = TimeSpan.Zero;
            Notify?.Invoke("Time for a break", $"You've been at the screen for {_settings.BreakMinutes} minutes. Look away, stretch, grab some water.");
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    (string Name, string? Path) NameFor(int pid)
    {
        if (_names.TryGetValue(pid, out var cached)) return cached;
        if (_names.Count > 500) _names.Clear();
        var path = Native.ProcessPath(pid);
        string name;
        try
        {
            var info = path is null ? null : FileVersionInfo.GetVersionInfo(path);
            name = info?.FileDescription?.Trim() is { Length: > 0 } d ? d
                 : info?.ProductName?.Trim() is { Length: > 0 } p ? p
                 : path is null ? "" : Path.GetFileNameWithoutExtension(path);
        }
        catch { name = path is null ? "" : Path.GetFileNameWithoutExtension(path); }
        // Windows' own shell parts read better under one name.
        if (name is "Windows Explorer" or "Windows Start Experience Host" or "Search application") name = "Windows";
        return _names[pid] = (name, path);
    }

    public Dictionary<string, int> Day(DateTime date) =>
        Data.Days.GetValueOrDefault(date.ToString("yyyy-MM-dd")) ?? [];

    static ScreenTimeData Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ScreenTimeData>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        _unsaved = 0;
        try
        {
            var cutoff = DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd");
            foreach (var old in Data.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) Data.Days.Remove(old);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data));
        }
        catch { }
    }

    public void Clear()
    {
        Data.Days.Clear();
        Data.Paths.Clear();
        Save();
    }
}
