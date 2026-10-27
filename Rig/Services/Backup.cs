using System.Text.Json;

namespace Rig.Services;

/// <summary>A shareable snapshot of how someone has Rig set up.</summary>
public sealed class BackupFile
{
    /// <summary>1: PC Optimizer / Rig 2.0 (settings copied wholesale). 2: settings follow SettingsPolicy.</summary>
    public int Format { get; set; } = Backup.CurrentFormat;
    public string Version { get; set; } = "";
    public DateTime Created { get; set; }
    public AppSettings Settings { get; set; } = new();
    /// <summary>Names of the tweaks that were switched on.</summary>
    public List<string> Tweaks { get; set; } = [];
    /// <summary>winget ids of catalog apps that were installed.</summary>
    public List<string> Apps { get; set; } = [];
    /// <summary>Data files (play history, screen time, notes…) by name, when the backup includes personal data.</summary>
    public Dictionary<string, string> Data { get; set; } = [];
}

/// <summary>What an import did, for an honest report.</summary>
public sealed record ImportResult(List<string> Notes, List<string> SkippedTweaks, List<string> DataRestored, List<string> DataFailed, string? SafetyCopy, bool SettingsSaved);

public static class Backup
{
    public const int CurrentFormat = 2;
    public const long MaxSize = 20 * 1024 * 1024;
    public const string Filter = "Rig setup (*.pcopt)|*.pcopt";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The file contents; personal data only when asked (a setup shared with friends stays settings-only).</summary>
    public static BackupFile Create(AppSettings settings, IEnumerable<string> tweaksOn, IEnumerable<string> apps, bool includeData)
    {
        // Only what the policy allows to travel: never this PC's recovery records or schedules.
        var portable = new AppSettings();
        SettingsPolicy.Apply(settings, portable, new ImportChoices(Preferences: true, AllowTrackingOn: true, PersonalData: includeData));
        var file = new BackupFile
        {
            Version = Updater.CurrentVersion.ToString(), Created = DateTime.Now, Settings = portable,
            Tweaks = tweaksOn.ToList(), Apps = apps.ToList(),
        };
        if (includeData)
            foreach (var name in AppSettings.DataFiles)
                if (File.Exists(AppSettings.DataFile(name)))
                    try { file.Data[name] = File.ReadAllText(AppSettings.DataFile(name)); } catch { }
        return file;
    }

    public static async Task ExportAsync(string path, AppSettings settings, IProgress<string> progress, bool includeData = false)
    {
        progress.Report("Reading tweaks…");
        var tweaks = await Task.Run(() =>
        {
            var all = Services.Tweaks.All().SelectMany(g => g.Tweaks).ToList();
            foreach (var t in all) t.Refresh();
            return all.Where(t => t.IsOn).Select(t => t.Name).ToList();
        });

        progress.Report("Checking installed apps (this takes a few seconds)…");
        var apps = new List<string>();
        try
        {
            var inventory = await Winget.InventoryAsync();
            apps = Winget.Catalog().SelectMany(g => g.Packages).Where(p => Winget.IsInstalled(p, inventory)).Select(p => p.Id).ToList();
        }
        catch { /* winget missing: share settings and tweaks only */ }

        await Write(path, Create(settings, tweaks, apps, includeData));
    }

    static async Task Write(string path, BackupFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads and checks a setup file; throws with a message a person can act on.</summary>
    public static BackupFile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSize) throw new InvalidDataException($"That file is {Fmt.Bytes(info.Length)}; setup files are never that big, so it wasn't opened.");
        BackupFile? b;
        try { b = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path)); }
        catch (JsonException) { throw new InvalidDataException("That isn't a Rig setup file (or it's damaged)."); }
        if (b?.Settings is null) throw new InvalidDataException("That isn't a Rig setup file.");
        if (b.Format > CurrentFormat) throw new InvalidDataException($"That setup was made by a newer Rig ({b.Version}). Update Rig, then import it.");
        b.Tweaks ??= []; b.Apps ??= []; b.Data ??= [];
        b.Settings.Normalize();
        // Only known data files, never paths taken from the file.
        b.Data = b.Data.Where(kv => AppSettings.DataFiles.Contains(kv.Key) && kv.Value is not null).ToDictionary();
        return b;
    }

    /// <summary>
    /// Applies the parts the person chose. Before anything changes, the current setup (with personal data) is saved
    /// to Rig's backups folder, so the import can be undone by importing that file.
    /// </summary>
    public static ImportResult Apply(BackupFile backup, AppSettings settings, ImportChoices choices, bool applyTweaks = true)
    {
        string? safety = null;
        try
        {
            safety = AppSettings.DataFile(Path.Combine("backups", $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.pcopt"));
            Write(safety, Create(settings, [], [], includeData: true)).GetAwaiter().GetResult();
        }
        catch { safety = null; }

        var notes = SettingsPolicy.Apply(backup.Settings, settings, choices);
        if (choices.Apps)
        {
            var catalog = Winget.Catalog().SelectMany(g => g.Packages).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            settings.PendingInstallIds = backup.Apps.Where(catalog.Contains).ToList(); // only known, vetted packages
        }
        bool saved = settings.Save();

        var restored = new List<string>();
        var failed = new List<string>();
        if (choices.PersonalData)
            foreach (var (name, content) in backup.Data)
                try { File.WriteAllText(AppSettings.DataFile(name), content); restored.Add(name); }
                catch { failed.Add(name); }

        var skipped = new List<string>();
        if (choices.Tweaks && applyTweaks)
        {
            var tweaks = Services.Tweaks.All().SelectMany(g => g.Tweaks).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in backup.Tweaks)
            {
                if (!tweaks.TryGetValue(name, out var t)) continue;
                t.Refresh();
                if (t.IsOn) continue;
                if (!t.CanChange) { skipped.Add(name); continue; }
                t.IsOn = true;
                if (!t.IsOn) skipped.Add(name);
            }
        }
        return new ImportResult(notes, skipped, restored, failed, safety, saved);
    }

    /// <summary>Plain-English summary for the import preview.</summary>
    public static string Describe(BackupFile b)
    {
        var s = b.Settings;
        var lines = new List<string>
        {
            $"From {(b.Format < 2 ? "PC Optimizer / Rig" : "Rig")} {b.Version}, saved {b.Created:g}.",
            $"Game Mode: automatic {(s.AutoGameMode ? "on" : "off")}, {s.GameProfiles.Count} game profile(s), " +
            $"{s.CustomGames.Count} custom game(s), {s.AppsToClose.Count} app(s) to close.",
            $"Look: {s.Theme} theme, {s.HomeCards.Count} Home cards, {s.DisabledModules.Count} module(s) switched off.",
        };
        return string.Join("\n", lines);
    }
}
