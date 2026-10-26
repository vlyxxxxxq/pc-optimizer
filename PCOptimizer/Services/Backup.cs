using System.Text.Json;

namespace PCOptimizer.Services;

/// <summary>A shareable snapshot of how someone has PC Optimizer set up.</summary>
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
}

public static class Backup
{
    public const string Filter = "PC Optimizer setup (*.pcopt)|*.pcopt";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task ExportAsync(string path, AppSettings settings, IProgress<string> progress)
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
            Version = Updater.CurrentVersion.ToString(3),
            Created = DateTime.Now,
            Settings = settings,
            Tweaks = tweaks.Where(t => t.IsOn).Select(t => t.Name).ToList(),
            Apps = apps,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, Json));
    }

    public static BackupFile Read(string path) =>
        JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Not a PC Optimizer setup file.");

    /// <summary>Applies settings and tweaks. Returns the tweaks that couldn't be applied (e.g. need admin).</summary>
    public static List<string> Apply(BackupFile backup, AppSettings settings)
    {
        settings.CopyFrom(backup.Settings);
        var catalog = Winget.Catalog().SelectMany(g => g.Packages).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.PendingInstallIds = backup.Apps.Where(catalog.Contains).ToList(); // only known, vetted packages
        settings.Save();

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
            $"From PC Optimizer {b.Version}, saved {b.Created:g}:",
            $"  • Game Mode: automatic {(s.AutoGameMode ? "on" : "off")}, {s.GameProfiles.Count} game profile(s), " +
            $"{s.CustomGames.Count} custom game(s), {s.AppsToClose.Count} app(s) to close",
            $"  • {b.Tweaks.Count} tweak(s) to switch on",
            $"  • {b.Apps.Count} app(s) to offer for install (you choose on the Apps page)",
            $"  • Auto-clean {(s.AutoClean ? $"every {s.AutoCleanDays} day(s)" : "off")}, updates {(s.AutoCheckUpdates ? "automatic" : "manual")}",
        };
        return string.Join("\n", lines);
    }
}
