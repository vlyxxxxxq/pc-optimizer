using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Rig.Services;

/// <summary>A display mode: resolution and refresh rate.</summary>
public sealed record DisplayMode(int Width, int Height, int Hz)
{
    public override string ToString() => DisplayModes.Format(Width, Height, Hz);
}

/// <summary>
/// Everything Game Mode changes on the PC, written down before each change and removed only once that change has
/// been put back. It lives in settings.json, so a crash, power cut or failed restore is finished on the next start.
/// </summary>
public sealed class GameModeRecovery
{
    /// <summary>Plan to go back to, and the plan Game Mode switched to (to notice if you changed it yourself since).</summary>
    public string? PowerPlan { get; set; }
    public string? PowerPlanName { get; set; }
    public string? PowerPlanSet { get; set; }

    /// <summary>The mode to go back to (null: the mode saved in Windows' display settings), and the one Game Mode set.</summary>
    public DisplayMode? DisplayOriginal { get; set; }
    public DisplayMode? DisplaySet { get; set; }
    public bool DisplayChanged { get; set; }

    /// <summary>The game whose priority was raised: identified by PID and start time, so a reused PID is never touched.</summary>
    public int PriorityPid { get; set; }
    public DateTime PriorityStart { get; set; }
    public ProcessPriorityClass PriorityOriginal { get; set; } = ProcessPriorityClass.Normal;
    public ProcessPriorityClass PrioritySet { get; set; }

    /// <summary>What couldn't be put back yet, in words.</summary>
    public List<string> Problems { get; set; } = [];

    [JsonIgnore] public bool Pending => PowerPlan is not null || DisplayChanged || PriorityPid != 0;

    public void Normalize()
    {
        Problems ??= [];
        if (!Enum.IsDefined(PriorityOriginal)) PriorityOriginal = ProcessPriorityClass.Normal;
    }
}

/// <summary>The Windows calls Game Mode makes, so its logic can be tested with a fake PC.</summary>
public interface IGameModeSystem
{
    string? ActivePlan();
    /// <summary>Ultimate Performance if present, else High performance; null if neither exists.</summary>
    (string Guid, string Name)? BestPlan();
    string? PlanName(string guid);
    bool SetPlan(string guid);

    DisplayMode? CurrentDisplay();
    bool SetDisplay(DisplayMode mode);
    /// <summary>Back to the given mode, or to Windows' saved mode when null.</summary>
    bool RestoreDisplay(DisplayMode? mode);

    /// <summary>Start time and priority of a running process, or null if it's gone.</summary>
    (DateTime Start, ProcessPriorityClass Priority)? Process(int pid);
    bool SetPriority(int pid, ProcessPriorityClass priority);

    void KeepAwake(bool on);

    /// <summary>Asks every process with this name to close. force: end any that don't (unsaved work is lost).</summary>
    (bool Closed, string? Path) CloseApp(string name, bool force);
}

/// <summary>The real PC.</summary>
public sealed partial class WindowsGameModeSystem : IGameModeSystem
{
    const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s+\((.+?)\)(\s*\*)?")]
    private static partial Regex SchemeLine();

    static (int Code, string Output) Run(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new ProcessStartInfo("powercfg", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, output.Result);
        }
        catch { return (-1, ""); }
    }

    List<(string Guid, string Name, bool Active)> Plans() =>
        SchemeLine().Matches(Run("/list").Output).Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success)).ToList();

    public string? ActivePlan() => SchemeLine().Match(Run("/getactivescheme").Output) is { Success: true } m ? m.Groups[1].Value : null;

    public (string Guid, string Name)? BestPlan()
    {
        var plans = Plans();
        var best = plans.FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        if (best.Guid is null) best = plans.FirstOrDefault(p => p.Guid.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase));
        return best.Guid is null ? null : (best.Guid, best.Name);
    }

    public string? PlanName(string guid) => Plans().FirstOrDefault(p => p.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)).Name;

    public bool SetPlan(string guid) => Run($"/setactive {guid}").Code == 0;

    public DisplayMode? CurrentDisplay() => DisplayModes.Current() is { } c ? new DisplayMode(c.W, c.H, c.Hz) : null;
    public bool SetDisplay(DisplayMode m) => DisplayModes.Apply(m.Width, m.Height, m.Hz);
    public bool RestoreDisplay(DisplayMode? m) => DisplayModes.RestoreTo(m is null ? null : (m.Width, m.Height, m.Hz));

    public (DateTime Start, ProcessPriorityClass Priority)? Process(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.HasExited ? null : (p.StartTime, p.PriorityClass);
        }
        catch { return null; }
    }

    public bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.PriorityClass = priority; return true; }
        catch { return false; }
    }

    public void KeepAwake(bool on) =>
        Native.SetThreadExecutionState(on ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED : Native.ES_CONTINUOUS);

    public (bool Closed, string? Path) CloseApp(string name, bool force)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(name);
        if (procs.Length == 0) return (false, null);
        string? path = null;
        bool all = true;
        foreach (var p in procs)
        {
            using (p)
            {
                try
                {
                    path ??= Native.ProcessPath(p.Id);
                    // Ask nicely and give it time to save; only end it if that's been explicitly allowed.
                    bool asked = p.CloseMainWindow();
                    if (asked && p.WaitForExit(10_000)) continue;
                    if (force) { p.Kill(); p.WaitForExit(3000); }
                    else all = false;
                }
                catch { all = false; }
            }
        }
        return (all, path);
    }
}
