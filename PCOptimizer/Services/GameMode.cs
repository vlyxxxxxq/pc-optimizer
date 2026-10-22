using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace PCOptimizer.Services;

public sealed record ActivityEntry(string Time, string Text);

/// <summary>
/// Turns Game Mode on manually or when a game starts, applies the boosts the user enabled,
/// and undoes every one of them when the game closes or Game Mode is turned off.
/// All public members are meant to be used from the UI thread.
/// </summary>
public sealed class GameModeService
{
    const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    readonly AppSettings _settings;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _checking;
    int? _suppressedPid;              // user switched off while this game was running
    string? _restorePlan, _restorePlanName;
    bool _keptAwake;
    readonly List<string> _toReopen = [];

    public GameDetector Detector { get; } = new();
    public bool IsActive { get; private set; }
    public bool IsAuto { get; private set; }
    public DetectedGame? Game { get; private set; }
    public DateTime? Since { get; private set; }
    public IReadOnlyList<string> Boosts { get; private set; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public event EventHandler? StateChanged;
    /// <summary>(title, message) for a user-facing notification.</summary>
    public event Action<string, string>? Notify;

    /// <summary>Live CPU/GPU/RAM numbers for the game being played, sampled every 2 seconds.</summary>
    public GameStats Stats { get; } = new();
    public event EventHandler? StatsUpdated;
    public event Action<GameSession>? SessionEnded;
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public GameModeService(AppSettings settings)
    {
        _settings = settings;
        _statsTimer.Tick += async (_, _) =>
        {
            if (!Stats.Running) return;
            await Task.Run(Stats.Sample);
            StatsUpdated?.Invoke(this, EventArgs.Empty);
        };
    }

    void BeginSession(DetectedGame game)
    {
        Stats.Start(game.Name, game.Pid);
        _statsTimer.Start();
    }

    void EndSession()
    {
        _statsTimer.Stop();
        if (Stats.Stop() is { } session)
        {
            Log($"Played {session.Game} for {session.DurationText} — {session.Summary}");
            SessionEnded?.Invoke(session);
        }
    }

    public void Start()
    {
        // A previous session ended without restoring the power plan (crash, forced shutdown).
        if (_settings.PendingRestorePlan is { } pending)
        {
            PowerPlans.SetActive(pending);
            _settings.PendingRestorePlan = null;
            _settings.Save();
        }

        Detector.RefreshLibraries();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public void RefreshLibraries() => Detector.RefreshLibraries();

    async Task TickAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (_suppressedPid is int sp && !GameDetector.IsAlive(sp)) _suppressedPid = null;

            if (IsActive)
            {
                if (Game is { } game && !GameDetector.IsAlive(game.Pid))
                {
                    if (IsAuto) { await DeactivateAsync($"{game.Name} closed"); return; }
                    EndSession();
                    Game = null; // manual mode keeps running without a game
                    Changed();
                }
                else if (Game is null)
                {
                    // Manual mode: adopt a game that starts later so it still gets priority.
                    var later = await Task.Run(() => Detector.Detect(_settings));
                    if (later is not null && IsActive && Game is null)
                    {
                        Game = later;
                        BeginSession(later);
                        if (_settings.BoostPriority) SetPriority(later.Pid, ProcessPriorityClass.High);
                        Log($"Detected {later.Name}");
                        Changed();
                    }
                }
                return;
            }

            if (!_settings.AutoGameMode) return;
            var found = await Task.Run(() => Detector.Detect(_settings));
            if (found is not null && found.Pid != _suppressedPid && !IsActive)
                await ActivateAsync(found, auto: true);
        }
        catch (Exception ex)
        {
            Log($"Detection error: {ex.Message}");
        }
        finally { _checking = false; }
    }

    public async Task ToggleAsync()
    {
        if (IsActive)
        {
            if (IsAuto && Game is not null) _suppressedPid = Game.Pid;
            await DeactivateAsync("turned off");
        }
        else
        {
            var found = await Task.Run(() => Detector.Detect(_settings));
            await ActivateAsync(found, auto: false);
        }
    }

    /// <summary>The current "game" isn't one: never detect it again and switch off.</summary>
    public async Task MarkNotGameAsync()
    {
        if (Game is not { } game) return;
        if (game.Source == "My games")
            _settings.CustomGames.RemoveAll(g => g.Equals(Path.GetFileName(game.Path), StringComparison.OrdinalIgnoreCase));
        else if (!_settings.IgnoredGames.Contains(game.Folder, StringComparer.OrdinalIgnoreCase))
            _settings.IgnoredGames.Add(game.Folder);
        _settings.Save();
        _suppressedPid = game.Pid;
        await DeactivateAsync($"{game.Name} won't be treated as a game");
        IgnoredChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? IgnoredChanged;

    async Task ActivateAsync(DetectedGame? game, bool auto)
    {
        IsActive = true;
        IsAuto = auto;
        Game = game;
        Since = DateTime.Now;
        if (game is not null) BeginSession(game);
        var boosts = new List<string>();

        if (_settings.BoostPowerPlan)
        {
            try
            {
                var plans = await PowerPlans.ListAsync();
                var current = plans.FirstOrDefault(p => p.IsActive);
                var target = plans.FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                             ?? plans.FirstOrDefault(p => p.Guid.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    Log("No high-performance power plan on this PC (add Ultimate Performance in Tools)");
                else if (current is not null && target.Guid != current.Guid && await PowerPlans.SetActiveAsync(target.Guid))
                {
                    _restorePlan = current.Guid;
                    _restorePlanName = current.Name;
                    _settings.PendingRestorePlan = current.Guid;
                    _settings.Save();
                    boosts.Add($"{target.Name} power plan");
                }
                else if (target.IsActive) boosts.Add($"{target.Name} power plan");
            }
            catch (Exception ex) { Log($"Power plan: {ex.Message}"); }
        }

        if (_settings.BoostPriority && game is not null && SetPriority(game.Pid, ProcessPriorityClass.High))
            boosts.Add("high CPU priority");

        if (_settings.KeepAwake)
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
            _keptAwake = true;
            boosts.Add("sleep blocked");
        }

        if (_settings.CloseApps && _settings.AppsToClose.Count > 0)
        {
            var names = _settings.AppsToClose.ToList();
            var closed = await Task.Run(() => CloseApps(names));
            if (closed.Count > 0) boosts.Add($"closed {string.Join(", ", closed)}");
        }

        Boosts = boosts;
        string what = game is null ? "Game Mode on" : $"Game Mode on · {game.Name}";
        Log($"{what}{(boosts.Count > 0 ? " — " + string.Join(", ", boosts) : "")}");
        Notify?.Invoke(game is null ? "Game Mode is on" : $"Game Mode on: {game.Name}",
            boosts.Count > 0 ? $"Boosted: {string.Join(", ", boosts)}." : "No boosts are enabled. Pick some in Game Mode.");
        Changed();
    }

    async Task DeactivateAsync(string reason)
    {
        if (!IsActive) return;
        var restored = new List<string>();

        if (_restorePlan is not null)
        {
            if (await PowerPlans.SetActiveAsync(_restorePlan)) restored.Add($"{_restorePlanName} power plan");
            _restorePlan = null;
            _settings.PendingRestorePlan = null;
            _settings.Save();
        }

        if (Game is { } game && GameDetector.IsAlive(game.Pid) && _settings.BoostPriority)
            SetPriority(game.Pid, ProcessPriorityClass.Normal);

        RestoreSleep();

        if (_toReopen.Count > 0)
        {
            int reopened = 0;
            if (_settings.ReopenApps)
                foreach (var path in _toReopen.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); reopened++; } catch { }
                }
            if (reopened > 0) restored.Add($"reopened {reopened} app{(reopened == 1 ? "" : "s")}");
            _toReopen.Clear();
        }

        EndSession();
        var played = Since is { } s ? Fmt.Duration(DateTime.Now - s) : null;
        IsActive = false;
        IsAuto = false;
        Game = null;
        Since = null;
        Boosts = [];

        Log($"Game Mode off — {reason}{(restored.Count > 0 ? "; restored " + string.Join(", ", restored) : "")}");
        Notify?.Invoke("Game Mode is off",
            $"{Capitalize(reason)}{(played is null ? "" : $" after {played}")}." +
            (restored.Count > 0 ? $" Restored {string.Join(", ", restored)}." : ""));
        Changed();
    }

    /// <summary>Synchronous restore for app exit, when there's no time for async work.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        if (!IsActive) return;
        EndSession(); // keep the session in history even when the app exits mid-game
        if (_restorePlan is not null && PowerPlans.SetActive(_restorePlan))
        {
            _settings.PendingRestorePlan = null;
            _settings.Save();
        }
        if (Game is { } game && _settings.BoostPriority) SetPriority(game.Pid, ProcessPriorityClass.Normal);
        RestoreSleep();
    }

    void RestoreSleep()
    {
        if (!_keptAwake) return;
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _keptAwake = false;
    }

    static bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Asks each app to close, then ends it if it ignores the request (tray apps have no window).</summary>
    List<string> CloseApps(List<string> names)
    {
        var closed = new List<string>();
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) continue;
            string? path = null;
            foreach (var p in procs)
            {
                using (p)
                {
                    try
                    {
                        path ??= Native.ProcessPath(p.Id);
                        if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                    }
                    catch { }
                }
            }
            if (path is not null) lock (_toReopen) _toReopen.Add(path);
            closed.Add(name);
        }
        return closed;
    }

    void Log(string text)
    {
        Activity.Insert(0, new ActivityEntry(DateTime.Now.ToString("t"), text));
        while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
    }

    void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
