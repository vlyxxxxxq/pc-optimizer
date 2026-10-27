using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Services;

/// <summary>How a setting travels in a backup or a shared setup.</summary>
public enum Transfer
{
    /// <summary>A preference: copied on import.</summary>
    Share,
    /// <summary>Turns tracking on (clipboard history, screen time…): an import can switch it off, but only switches it
    /// on when the person importing says so.</summary>
    Privacy,
    /// <summary>Personal history (picked colours): only with "include personal data".</summary>
    Personal,
    /// <summary>About this PC or this install (recovery state, schedules, what's been seen): never imported.</summary>
    Local,
}

/// <summary>What the person importing chose in the preview.</summary>
public sealed record ImportChoices(bool Preferences = true, bool AllowTrackingOn = false, bool PersonalData = false, bool Tweaks = true, bool Apps = true);

/// <summary>
/// The contract for every setting: whether it transfers and how. Every property of AppSettings must be listed here
/// (a test fails otherwise), so a new setting can't silently be shared, or silently left out.
/// </summary>
public static class SettingsPolicy
{
    public static readonly Dictionary<string, Transfer> Fields = new()
    {
        // General
        [nameof(AppSettings.CloseToTray)] = Transfer.Share,
        [nameof(AppSettings.Notifications)] = Transfer.Share,
        [nameof(AppSettings.TrayHintShown)] = Transfer.Local,
        [nameof(AppSettings.Theme)] = Transfer.Share,
        [nameof(AppSettings.Accent)] = Transfer.Share,
        [nameof(AppSettings.SidebarCollapsed)] = Transfer.Share,
        [nameof(AppSettings.CollapsedCategories)] = Transfer.Share,
        [nameof(AppSettings.DisabledModules)] = Transfer.Share,
        [nameof(AppSettings.HomeCards)] = Transfer.Share,
        [nameof(AppSettings.PinnedPages)] = Transfer.Share,
        [nameof(AppSettings.AutoCheckUpdates)] = Transfer.Share,
        [nameof(AppSettings.BetaUpdates)] = Transfer.Share,
        // Game Mode
        [nameof(AppSettings.AutoGameMode)] = Transfer.Share,
        [nameof(AppSettings.DetectFullscreen)] = Transfer.Share,
        [nameof(AppSettings.BoostPowerPlan)] = Transfer.Share,
        [nameof(AppSettings.BoostPriority)] = Transfer.Share,
        [nameof(AppSettings.KeepAwake)] = Transfer.Share,
        [nameof(AppSettings.CloseApps)] = Transfer.Share,
        [nameof(AppSettings.ReopenApps)] = Transfer.Share,
        [nameof(AppSettings.ForceCloseApps)] = Transfer.Share,
        [nameof(AppSettings.CustomGames)] = Transfer.Share,
        [nameof(AppSettings.AppsToClose)] = Transfer.Share,
        [nameof(AppSettings.IgnoredGames)] = Transfer.Share,
        [nameof(AppSettings.GameProfiles)] = Transfer.Share,
        [nameof(AppSettings.OverlayWithGameMode)] = Transfer.Share,
        [nameof(AppSettings.OverlayCorner)] = Transfer.Share,
        [nameof(AppSettings.SessionRecap)] = Transfer.Share,
        [nameof(AppSettings.PendingRestorePlan)] = Transfer.Local,
        [nameof(AppSettings.PendingDisplayRestore)] = Transfer.Local,
        [nameof(AppSettings.Recovery)] = Transfer.Local,
        // Cleaning and maintenance (schedules belong to this PC: importing doesn't create scheduled tasks)
        [nameof(AppSettings.AutoClean)] = Transfer.Share,
        [nameof(AppSettings.AutoCleanDays)] = Transfer.Share,
        [nameof(AppSettings.LastAutoClean)] = Transfer.Local,
        [nameof(AppSettings.MaintenanceEnabled)] = Transfer.Local,
        [nameof(AppSettings.MaintenanceDay)] = Transfer.Share,
        [nameof(AppSettings.MaintenanceHour)] = Transfer.Share,
        [nameof(AppSettings.MaintenanceInstallUpdates)] = Transfer.Share,
        [nameof(AppSettings.LastMaintenance)] = Transfer.Local,
        [nameof(AppSettings.LastMaintenanceSummary)] = Transfer.Local,
        [nameof(AppSettings.PendingInstallIds)] = Transfer.Local,
        // Library, captures, hotkeys
        [nameof(AppSettings.LaunchWithGameMode)] = Transfer.Share,
        [nameof(AppSettings.FreeGameAlerts)] = Transfer.Share,
        [nameof(AppSettings.SeenGiveaways)] = Transfer.Local,
        [nameof(AppSettings.DownloadsAutoOrganize)] = Transfer.Share,
        [nameof(AppSettings.Crosshair)] = Transfer.Share,
        [nameof(AppSettings.CrosshairGames)] = Transfer.Share,
        [nameof(AppSettings.CrosshairHotkey)] = Transfer.Share,
        [nameof(AppSettings.CaptureHotkeys)] = Transfer.Share,
        [nameof(AppSettings.ClipHotkey)] = Transfer.Share,
        [nameof(AppSettings.ScreenshotHotkey)] = Transfer.Share,
        [nameof(AppSettings.RecordHotkey)] = Transfer.Share,
        [nameof(AppSettings.RecordGif)] = Transfer.Share,
        [nameof(AppSettings.RecordCursor)] = Transfer.Share,
        [nameof(AppSettings.ColorHotkey)] = Transfer.Share,
        [nameof(AppSettings.ColorHistory)] = Transfer.Personal,
        [nameof(AppSettings.LayoutsLaunchApps)] = Transfer.Share,
        // Tracking: never switched on by somebody else's setup
        [nameof(AppSettings.ClipboardHistoryEnabled)] = Transfer.Privacy,
        [nameof(AppSettings.ScreenTimeEnabled)] = Transfer.Privacy,
        [nameof(AppSettings.InputStatsEnabled)] = Transfer.Privacy,
        [nameof(AppSettings.ShowNowPlaying)] = Transfer.Privacy,
        [nameof(AppSettings.BreakReminders)] = Transfer.Share,
        [nameof(AppSettings.BreakMinutes)] = Transfer.Share,
        // The format version itself
        [nameof(AppSettings.SchemaVersion)] = Transfer.Local,
    };

    /// <summary>Persisted settings properties (everything serialized).</summary>
    public static IEnumerable<PropertyInfo> Properties() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null);

    /// <summary>Applies an incoming setup to these settings, field by field, as the policy and the choices allow.</summary>
    public static List<string> Apply(AppSettings incoming, AppSettings target, ImportChoices choices)
    {
        var notes = new List<string>();
        foreach (var p in Properties())
        {
            if (!Fields.TryGetValue(p.Name, out var rule)) continue; // unknown to the policy: never copied
            switch (rule)
            {
                case Transfer.Share when choices.Preferences:
                    p.SetValue(target, Clone(p.GetValue(incoming), p.PropertyType));
                    break;
                case Transfer.Personal when choices.PersonalData:
                    p.SetValue(target, Clone(p.GetValue(incoming), p.PropertyType));
                    break;
                case Transfer.Privacy when choices.Preferences:
                    bool now = (bool)p.GetValue(target)!, wanted = (bool)p.GetValue(incoming)!;
                    if (!wanted) p.SetValue(target, false);
                    else if (!now && choices.AllowTrackingOn) p.SetValue(target, true);
                    else if (!now) notes.Add($"{Describe(p.Name)} stays off (it was on in the setup).");
                    break;
            }
        }
        target.Normalize();
        return notes;
    }

    public static string Describe(string field) => field switch
    {
        nameof(AppSettings.ClipboardHistoryEnabled) => "Clipboard history",
        nameof(AppSettings.ScreenTimeEnabled) => "Screen time tracking",
        nameof(AppSettings.InputStatsEnabled) => "Input counting",
        nameof(AppSettings.ShowNowPlaying) => "Now playing",
        _ => Regex.Replace(field, "(?<=[a-z])(?=[A-Z])", " "),
    };

    /// <summary>Tracking switches the incoming setup has on and these settings have off.</summary>
    public static List<string> TrackingItWouldTurnOn(AppSettings incoming, AppSettings target) =>
        Properties().Where(p => Fields.GetValueOrDefault(p.Name) == Transfer.Privacy && (bool)p.GetValue(incoming)! && !(bool)p.GetValue(target)!)
            .Select(p => Describe(p.Name)).ToList();

    static object? Clone(object? value, Type type) =>
        value is null || type.IsValueType || type == typeof(string) ? value : JsonSerializer.Deserialize(JsonSerializer.Serialize(value, type), type);
}
