using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer.Services;

/// <summary>Boosts for one game, replacing the Game Mode defaults while it runs.</summary>
public sealed class GameProfile
{
    public string Game { get; set; } = "";
    public bool PowerPlan { get; set; } = true;
    public bool Priority { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool CloseApps { get; set; }
}

public enum CrosshairShape { Cross, CrossDot, Dot, Circle, T }

public sealed class CrosshairStyle
{
    public CrosshairShape Shape { get; set; } = CrosshairShape.CrossDot;
    public string Color { get; set; } = "#00FF66";
    public double Size { get; set; } = 7;       // arm length (circle radius)
    public double Thickness { get; set; } = 2;
    public double Gap { get; set; } = 4;
    public bool Outline { get; set; } = true;
    public double Opacity { get; set; } = 1;
}

/// <summary>A game the crosshair turns on for: matched by install folder, or by exe name for games added by hand.</summary>
public sealed class CrosshairGame
{
    public string Name { get; set; } = "";
    public string? Folder { get; set; }
    public string? Exe { get; set; }
}

/// <summary>User preferences, stored as JSON in %AppData%\PCOptimizer\settings.json.</summary>
public sealed class AppSettings
{
    // General
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool TrayHintShown { get; set; }

    // Game Mode
    public bool AutoGameMode { get; set; } = true;
    public bool DetectFullscreen { get; set; }
    public bool BoostPowerPlan { get; set; } = true;
    public bool BoostPriority { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool CloseApps { get; set; }
    public bool ReopenApps { get; set; } = true;
    public List<string> CustomGames { get; set; } = [];
    public List<string> AppsToClose { get; set; } = [];
    /// <summary>Game folders or exe names the user marked "Not a game".</summary>
    public List<string> IgnoredGames { get; set; } = [];
    /// <summary>Per-game boosts, keyed by the game's display name. Games without one use the defaults above.</summary>
    public List<GameProfile> GameProfiles { get; set; } = [];

    public GameProfile? ProfileFor(string? game) =>
        game is null ? null : GameProfiles.FirstOrDefault(p => p.Game.Equals(game, StringComparison.OrdinalIgnoreCase));

    public bool AutoCheckUpdates { get; set; } = true;

    // Scheduled cleaning
    public bool AutoClean { get; set; }
    public int AutoCleanDays { get; set; } = 7;
    public DateTime? LastAutoClean { get; set; }

    /// <summary>Catalog apps to tick on the Apps page after importing a backup (consumed on next start).</summary>
    public List<string> PendingInstallIds { get; set; } = [];

    /// <summary>Copies the shareable preferences from a backup (not machine state like pending restores).</summary>
    public void CopyFrom(AppSettings o)
    {
        CloseToTray = o.CloseToTray; Notifications = o.Notifications;
        AutoGameMode = o.AutoGameMode; DetectFullscreen = o.DetectFullscreen;
        BoostPowerPlan = o.BoostPowerPlan; BoostPriority = o.BoostPriority; KeepAwake = o.KeepAwake;
        CloseApps = o.CloseApps; ReopenApps = o.ReopenApps;
        CustomGames = o.CustomGames.ToList(); AppsToClose = o.AppsToClose.ToList();
        IgnoredGames = o.IgnoredGames.ToList(); GameProfiles = o.GameProfiles.ToList();
        AutoCheckUpdates = o.AutoCheckUpdates; AutoClean = o.AutoClean; AutoCleanDays = o.AutoCleanDays;
    }

    /// <summary>Power plan to restore if the app exited while Game Mode was on.</summary>
    public string? PendingRestorePlan { get; set; }

    /// <summary>Set PCOPTIMIZER_SETTINGS to use a different file (handy for testing).</summary>
    static string FilePath => Environment.GetEnvironmentVariable("PCOPTIMIZER_SETTINGS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCOptimizer", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new() { IsFirstRun = true };
    }

    /// <summary>No settings file existed yet: show the welcome. Not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFirstRun { get; private set; }

    // Gaming overlay
    public bool OverlayWithGameMode { get; set; }
    public int OverlayCorner { get; set; } // 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right

    // Library, clips, clipboard, screen time
    public bool LaunchWithGameMode { get; set; } = true;
    public bool FreeGameAlerts { get; set; } = true;
    public bool SessionRecap { get; set; } = true;
    public bool DownloadsAutoOrganize { get; set; }

    // Crosshair
    public CrosshairStyle Crosshair { get; set; } = new();
    public List<CrosshairGame> CrosshairGames { get; set; } = [];
    public string CrosshairHotkey { get; set; } = "Ctrl+Shift+F8";
    /// <summary>Giveaway ids already announced, so each free game is only mentioned once.</summary>
    public List<int> SeenGiveaways { get; set; } = [];
    public bool CaptureHotkeys { get; set; } = true;
    public string ClipHotkey { get; set; } = "Ctrl+Shift+F9";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+F10";
    public bool ClipboardHistoryEnabled { get; set; } = true;
    public bool ScreenTimeEnabled { get; set; } = true;
    public bool BreakReminders { get; set; }
    public int BreakMinutes { get; set; } = 60;

    // Maintenance night
    public bool MaintenanceEnabled { get; set; }
    public DayOfWeek MaintenanceDay { get; set; } = DayOfWeek.Sunday;
    public int MaintenanceHour { get; set; } = 3;
    public bool MaintenanceInstallUpdates { get; set; }
    public DateTime? LastMaintenance { get; set; }
    public string? LastMaintenanceSummary { get; set; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}

/// <summary>Launches the app in the tray at sign-in via the per-user Run key.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PCOptimizer";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled, string? exe = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exe ?? Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
