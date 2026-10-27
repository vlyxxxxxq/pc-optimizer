using System.Globalization;
using Microsoft.Win32;

namespace Rig.Services;

/// <summary>
/// Pauses Windows Update with the same registry values Settings → Windows Update → "Pause updates" writes,
/// so Windows itself shows the pause and can resume it. Needs administrator rights. Max 5 weeks.
/// </summary>
public static class UpdatePause
{
    const string Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    static readonly string[] StartNames = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    static readonly string[] EndNames = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    /// <summary>When updates resume, or null if they aren't paused.</summary>
    public static DateTime? PausedUntil()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key);
        return k?.GetValue("PauseUpdatesExpiryTime") is string s &&
               DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until) &&
               until > DateTime.UtcNow
            ? until.ToLocalTime()
            : null;
    }

    public static void Pause(int weeks)
    {
        var now = DateTime.UtcNow;
        var end = now.AddDays(Math.Clamp(weeks, 1, 5) * 7);
        using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        foreach (var n in StartNames) k.SetValue(n, Iso(now), RegistryValueKind.String);
        foreach (var n in EndNames) k.SetValue(n, Iso(end), RegistryValueKind.String);
    }

    public static void Resume()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key, writable: true);
        if (k is null) return;
        foreach (var n in StartNames.Concat(EndNames)) k.DeleteValue(n, throwOnMissingValue: false);
    }

    static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
