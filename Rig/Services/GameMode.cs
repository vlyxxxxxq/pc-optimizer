using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace Rig.Services;

public sealed record ActivityEntry(string Time, string Text);

public enum GameModeState { Off, Starting, On, Restoring, RecoveryNeeded }

/// <summary>
/// Turns Game Mode on manually or when a game starts, applies the boosts the user enabled,
/// and undoes every one of them when the game closes or Game Mode is turned off.
/// All public members are meant to be used from the UI thread.
/// </summary>
public sealed class GameModeService
{
    readonly AppSettings _settings;
    readonly IGameModeSystem _system;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _checking;
    int? _suppressedPid;              // user switched off while this game was running
    bool _keptAwake;
    readonly List<string> _toReopen = [];

    public GameDetector Detector { get; } = new();
    public bool IsActive { get; private set; }
    public bool IsAuto { get; private set; }
    public DetectedGame? Game { get; private set; }
    public DateTime? Since { get; private set; }
    public IReadOnlyList<string> Boosts { get; private set; } = [];
    /// <summary>What was skipped or failed when turning on, or what's still to be put back.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];
    public GameModeState State { get; private set; }
    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public event EventHandler? StateChanged;
    /// <summary>(title, message) for a user-facing notification.</summary>
    public event Action<string, string>? Notify;

    /// <summary>Live CPU/GPU/RAM numbers for the game being played, sampled every 2 seconds.</summary>
    public GameStats Stats { get; } = new();
    public event EventHandler? StatsUpdated;
    public event Action<GameSession>? SessionEnded;
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public GameModeService(AppSettings settings, IGameModeSystem? system = null)
    {
        _settings = settings;
        _system = system ?? new WindowsGameModeSystem();
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

    /// <summary>Finishes restoring anything a previous session left changed (crash, power cut, failed restore).</summary>
    public void RecoverAtStart()
    {
        // 2.0 kept two flags; they become the recovery record (the plan we set is unknown, so restore regardless).
        if (_settings.PendingRestorePlan is { } legacyPlan) { R.PowerPlan ??= legacyPlan; _settings.PendingRestorePlan = null; }
        if (_settings.PendingDisplayRestore) { R.DisplayChanged = true; _settings.PendingDisplayRestore = false; }
        if (!R.Pending) return;
        var restored = RestoreChanges();
        Problems = R.Problems.ToList();
        State = R.Pending ? GameModeState.RecoveryNeeded : GameModeState.Off;
        Log(R.Pending ? $"Couldn't restore after the last session: {string.Join("; ", R.Problems)}" : $"Restored after the last session: {string.Join(", ", restored.DefaultIfEmpty("everything"))}");
        if (R.Pending) Notify?.Invoke("Game Mode couldn't put everything back", $"Still changed: {string.Join("; ", R.Problems)}. Open Game Mode to try again.");
    }

    public void Start()
    {
        RecoverAtStart();
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
                        if (_settings.ProfileFor(later.Name)?.Priority ?? _settings.BoostPriority)
                        {
                            var boosts = Boosts.ToList(); var problems = Problems.ToList();
                            await _gate.WaitAsync();
                            try { RaisePriority(later, boosts, problems); } finally { _gate.Release(); }
                            Boosts = boosts; Problems = problems;
                        }
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
        if (State is GameModeState.Starting or GameModeState.Restoring) return; // a transition is already under way
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

    // ---------- Turning on and off ----------

    /// <summary>Only one transition at a time: rapid toggles, detection, shutdown and updates queue up instead of interleaving.</summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    GameModeRecovery R => _settings.Recovery;

    /// <summary>Writes the recovery record. If it can't be written, the change isn't made.</summary>
    bool Persist() => _settings.Save();

    async Task ActivateAsync(DetectedGame? game, bool auto)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsActive) return;
            State = GameModeState.Starting;
            IsActive = true;
            IsAuto = auto;
            Game = game;
            Since = DateTime.Now;
            Changed();
            if (game is not null) BeginSession(game);
            var boosts = new List<string>();
            var problems = new List<string>();

            // A game's own profile replaces the default boosts.
            var profile = _settings.ProfileFor(game?.Name);
            bool wantPlan = profile?.PowerPlan ?? _settings.BoostPowerPlan;
            bool wantPriority = profile?.Priority ?? _settings.BoostPriority;
            bool wantAwake = profile?.KeepAwake ?? _settings.KeepAwake;
            bool wantClose = profile?.CloseApps ?? _settings.CloseApps;
            if (profile is not null) boosts.Add($"{game!.Name} profile");

            if (wantPlan) await Task.Run(() => ApplyPlan(boosts, problems));
            if (wantPriority && game is not null) RaisePriority(game, boosts, problems);
            if (DisplayModes.Parse(profile?.DisplayMode) is { } m) ApplyDisplay(new DisplayMode(m.W, m.H, m.Hz), boosts, problems);
            if (wantAwake) { _system.KeepAwake(true); _keptAwake = true; boosts.Add("sleep blocked"); }
            if (wantClose && _settings.AppsToClose.Count > 0)
            {
                var names = _settings.AppsToClose.ToList();
                bool force = _settings.ForceCloseApps;
                var (closed, stayed) = await Task.Run(() => CloseApps(names, force));
                if (closed.Count > 0) boosts.Add($"closed {string.Join(", ", closed)}");
                if (stayed.Count > 0) problems.Add($"{string.Join(", ", stayed)} didn't close (maybe unsaved work), so it was left open");
            }

            Boosts = boosts;
            Problems = problems;
            State = GameModeState.On;
            string what = game is null ? "Game Mode on" : $"Game Mode on · {game.Name}";
            Log($"{what}{(boosts.Count > 0 ? " — " + string.Join(", ", boosts) : "")}");
            foreach (var p in problems) Log($"Skipped: {p}");
            Notify?.Invoke(game is null ? "Game Mode is on" : $"Game Mode on: {game.Name}",
                (boosts.Count > 0 ? $"Boosted: {string.Join(", ", boosts)}." : "No boosts are enabled. Pick some in Game Mode.") +
                (problems.Count > 0 ? $" Not done: {string.Join("; ", problems)}." : ""));
        }
        finally
        {
            _gate.Release();
            Changed();
        }
    }

    void ApplyPlan(List<string> boosts, List<string> problems)
    {
        var current = _system.ActivePlan();
        if (_system.BestPlan() is not { } target) { problems.Add("no high-performance power plan on this PC (add Ultimate Performance in Tools)"); return; }
        if (current is null) { problems.Add("couldn't read the current power plan, so it wasn't changed"); return; }
        if (current.Equals(target.Guid, StringComparison.OrdinalIgnoreCase)) { boosts.Add($"{target.Name} power plan (already on)"); return; }
        // Write down where to go back to before changing anything.
        if (R.PowerPlan is null) { R.PowerPlan = current; R.PowerPlanName = _system.PlanName(current); }
        R.PowerPlanSet = target.Guid;
        if (!Persist()) { R.PowerPlan = null; R.PowerPlanSet = null; problems.Add("couldn't save the recovery record, so the power plan wasn't changed"); return; }
        if (_system.SetPlan(target.Guid)) boosts.Add($"{target.Name} power plan");
        else
        {
            R.PowerPlan = null; R.PowerPlanSet = null; Persist();
            problems.Add($"Windows refused the {target.Name} power plan");
        }
    }

    void RaisePriority(DetectedGame game, List<string> boosts, List<string> problems)
    {
        if (_system.Process(game.Pid) is not { } p) return;
        if (p.Priority is ProcessPriorityClass.High or ProcessPriorityClass.RealTime) { boosts.Add("high CPU priority (already)"); return; }
        R.PriorityPid = game.Pid; R.PriorityStart = p.Start; R.PriorityOriginal = p.Priority; R.PrioritySet = ProcessPriorityClass.High;
        if (!Persist()) { R.PriorityPid = 0; problems.Add("couldn't save the recovery record, so priority wasn't changed"); return; }
        if (_system.SetPriority(game.Pid, ProcessPriorityClass.High)) boosts.Add("high CPU priority");
        else { R.PriorityPid = 0; Persist(); problems.Add("Windows didn't allow a higher priority for the game"); }
    }

    void ApplyDisplay(DisplayMode wanted, List<string> boosts, List<string> problems)
    {
        if (_system.CurrentDisplay() is not { } now) { problems.Add("couldn't read the display, so the resolution wasn't changed"); return; }
        if (now == wanted) return;
        if (!R.DisplayChanged) R.DisplayOriginal = now;
        R.DisplayChanged = true; R.DisplaySet = wanted;
        if (!Persist()) { R.DisplayChanged = false; problems.Add("couldn't save the recovery record, so the resolution wasn't changed"); return; }
        if (_system.SetDisplay(wanted)) boosts.Add(wanted.ToString());
        else { R.DisplayChanged = false; R.DisplayOriginal = R.DisplaySet = null; Persist(); problems.Add($"the display refused {wanted}"); }
    }

    async Task DeactivateAsync(string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsActive) return;
            State = GameModeState.Restoring;
            Changed();
            var restored = await Task.Run(RestoreChanges);
            if (_keptAwake) { _system.KeepAwake(false); _keptAwake = false; }

            if (_toReopen.Count > 0)
            {
                int reopened = 0;
                if (_settings.ReopenApps)
                    foreach (var path in _toReopen.Distinct(StringComparer.OrdinalIgnoreCase))
                        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); reopened++; } catch { }
                // Only the program starts again; documents or sessions it had open aren't restored.
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
            Problems = R.Problems.ToList();
            State = R.Pending ? GameModeState.RecoveryNeeded : GameModeState.Off;

            Log($"Game Mode off — {reason}{(restored.Count > 0 ? "; restored " + string.Join(", ", restored) : "")}");
            foreach (var p in R.Problems) Log($"Not restored yet: {p}");
            Notify?.Invoke(State == GameModeState.RecoveryNeeded ? "Game Mode is off, but something wasn't put back" : "Game Mode is off",
                $"{Capitalize(reason)}{(played is null ? "" : $" after {played}")}." +
                (restored.Count > 0 ? $" Restored {string.Join(", ", restored)}." : "") +
                (R.Problems.Count > 0 ? $" Still to restore: {string.Join("; ", R.Problems)}. Open Game Mode to try again." : ""));
        }
        finally
        {
            _gate.Release();
            Changed();
        }
    }

    /// <summary>
    /// Puts back everything in the recovery record that it safely can. Each item is cleared only after it has been
    /// restored, or when there's nothing left to restore (the game exited, or you changed the setting yourself since).
    /// </summary>
    List<string> RestoreChanges()
    {
        var restored = new List<string>();
        R.Problems.Clear();

        if (R.PowerPlan is { } plan)
        {
            var current = _system.ActivePlan();
            if (R.PowerPlanSet is not null && current is not null && !current.Equals(R.PowerPlanSet, StringComparison.OrdinalIgnoreCase))
                Log("Power plan was changed meanwhile, so it was left as it is");
            else if (_system.SetPlan(plan)) restored.Add($"{R.PowerPlanName ?? "previous"} power plan");
            else { R.Problems.Add($"the {R.PowerPlanName ?? "previous"} power plan"); plan = null!; }
            if (plan is not null) { R.PowerPlan = R.PowerPlanName = R.PowerPlanSet = null; }
            Persist();
        }

        if (R.PriorityPid != 0)
        {
            // Same process (PID and start time) and still at the priority Game Mode gave it: then it's ours to change back.
            if (_system.Process(R.PriorityPid) is { } p && Math.Abs((p.Start - R.PriorityStart).TotalSeconds) < 1 && p.Priority == R.PrioritySet)
            {
                if (_system.SetPriority(R.PriorityPid, R.PriorityOriginal)) { restored.Add("game priority"); R.PriorityPid = 0; }
                else R.Problems.Add("the game's priority");
            }
            else R.PriorityPid = 0; // the game is gone (or was changed by hand): nothing to put back
            Persist();
        }

        if (R.DisplayChanged)
        {
            var now = _system.CurrentDisplay();
            if (now is null) R.Problems.Add("the resolution (the display isn't connected right now)");
            else if (R.DisplaySet is not null && now != R.DisplaySet) { Log("Resolution was changed meanwhile, so it was left as it is"); Clear(); }
            else if (_system.RestoreDisplay(R.DisplayOriginal)) { restored.Add("resolution"); Clear(); }
            else R.Problems.Add($"the resolution ({R.DisplayOriginal?.ToString() ?? "Windows' setting"})");
            Persist();

            void Clear() { R.DisplayChanged = false; R.DisplayOriginal = R.DisplaySet = null; }
        }
        return restored;
    }

    /// <summary>Tries again to put back what a previous session (or a crash) left changed.</summary>
    public async Task<bool> RetryRecoveryAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsActive) return false;
            var restored = await Task.Run(RestoreChanges);
            Problems = R.Problems.ToList();
            State = R.Pending ? GameModeState.RecoveryNeeded : GameModeState.Off;
            Log(R.Pending ? $"Still couldn't restore: {string.Join("; ", R.Problems)}" : $"Restored {string.Join(", ", restored.DefaultIfEmpty("everything"))}");
            return !R.Pending;
        }
        finally { _gate.Release(); Changed(); }
    }

    /// <summary>
    /// For app exit and restarts: waits for any transition in progress, then puts everything back. Whatever can't be
    /// restored stays in the recovery record for the next start.
    /// </summary>
    public void Shutdown()
    {
        _timer.Stop();
        if (!_gate.Wait(TimeSpan.FromSeconds(15))) return; // a transition is stuck; the record is still on disk
        try
        {
            if (IsActive) EndSession(); // keep the session in history even when the app exits mid-game
            if (R.Pending) RestoreChanges();
            if (_keptAwake) { _system.KeepAwake(false); _keptAwake = false; }
            IsActive = false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Asks each app to close; only ends one that won't if force-closing was chosen.</summary>
    (List<string> Closed, List<string> Stayed) CloseApps(List<string> names, bool force)
    {
        var closed = new List<string>();
        var stayed = new List<string>();
        foreach (var name in names)
        {
            var (ok, path) = _system.CloseApp(name, force);
            if (path is null) continue; // wasn't running
            if (ok) { lock (_toReopen) _toReopen.Add(path); closed.Add(name); }
            else stayed.Add(name);
        }
        return (closed, stayed);
    }

    void Log(string text)
    {
        Activity.Insert(0, new ActivityEntry(DateTime.Now.ToString("t"), text));
        while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
    }

    void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
