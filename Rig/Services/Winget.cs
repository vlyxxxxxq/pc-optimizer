using System.Text;

namespace Rig.Services;

public sealed class Package : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Category { get; init; } = "";
    /// <summary>Regex for the installed program's display name, anchored at the start (e.g. "Mozilla Firefox").</summary>
    public string? NamePattern { get; init; }
    public string Version { get; init; } = "";
    public string Available { get; init; } = "";
    public string VersionText => Available.Length > 0 ? $"{Version} → {Available}" : Version;

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    bool _isInstalled;
    public bool IsInstalled { get => _isInstalled; set { if (Set(ref _isInstalled, value)) OnPropertyChanged(nameof(CanInstall)); } }
    public bool CanInstall => !IsInstalled;

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed record PackageGroup(string Name, List<Package> Packages);

public sealed record InstalledInventory(HashSet<string> Ids, List<string> Names);

/// <summary>Thin wrapper over the winget CLI that ships with Windows 10/11.</summary>
public static class Winget
{
    const string Common = "--accept-source-agreements --disable-interactivity";

    // Third value: how the app names itself once installed (Settings → Apps, Start menu, winget's Name column).
    public static List<PackageGroup> Catalog() =>
    [
        Group("Gaming",
            ("Steam", "Valve.Steam", "Steam"), ("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "Epic Games Launcher"),
            ("GOG Galaxy", "GOG.Galaxy", "GOG GALAXY"), ("EA app", "ElectronicArts.EADesktop", "EA app|EA$"),
            ("Ubisoft Connect", "Ubisoft.Connect", "Ubisoft Connect"), ("Battle.net", "Blizzard.BattleNet", @"Battle\.net"),
            ("Discord", "Discord.Discord", "Discord"), ("MSI Afterburner", "Guru3D.Afterburner", "MSI Afterburner"),
            ("Parsec", "Parsec.Parsec", "Parsec")),
        Group("Browsers",
            ("Google Chrome", "Google.Chrome", "Google Chrome"), ("Firefox", "Mozilla.Firefox", "Mozilla Firefox|Firefox"),
            ("Brave", "Brave.Brave", "Brave")),
        Group("Media & streaming",
            ("OBS Studio", "OBSProject.OBSStudio", "OBS Studio"), ("VLC", "VideoLAN.VLC", "VLC media player|VLC"),
            ("Spotify", "Spotify.Spotify", "Spotify"), ("Audacity", "Audacity.Audacity", "Audacity"),
            ("ShareX", "ShareX.ShareX", "ShareX")),
        Group("System tools",
            ("7-Zip", "7zip.7zip", "7-Zip"), ("Everything search", "voidtools.Everything", "Everything"),
            ("PowerToys", "Microsoft.PowerToys", "PowerToys"), ("WizTree", "AntibodySoftware.WizTree", "WizTree"),
            ("HWiNFO", "REALiX.HWiNFO", "HWiNFO"), ("CPU-Z", "CPUID.CPU-Z", "CPUID CPU-Z|CPU-Z"),
            ("GPU-Z", "TechPowerUp.GPU-Z", "(TechPowerUp )?GPU-Z"), ("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo"),
            ("Malwarebytes", "Malwarebytes.Malwarebytes", "Malwarebytes"), ("Bitwarden", "Bitwarden.Bitwarden", "Bitwarden"),
            ("qBittorrent", "qBittorrent.qBittorrent", "qBittorrent")),
        Group("Development",
            ("VS Code", "Microsoft.VisualStudioCode", "(Microsoft )?Visual Studio Code"), ("Git", "Git.Git", "Git"),
            ("Windows Terminal", "Microsoft.WindowsTerminal", "Windows Terminal"), ("Notepad++", "Notepad++.Notepad++", @"Notepad\+\+"),
            ("Python 3.13", "Python.Python.3.13", @"Python 3\.13"), ("Node.js LTS", "OpenJS.NodeJS.LTS", @"Node\.js")),
    ];

    static PackageGroup Group(string name, params (string Name, string Id, string Pattern)[] apps) =>
        new(name, apps.Select(a => new Package { Name = a.Name, Id = a.Id, NamePattern = a.Pattern, Category = name }).ToList());

    /// <summary>
    /// True when winget knows the package by id, or any installed program's display name matches the package's
    /// pattern. The name check matters: apps installed outside winget show up as "ARP\User\X64\Discord" instead of
    /// "Discord.Discord". Patterns match whole words from the start, so "Git" doesn't match "GitHub CLI".
    /// </summary>
    public static bool IsInstalled(Package p, InstalledInventory inventory)
    {
        if (inventory.Ids.Contains(p.Id) ||
            inventory.Ids.Any(id => id.EndsWith('…') && p.Id.StartsWith(id[..^1], StringComparison.OrdinalIgnoreCase)))
            return true;
        if (p.NamePattern is null) return false;
        var regex = new System.Text.RegularExpressions.Regex($@"^(?:{p.NamePattern})(?!\w)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return inventory.Names.Any(n => regex.IsMatch(n.Trim()));
    }

    public static async Task<bool> IsAvailableAsync()
    {
        try { return (await Shell.RunAsync("winget", "--version")).ExitCode == 0; }
        catch { return false; }
    }

    public static async Task<List<Package>> UpgradesAsync()
    {
        var (_, output) = await Shell.RunAsync("winget", $"upgrade {Common}", Encoding.UTF8);
        return ParseTable(output, "Name", "Id", "Version", "Available")
            .Select(r => new Package { Name = r[0], Id = r[1], Version = r[2], Available = r[3], IsSelected = true })
            .Where(p => p.Id.Length > 0 && p.Available.Length > 0)
            .ToList();
    }

    /// <summary>Everything we know is installed: winget's list, Settings → Apps entries and Start menu shortcuts.</summary>
    public static async Task<InstalledInventory> InventoryAsync()
    {
        var (_, output) = await Shell.RunAsync("winget", $"list {Common}", Encoding.UTF8);
        var rows = ParseTable(output, "Name", "Id", "Version");
        var names = new List<string>(rows.Select(r => r[0]));
        await Task.Run(() =>
        {
            names.AddRange(InstalledApps.LoadPrograms().Select(a => a.Name));
            names.AddRange(StartMenuNames());
        });
        return new InstalledInventory(
            rows.Select(r => r[1]).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    static IEnumerable<string> StartMenuNames()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var l in links)
            {
                var name = Path.GetFileNameWithoutExtension(l);
                if (!name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) yield return name;
            }
        }
    }

    public static async Task<(bool Ok, string Output)> UpgradeAsync(string id)
    {
        var (code, output) = await Shell.RunAsync("winget",
            $"upgrade --id \"{id}\" -e --silent --accept-package-agreements {Common}", Encoding.UTF8);
        return (code == 0, LastLine(output));
    }

    public static async Task<(bool Ok, string Output)> InstallAsync(string id)
    {
        var (code, output) = await Shell.RunAsync("winget",
            $"install --id \"{id}\" -e --silent --accept-package-agreements {Common}", Encoding.UTF8);
        return (code == 0, LastLine(output));
    }

    static string LastLine(string output) =>
        CleanLines(output).LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

    /// <summary>winget redraws progress with \r; keep only what each line finally showed.</summary>
    static IEnumerable<string> CleanLines(string output) =>
        output.Split('\n').Select(l =>
        {
            var line = l.TrimEnd('\r');
            int cr = line.LastIndexOf('\r');
            return cr >= 0 ? line[(cr + 1)..] : line;
        });

    /// <summary>Parses winget's fixed-width table using the header's column positions.</summary>
    static List<string[]> ParseTable(string output, params string[] columns)
    {
        var lines = CleanLines(output).ToList();
        int header = lines.FindIndex(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith(columns[0]) && columns.All(c => l.Contains(c));
        });
        var rows = new List<string[]>();
        if (header < 0) return rows;

        var head = lines[header];
        int indent = head.Length - head.TrimStart().Length;
        var starts = columns.Select(c => head.IndexOf(c, StringComparison.Ordinal) - indent).ToArray();

        for (int i = header + 2; i < lines.Count; i++)
        {
            var row = lines[i];
            if (row.Trim().Length == 0 || row.TrimStart().StartsWith('-')) break; // end of table
            if (char.IsDigit(row.TrimStart().FirstOrDefault()) && row.Contains(" upgrade")) break;
            if (row.Length < starts[1]) continue; // wrapped or stray line

            var cells = new string[columns.Length];
            for (int c = 0; c < columns.Length; c++)
            {
                int start = Math.Min(starts[c], row.Length);
                int end = c + 1 < columns.Length ? Math.Min(starts[c + 1], row.Length) : row.Length;
                cells[c] = end > start ? row[start..end].Trim() : "";
            }
            // Columns after the requested ones (e.g. Source) bleed into the last cell; keep the first word.
            cells[^1] = cells[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            rows.Add(cells);
        }
        return rows;
    }
}
