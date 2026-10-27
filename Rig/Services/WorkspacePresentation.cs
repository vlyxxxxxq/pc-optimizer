namespace Rig.Services;

public enum StatusTone { Neutral, Active, Attention }

/// <summary>Shared wording and available actions for Home and Game Mode, including transitional states.</summary>
public sealed record GameModeSummary(string Title, string Detail, string Badge, string Action, StatusTone Tone, bool Busy)
{
    public static GameModeSummary Create(GameModeState state, bool automatic, string? game = null) => state switch
    {
        GameModeState.Starting => new("Getting your session ready", "Applying your Game Mode preferences…", "Starting", "Starting…", StatusTone.Active, true),
        GameModeState.Restoring => new("Putting things back", "Restoring the changes from this session…", "Restoring", "Restoring…", StatusTone.Active, true),
        GameModeState.RecoveryNeeded => new("A few changes need attention", "Open Game Mode to review what still needs to be restored.", "Recovery needed", "Review recovery", StatusTone.Attention, false),
        GameModeState.On => new(game is { Length: > 0 } ? game : "Your session is ready", "Your chosen settings are active. Turn Game Mode off to restore them.", "Game Mode on", "Turn off", StatusTone.Active, false),
        _ => automatic
            ? new("Ready for your next game", "Automatic detection is on. Your preferences apply when a supported game is in focus.", "Automatic", "Turn on now", StatusTone.Neutral, false)
            : new("Your game. Your settings.", "Turn Game Mode on when you need it, or enable automatic detection on its page.", "Manual", "Turn on", StatusTone.Neutral, false),
    };
}

public sealed record MaintenanceOverview(string Title, string Detail, int Issues, int Unknown, bool Attention)
{
    public static MaintenanceOverview From(HealthReport report)
    {
        int issues = report.Findings.Count(f => f.Severity is Severity.Warn or Severity.Bad);
        int unknown = report.Findings.Count(f => f.Severity == Severity.Unknown);
        var title = issues > 0 ? $"{issues} {(issues == 1 ? "item" : "items")} to review"
            : unknown > 0 ? "Some checks couldn't finish" : report.Findings.Count == 0 ? "No checks available" : "Nothing needs your attention";
        var detail = $"Checked {report.Checked:t}" + (unknown > 0 ? $" · {unknown} {(unknown == 1 ? "check" : "checks")} unavailable" : "")
            + ". Suggestions to review, not a performance score.";
        return new(title, detail, issues, unknown, issues > 0 || unknown > 0 || report.Findings.Count == 0);
    }
}

/// <summary>Only the old factory layout is upgraded; intentionally customized card orders are kept.</summary>
public static class HomeLayout
{
    public static readonly string[] Default = ["clips", "gamemode", "cpu", "memory", "system", "pins", "health", "recent", "storage"];
    public static readonly string[] PreviousDefault = ["gamemode", "cpu", "memory", "system", "pins", "health", "recent", "storage"];
    public static readonly string[] LegacyDefault = ["pins", "health", "cpu", "memory", "system", "gamemode", "music", "screentime", "storage"];
    public static List<string> Upgrade(IReadOnlyList<string> cards) => cards.SequenceEqual(LegacyDefault) || cards.SequenceEqual(PreviousDefault) ? [.. Default] : [.. cards];

    public static List<string> Reorder(IReadOnlyList<string> cards, string source, string target, bool after)
    {
        var result = cards.ToList();
        if (source == target || !result.Contains(source) || !result.Contains(target)) return result;
        result.Remove(source);
        result.Insert(result.IndexOf(target) + (after ? 1 : 0), source);
        return result;
    }

    public static string? ModuleForAction(string action) => action switch
    {
        "clean" => "Cleaner", "startup" => "Startup", "disk" => "Disk", "processes" => "Processes", "drivers" => "Tools", _ => null,
    };
}

/// <summary>Recent navigation only, in memory for this session. No system activity or history is collected.</summary>
public sealed class RecentTools
{
    readonly List<string> _ids = [];
    public void Visit(string id)
    {
        if (id is Modules.Home or Modules.Settings || !Modules.All.Any(m => m.Id == id)) return;
        _ids.Remove(id);
        _ids.Insert(0, id);
        if (_ids.Count > 4) _ids.RemoveAt(4);
    }
    public IReadOnlyList<ModuleInfo> Available(Func<string, bool> enabled) =>
        _ids.Where(enabled).Select(Modules.Get).ToList();
}
