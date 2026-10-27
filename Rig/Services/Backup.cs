using System.Text.Json;

namespace Rig.Services;

/// <summary>A shareable snapshot of how someone has Rig set up.</summary>
public sealed class BackupFile
{
    public int Format { get; set; } = 1;
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

public static class Backup
{
    public const string Filter = "Rig setup (*.pcopt)|*.pcopt";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task ExportAsync(string path, AppSettings settings, IProgress<string> progress, bool includeData = false)
    {
        progress.Report("Reading tweaks…");
        var tweaks = Services.Tweaks.All().SelectMany(g => g.Tweaks).ToList();
        foreach (var t in tweaks) t.Refresh();

        progress.Report("Checking installed apps (this takes a few seconds)…");
        var apps = new List<string>();
        try
        {
            var inventory = await Winget.InventoryAsync();
            apps = Winget.Catalog().SelectMany(g => g.Packages).Where(p => Winget.IsInstalled(p, inventory)).Select(p => p.Id).ToList();
        }
        catch { /* winget missing: share settings and tweaks only */ }

        var file = new BackupFile
        {
            Version = Updater.CurrentVersion.ToString(),
            Created = DateTime.Now,
            Settings = settings,
            Tweaks = tweaks.Where(t => t.IsOn).Select(t => t.Name).ToList(),
            Apps = apps,
        };
        if (includeData)
            foreach (var name in AppSettings.DataFiles)
                if (File.Exists(AppSettings.DataFile(name))) file.Data[name] = await File.ReadAllTextAsync(AppSettings.DataFile(name));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, Json));
    }

    public static BackupFile Read(string path) =>
        JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Not a Rig setup file.");

    /// <summary>Applies settings and tweaks. Returns the tweaks that couldn't be applied (e.g. need admin).</summary>
    public static List<string> Apply(BackupFile backup, AppSettings settings)
    {
        settings.CopyFrom(backup.Settings);
        var catalog = Winget.Catalog().SelectMany(g => g.Packages).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.PendingInstallIds = backup.Apps.Where(catalog.Contains).ToList(); // only known, vetted packages
        settings.Save();
        // Only known file names are written back, never paths taken from the file.
        foreach (var (name, content) in backup.Data)
            if (AppSettings.DataFiles.Contains(name))
                try { File.WriteAllText(AppSettings.DataFile(name), content); } catch { }

        var skipped = new List<string>();
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
        return skipped;
    }

    /// <summary>Plain-English summary for the import confirmation.</summary>
    public static string Describe(BackupFile b)
    {
        var s = b.Settings;
        var lines = new List<string>
        {
            $"From Rig {b.Version}, saved {b.Created:g}:",
            $"  • Game Mode: automatic {(s.AutoGameMode ? "on" : "off")}, {s.GameProfiles.Count} game profile(s), " +
            $"{s.CustomGames.Count} custom game(s), {s.AppsToClose.Count} app(s) to close",
            $"  • {b.Tweaks.Count} tweak(s) to switch on",
            $"  • {b.Apps.Count} app(s) to offer for install (you choose on the Apps page)",
            $"  • Auto-clean {(s.AutoClean ? $"every {s.AutoCleanDays} day(s)" : "off")}, updates {(s.AutoCheckUpdates ? "automatic" : "manual")}",
        };
        if (b.Data.Count > 0)
            lines.Add($"  • Personal data: {string.Join(", ", b.Data.Keys.Select(Path.GetFileNameWithoutExtension))} (replaces yours)");
        return string.Join("\n", lines);
    }
}
