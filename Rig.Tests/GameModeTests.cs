using System.Diagnostics;
using System.Reflection;
using Rig.Services;

namespace Rig.Tests;

/// <summary>A pretend PC: power plans, a display and processes live in memory, and any call can be made to fail.</summary>
sealed class FakePc : IGameModeSystem
{
    public string Plan = "balanced";
    public bool PlanFails, DisplayFails, RestoreDisplayFails, PriorityFails, DisplayGone;
    public DisplayMode Display = new(2560, 1440, 165);
    public readonly Dictionary<int, (DateTime Start, ProcessPriorityClass Priority)> Procs = [];
    public int SetPlanCalls;

    public string? ActivePlan() => Plan;
    public (string Guid, string Name)? BestPlan() => ("ultimate", "Ultimate Performance");
    public string? PlanName(string guid) => guid == "balanced" ? "Balanced" : guid;
    public bool SetPlan(string guid) { SetPlanCalls++; if (PlanFails) return false; Plan = guid; return true; }
    public DisplayMode? CurrentDisplay() => DisplayGone ? null : Display;
    public bool SetDisplay(DisplayMode m) { if (DisplayFails) return false; Display = m; return true; }
    public bool RestoreDisplay(DisplayMode? m) { if (RestoreDisplayFails || m is null) return false; Display = m; return true; }
    public (DateTime Start, ProcessPriorityClass Priority)? Process(int pid) => Procs.TryGetValue(pid, out var p) ? p : null;
    public bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        if (PriorityFails || !Procs.ContainsKey(pid)) return false;
        Procs[pid] = (Procs[pid].Start, priority);
        return true;
    }
    public void KeepAwake(bool on) { }
    public (bool Closed, string? Path) CloseApp(string name, bool force) => (false, null);
}

public class GameModeTests
{
    // GameModeService is driven through its private transitions here (no UI thread or timers needed).
    static Task Activate(GameModeService s, DetectedGame? g) =>
        (Task)typeof(GameModeService).GetMethod("ActivateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, [g, false])!;
    static Task Deactivate(GameModeService s) =>
        (Task)typeof(GameModeService).GetMethod("DeactivateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, ["turned off"])!;

    static DetectedGame Game(int pid) => new(pid, "Test Game", @"C:\Games\test.exe", "My games", @"C:\Games\");

    static (GameModeService Service, AppSettings Settings, FakePc Pc) Make(Sandbox _, Action<AppSettings>? setup = null)
    {
        var settings = new AppSettings { BoostPowerPlan = true, BoostPriority = true, KeepAwake = false, CloseApps = false };
        setup?.Invoke(settings);
        var pc = new FakePc();
        return (new GameModeService(settings, pc), settings, pc);
    }

    [Fact]
    public async Task On_then_off_puts_everything_back()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box, st => st.GameProfiles = [new GameProfile { Game = "Test Game", DisplayMode = "1920 × 1080 @ 360 Hz" }]);
        pc.Procs[42] = (DateTime.Now, ProcessPriorityClass.BelowNormal);
        await Activate(s, Game(42));
        Assert.Equal(GameModeState.On, s.State);
        Assert.Equal("ultimate", pc.Plan);
        Assert.Equal(new DisplayMode(1920, 1080, 360), pc.Display);
        Assert.Equal(ProcessPriorityClass.High, pc.Procs[42].Priority);
        Assert.True(settings.Recovery.Pending);

        await Deactivate(s);
        Assert.Equal(GameModeState.Off, s.State);
        Assert.Equal("balanced", pc.Plan);
        Assert.Equal(new DisplayMode(2560, 1440, 165), pc.Display);
        Assert.Equal(ProcessPriorityClass.BelowNormal, pc.Procs[42].Priority); // its original, not "Normal"
        Assert.False(settings.Recovery.Pending);
    }

    [Fact]
    public async Task A_failed_restore_is_kept_and_retried_later()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box);
        await Activate(s, null);
        pc.PlanFails = true;
        await Deactivate(s);
        Assert.Equal(GameModeState.RecoveryNeeded, s.State);
        Assert.Equal("balanced", settings.Recovery.PowerPlan);
        Assert.NotEmpty(s.Problems);

        pc.PlanFails = false;
        Assert.True(await s.RetryRecoveryAsync());
        Assert.Equal("balanced", pc.Plan);
        Assert.Equal(GameModeState.Off, s.State);
    }

    [Fact]
    public async Task Recovery_survives_a_restart()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box);
        await Activate(s, null);
        // The app dies here. Next start reads the record from disk.
        var reloaded = AppSettings.Load();
        Assert.Equal("balanced", reloaded.Recovery.PowerPlan);
        var next = new GameModeService(reloaded, pc);
        next.RecoverAtStart();
        Assert.Equal("balanced", pc.Plan);
        Assert.False(reloaded.Recovery.Pending);
    }

    [Fact]
    public async Task A_power_plan_you_changed_yourself_is_left_alone()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box);
        await Activate(s, null);
        pc.Plan = "power-saver"; // changed by hand during the session
        int calls = pc.SetPlanCalls;
        await Deactivate(s);
        Assert.Equal("power-saver", pc.Plan);
        Assert.Equal(calls, pc.SetPlanCalls);
        Assert.False(settings.Recovery.Pending);
    }

    [Fact]
    public async Task A_reused_process_id_is_never_touched()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box, st => st.BoostPowerPlan = false);
        pc.Procs[42] = (new DateTime(2026, 1, 1, 10, 0, 0), ProcessPriorityClass.Normal);
        await Activate(s, Game(42));
        // The game exits and an unrelated process gets PID 42, at High priority of its own.
        pc.Procs[42] = (new DateTime(2026, 1, 1, 11, 0, 0), ProcessPriorityClass.High);
        await Deactivate(s);
        Assert.Equal(ProcessPriorityClass.High, pc.Procs[42].Priority);
        Assert.False(settings.Recovery.Pending);
    }

    [Fact]
    public async Task A_rejected_display_mode_is_reported_and_nothing_is_left_to_restore()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box, st => { st.BoostPowerPlan = false; st.GameProfiles = [new GameProfile { Game = "Test Game", Priority = false, PowerPlan = false, DisplayMode = "1280 × 720 @ 60 Hz" }]; });
        pc.DisplayFails = true;
        pc.Procs[7] = (DateTime.Now, ProcessPriorityClass.Normal);
        await Activate(s, Game(7));
        Assert.Contains(s.Problems, p => p.Contains("refused"));
        Assert.False(settings.Recovery.DisplayChanged);
    }

    [Fact]
    public async Task A_disconnected_display_keeps_its_restore_for_later()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box, st => { st.BoostPowerPlan = false; st.GameProfiles = [new GameProfile { Game = "Test Game", Priority = false, PowerPlan = false, DisplayMode = "1280 × 720 @ 60 Hz" }]; });
        pc.Procs[7] = (DateTime.Now, ProcessPriorityClass.Normal);
        await Activate(s, Game(7));
        pc.DisplayGone = true;
        await Deactivate(s);
        Assert.Equal(GameModeState.RecoveryNeeded, s.State);
        Assert.True(settings.Recovery.DisplayChanged);
        pc.DisplayGone = false;
        Assert.True(await s.RetryRecoveryAsync());
        Assert.Equal(new DisplayMode(2560, 1440, 165), pc.Display);
    }

    [Fact]
    public async Task Rapid_toggles_never_interleave()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box);
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++) { tasks.Add(Activate(s, null)); tasks.Add(Deactivate(s)); }
        await Task.WhenAll(tasks);
        await Deactivate(s);
        Assert.Equal("balanced", pc.Plan);
        Assert.False(settings.Recovery.Pending);
        Assert.Equal(GameModeState.Off, s.State);
    }

    [Fact]
    public async Task Activation_uses_the_installation_profile_even_when_the_detected_name_differs()
    {
        using var box = new Sandbox();
        var (service, _, pc) = Make(box, settings => settings.GameProfiles =
        [new GameProfile { Game = "Library display name", MatchPath = @"C:\Games", PowerPlan = false, Priority = false, KeepAwake = false }]);
        pc.Procs[7] = (DateTime.Now, ProcessPriorityClass.Normal);
        try
        {
            await Activate(service, Game(7));
            Assert.Equal(GameModeState.On, service.State);
            Assert.Equal(0, pc.SetPlanCalls);
            Assert.Equal(ProcessPriorityClass.Normal, pc.Procs[7].Priority);
            Assert.Contains("Test Game profile", service.Boosts);
        }
        finally { await Deactivate(service); }
    }

    [Fact]
    public async Task Shutdown_restores_and_keeps_what_it_could_not()
    {
        using var box = new Sandbox();
        var (s, settings, pc) = Make(box);
        await Activate(s, null);
        pc.PlanFails = true;
        s.Shutdown();
        Assert.Equal("balanced", AppSettings.Load().Recovery.PowerPlan); // still on disk for next start
    }

    [Fact]
    public async Task Version_2_flags_become_a_recovery_record()
    {
        using var box = new Sandbox();
        var settings = new AppSettings { PendingRestorePlan = "balanced" };
        var pc = new FakePc { Plan = "ultimate" };
        var s = new GameModeService(settings, pc);
        s.RecoverAtStart();
        Assert.Equal("balanced", pc.Plan);
        Assert.Null(settings.PendingRestorePlan);
        await Task.CompletedTask;
    }
}
