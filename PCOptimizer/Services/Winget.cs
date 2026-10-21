using System.Text;

namespace PCOptimizer.Services;

public sealed class Package : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Category { get; init; } = "";
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

/// <summary>Thin wrapper over the winget CLI that ships with Windows 10/11.</summary>
public static class Winget
{
    const string Common = "--accept-source-agreements --disable-interactivity";

    public static List<PackageGroup> Catalog() =>
    [
        Group("Gaming",
            ("Steam", "Valve.Steam"), ("Epic Games Launcher", "EpicGames.EpicGamesLauncher"), ("GOG Galaxy", "GOG.Galaxy"),
            ("EA app", "ElectronicArts.EADesktop"), ("Ubisoft Connect", "Ubisoft.Connect"), ("Battle.net", "Blizzard.BattleNet"),
            ("Discord", "Discord.Discord"), ("MSI Afterburner", "Guru3D.Afterburner"), ("Parsec", "Parsec.Parsec")),
        Group("Browsers",
            ("Google Chrome", "Google.Chrome"), ("Firefox", "Mozilla.Firefox"), ("Brave", "Brave.Brave")),
        Group("Media & streaming",
            ("OBS Studio", "OBSProject.OBSStudio"), ("VLC", "VideoLAN.VLC"), ("Spotify", "Spotify.Spotify"),
            ("Audacity", "Audacity.Audacity"), ("ShareX", "ShareX.ShareX")),
        Group("System tools",
            ("7-Zip", "7zip.7zip"), ("Everything search", "voidtools.Everything"), ("PowerToys", "Microsoft.PowerToys"),
            ("WizTree", "AntibodySoftware.WizTree"), ("HWiNFO", "REALiX.HWiNFO"), ("CPU-Z", "CPUID.CPU-Z"),
            ("GPU-Z", "TechPowerUp.GPU-Z"), ("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo"),
            ("Malwarebytes", "Malwarebytes.Malwarebytes"), ("Bitwarden", "Bitwarden.Bitwarden"),
            ("qBittorrent", "qBittorrent.qBittorrent")),
        Group("Development",
            ("VS Code", "Microsoft.VisualStudioCode"), ("Git", "Git.Git"), ("Windows Terminal", "Microsoft.WindowsTerminal"),
            ("Notepad++", "Notepad++.Notepad++"), ("Python 3.13", "Python.Python.3.13"), ("Node.js LTS", "OpenJS.NodeJS.LTS")),
    ];

    static PackageGroup Group(string name, params (string Name, string Id)[] apps) =>
        new(name, apps.Select(a => new Package { Name = a.Name, Id = a.Id, Category = name }).ToList());

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

    public static async Task<HashSet<string>> InstalledIdsAsync()
    {
        var (_, output) = await Shell.RunAsync("winget", $"list {Common}", Encoding.UTF8);
        return ParseTable(output, "Name", "Id", "Version").Select(r => r[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
