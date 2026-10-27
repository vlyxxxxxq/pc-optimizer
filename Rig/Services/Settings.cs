using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Win32;

namespace Rig.Services;

/// <summary>Boosts for one game, replacing the Game Mode defaults while it runs.</summary>
public sealed class GameProfile
{
    public string Game { get; set; } = "";
    /// <summary>Install folder of a selected library game. Older/name-only profiles remain supported.</summary>
    public string? MatchPath { get; set; }
    public bool PowerPlan { get; set; } = true;
    public bool Priority { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool CloseApps { get; set; }
    /// <summary>Resolution and refresh rate while playing, e.g. "1920 × 1080 @ 360 Hz" (see DisplayModes).</summary>
    public string DisplayMode { get; set; } = DisplayModes.Unchanged;
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

/// <summary>User preferences, stored as JSON in %AppData%\Rig\settings.json.</summary>
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
    /// <summary>End apps that don't close when asked (unsaved work in them is lost). Off: they're left open.</summary>
    public bool ForceCloseApps { get; set; }
    public List<string> CustomGames { get; set; } = [];
    public List<string> AppsToClose { get; set; } = [];
    /// <summary>Game folders or exe names the user marked "Not a game".</summary>
    public List<string> IgnoredGames { get; set; } = [];
    /// <summary>Per-game preferences matched by installation path, with legacy name-only profiles retained.</summary>
    public List<GameProfile> GameProfiles { get; set; } = [];

    public GameProfile? ProfileFor(string? game, string? executable = null)
    {
        if (executable is { Length: > 0 })
        {
            executable = executable.Replace('/', '\\');
            var exact = GameProfiles.Where(p => !string.IsNullOrWhiteSpace(p.MatchPath))
                .Where(p => executable.StartsWith(p.MatchPath!.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    || executable.Equals(p.MatchPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.MatchPath!.Length).FirstOrDefault();
            if (exact is not null) return exact;
        }
        return game is null ? null : GameProfiles.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.MatchPath) && p.Game.Equals(game, StringComparison.OrdinalIgnoreCase));
    }

    public bool AutoCheckUpdates { get; set; } = true;

    // Scheduled cleaning
    public bool AutoClean { get; set; }
    public int AutoCleanDays { get; set; } = 7;
    public DateTime? LastAutoClean { get; set; }

    /// <summary>Catalog apps to tick on the Apps page after importing a backup (consumed on next start).</summary>
    public List<string> PendingInstallIds { get; set; } = [];

    /// <summary>Power plan to restore if the app exited while Game Mode was on.</summary>
    public string? PendingRestorePlan { get; set; }
    /// <summary>A game profile changed the resolution and the app exited before putting it back.</summary>
    public bool PendingDisplayRestore { get; set; }
    /// <summary>Game Mode changes still to undo (see GameModeRecovery); kept until each one is put back.</summary>
    public GameModeRecovery Recovery { get; set; } = new();

    /// <summary>Format of this file; newer files from a later Rig are read as far as possible and never downgraded silently.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;
    public const int CurrentSchema = 2;

    /// <summary>Set RIG_SETTINGS to use a different file (handy for testing).</summary>
    static string FilePath => Environment.GetEnvironmentVariable("RIG_SETTINGS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(DataFolder, "settings.json");

    /// <summary>%AppData%\Rig. On the first run after updating from 1.x, PC Optimizer's folder is copied here.</summary>
    public static string DataFolder
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "Rig");
            var old = Path.Combine(appData, "PCOptimizer");
            if (!Directory.Exists(folder) && Directory.Exists(old))
            {
                try { CopyFolder(old, folder); }
                catch { } // start fresh rather than not at all
            }
            return folder;
        }
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>A file next to settings.json, where every feature keeps its data (follows RIG_SETTINGS).</summary>
    public static string DataFile(string name) => Path.Combine(Path.GetDirectoryName(FilePath)!, name);

    /// <summary>Data files a full backup carries along with the settings.</summary>
    public static readonly string[] DataFiles = ["sessions.json", "screentime.json", "inputstats.json", "notes.txt", "benchmarks.json", "layouts.json", "timers.json", "capture-library.json"];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Set when settings.json couldn't be read: what happened and where the damaged copy was kept.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? LoadProblem { get; private set; }

    /// <summary>A save failed (disk full, no access…); the app says so once instead of pretending it worked.</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>
    /// Reads settings.json. A damaged file is kept beside it (settings.corrupt-*.json) and the last good copy
    /// (settings.json.bak, made by every save) is used instead; only if that fails too do the defaults apply.
    /// </summary>
    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return File.Exists(FilePath + ".bak") && TryRead(FilePath + ".bak") is { } fromBackup ? fromBackup : new() { IsFirstRun = true };
        if (TryRead(FilePath) is { } s) return s;

        string kept = FilePath.Replace(".json", $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try { File.Copy(FilePath, kept, overwrite: true); } catch { kept = "(couldn't keep a copy)"; }
        var good = TryRead(FilePath + ".bak") ?? new AppSettings();
        good.LoadProblem = $"Your settings file was damaged, so {(File.Exists(FilePath + ".bak") ? "the last good copy" : "the defaults")} were loaded. The damaged file was kept as {Path.GetFileName(kept)}.";
        return good;
    }

    static AppSettings? TryRead(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            s?.Normalize();
            return s;
        }
        catch { return null; }
    }

    static readonly string[] Themes = Services.Theme.Modes;

    /// <summary>
    /// Makes loaded or imported values safe: missing (null) lists and objects get their defaults, numbers are kept
    /// in range, and unknown themes, colours, enums and hotkeys fall back to defaults.
    /// </summary>
    public void Normalize()
    {
        var defaults = new AppSettings();
        foreach (var p in SettingsPolicy.Properties())
            if (!p.PropertyType.IsValueType && p.PropertyType != typeof(string) && p.GetValue(this) is null && p.GetValue(defaults) is { } d)
                p.SetValue(this, d);
        GameProfiles.RemoveAll(g => g is null);
        foreach (var g in GameProfiles) { g.Game ??= ""; g.DisplayMode ??= DisplayModes.Unchanged; }
        CrosshairGames.RemoveAll(g => g is null);
        Crosshair ??= new();
        if (!Enum.IsDefined(Crosshair.Shape)) Crosshair.Shape = CrosshairShape.CrossDot;
        Crosshair.Size = Math.Clamp(Crosshair.Size, 1, 60);
        Crosshair.Thickness = Math.Clamp(Crosshair.Thickness, 1, 12);
        Crosshair.Gap = Math.Clamp(Crosshair.Gap, 0, 40);
        Crosshair.Opacity = Math.Clamp(Crosshair.Opacity, 0.1, 1);
        if (!IsColor(Crosshair.Color)) Crosshair.Color = defaults.Crosshair.Color;
        if (!Themes.Contains(Theme)) Theme = defaults.Theme;
        if (Accent is not null && !IsColor(Accent)) Accent = null;
        if (!Enum.IsDefined(MaintenanceDay)) MaintenanceDay = DayOfWeek.Sunday;
        MaintenanceHour = Math.Clamp(MaintenanceHour, 0, 23);
        AutoCleanDays = Math.Clamp(AutoCleanDays, 1, 90);
        BreakMinutes = Math.Clamp(BreakMinutes, 15, 240);
        OverlayCorner = Math.Clamp(OverlayCorner, 0, 3);
        Shortcuts.Normalize(this);
        ReplaySeconds = Math.Clamp(ReplaySeconds, 15, 120);
        CaptureFps = CaptureFps == 30 ? 30 : 60;
        CaptureHeight = new[] { 720, 1080, 1440, 2160 }.Contains(CaptureHeight) ? CaptureHeight : 1080;
        CaptureMbps = Math.Clamp(CaptureMbps, 4, 80);
        CaptureSystemVolume = double.IsFinite(CaptureSystemVolume) ? Math.Clamp(CaptureSystemVolume, 0, 1) : 1;
        CaptureMicVolume = double.IsFinite(CaptureMicVolume) ? Math.Clamp(CaptureMicVolume, 0, 2) : 1;
        if (!string.IsNullOrWhiteSpace(CaptureFolder) && !Path.IsPathFullyQualified(CaptureFolder)) CaptureFolder = null;
        CaptureFolders = CaptureFolders.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DisabledModules = DisabledModules.Where(id => id is not null && Rig.Modules.All.Any(m => m.Id == id && !m.Core)).Distinct().ToList();
        HomeCards = HomeCards.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        foreach (var list in new[] { CustomGames, AppsToClose, IgnoredGames, PinnedPages, CollapsedCategories, ColorHistory, PendingInstallIds })
            list.RemoveAll(string.IsNullOrWhiteSpace);
        ColorHistory = ColorHistory.Where(IsColor).Take(24).ToList();
        Recovery ??= new();
        Recovery.Normalize();
    }

    static bool IsColor(string? s) => s is not null && Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");

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
    public string RecordHotkey { get; set; } = "Ctrl+Shift+F11";
    public string ReplayHotkey { get; set; } = "Ctrl+Shift+F12";
    public string PaletteHotkey { get; set; } = "Ctrl+Alt+P";
    public string ClipboardHotkey { get; set; } = "Ctrl+Alt+V";
    public bool RecordGif { get; set; }
    public bool RecordCursor { get; set; } = true;
    public int ReplaySeconds { get; set; } = 30;
    public int CaptureFps { get; set; } = 60;
    public int CaptureHeight { get; set; } = 1080;
    public int CaptureMbps { get; set; } = 16;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }
    public double CaptureSystemVolume { get; set; } = 1;
    public double CaptureMicVolume { get; set; } = 1;
    public string? CaptureOutputDevice { get; set; }
    public string? CaptureMicDevice { get; set; }
    public string? CaptureFolder { get; set; }
    public List<string> CaptureFolders { get; set; } = [];
    public int CaptureMonitor { get; set; } = -1;
    public bool ReplayWithGameMode { get; set; }
    public bool InputStatsEnabled { get; set; }
    public bool LayoutsLaunchApps { get; set; }
    /// <summary>Appearance options applied immediately through shared theme resources.</summary>
    public bool ThemeGlow { get; set; } = true;
    public bool RoundCards { get; set; } = true;
    public string ColorHotkey { get; set; } = "Ctrl+Shift+F7";
    /// <summary>Picked colours as #RRGGBB, newest first.</summary>
    public List<string> ColorHistory { get; set; } = [];
    /// <summary>Modules switched off in Settings → Modules (by id, see Modules.All).</summary>
    public List<string> DisabledModules { get; set; } = [];
    public bool SidebarCollapsed { get; set; }
    /// <summary>"Dark" or "Light"; applied at start.</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>Accent colour as #RRGGBB (see Theme.Accents).</summary>
    public string? Accent { get; set; }
    public List<string> CollapsedCategories { get; set; } = [];
    public bool ShowNowPlaying { get; set; } = true;
    /// <summary>Sidebar pages pinned to the Dashboard, by their nav button name.</summary>
    /// <summary>Cards on Home, in order (see DashboardView.Catalog).</summary>
    public List<string> HomeCards { get; set; } = [.. HomeLayout.Default];
    public List<string> PinnedPages { get; set; } = ["NavGame", "NavClips", "NavCleaner", "NavDisk"];
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

    static readonly object WriteLock = new();
    static bool _reportedFailure;

    /// <summary>
    /// Writes settings.json atomically (a temp file replaces it, and the previous file becomes settings.json.bak), one
    /// writer at a time across processes. Fields the scheduled maintenance writes from its own process are merged so
    /// this copy never overwrites them with older values. Returns false (and raises SaveFailed once) if it couldn't.
    /// </summary>
    public bool Save()
    {
        lock (WriteLock)
        {
            using var mutex = new Mutex(false, @"Local\Rig.Settings." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(FilePath.ToLowerInvariant())))[..12]);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { owned = true; }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && TryRead(FilePath) is { } onDisk) MergeNewer(onDisk);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, FilePath);
                _reportedFailure = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!_reportedFailure) { _reportedFailure = true; SaveFailed?.Invoke($"Rig couldn't save your settings ({ex.Message}). Changes may be lost when it closes."); }
                return false;
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
    }

    /// <summary>Maintenance runs as its own process; its results on disk win if they're newer than this copy's.</summary>
    void MergeNewer(AppSettings onDisk)
    {
        static bool Newer(DateTime? disk, DateTime? mine) => disk is { } d && (mine is null || d > mine);
        if (Newer(onDisk.LastMaintenance, LastMaintenance)) { LastMaintenance = onDisk.LastMaintenance; LastMaintenanceSummary = onDisk.LastMaintenanceSummary; }
        if (Newer(onDisk.LastAutoClean, LastAutoClean)) LastAutoClean = onDisk.LastAutoClean;
    }

    /// <summary>Reads the newest settings, changes them and saves, for processes that mustn't overwrite the app's choices.</summary>
    public static bool Update(Action<AppSettings> change)
    {
        var fresh = Load();
        if (fresh.LoadProblem is not null) return false; // never write over a file we couldn't read
        change(fresh);
        return fresh.Save();
    }
}

/// <summary>Launches the app in the tray at sign-in via the per-user Run key.</summary>
public static class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Rig";

    public static bool IsEnabled
    {
        get
        {
            using var key = Places.OpenUserKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled, string? exe = null)
    {
        using var key = Places.OpenUserKey(RunKey, write: true)!;
        if (enabled) key.SetValue(ValueName, $"\"{exe ?? Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
