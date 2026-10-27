using System.Text.Json;
using Microsoft.Win32;

namespace Rig.Services;

public sealed record PlannedMove(string From, string To, string Category)
{
    public string Name => Path.GetFileName(From);
}

public sealed class OldInstaller : ObservableObject
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Fmt.Bytes(Size)} · downloaded {Modified:d MMM yyyy}";
    bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>
/// Sorts loose files in Downloads into folders by type. Only top-level files are moved (folders stay put),
/// downloads still in progress or touched in the last 2 minutes are skipped, and the last run can be undone.
/// </summary>
public static class DownloadOrganizer
{
    public static readonly (string Folder, string[] Extensions)[] Categories =
    [
        ("Installers", [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appinstaller"]),
        ("Archives", [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".img"]),
        ("Images", [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg", ".ico", ".tif", ".tiff", ".psd", ".avif"]),
        ("Videos", [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v"]),
        ("Music", [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".wma", ".opus"]),
        ("Documents", [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".epub", ".md"]),
        ("Code", [".json", ".xml", ".yml", ".yaml", ".py", ".js", ".ts", ".cs", ".cpp", ".h", ".java", ".html", ".css", ".ps1", ".bat", ".sh", ".sql", ".ini", ".cfg"]),
        ("Torrents", [".torrent"]),
        ("Fonts", [".ttf", ".otf", ".woff", ".woff2"]),
    ];

    /// <summary>Still being written by a browser or download manager.</summary>
    static readonly string[] Partial = [".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp", ".!ut", ".!qb"];

    public static string Folder
    {
        get
        {
            if (Environment.GetEnvironmentVariable("RIG_TEST_DOWNLOADS") is { Length: > 0 } test) return test;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p) return Environment.ExpandEnvironmentVariables(p);
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    static string UndoFile => AppSettings.DataFile("downloads-undo.json");

    public static string CategoryOf(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        foreach (var (folder, exts) in Categories) if (exts.Contains(ext)) return folder;
        return "Other";
    }

    /// <summary>What "Organize" would do right now, without touching anything.</summary>
    public static List<PlannedMove> Plan()
    {
        var root = Folder;
        var moves = new List<PlannedMove>();
        if (!Directory.Exists(root)) return moves;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(root).EnumerateFiles())
        {
            try
            {
                if (f.Attributes.HasFlag(FileAttributes.Hidden) || f.Attributes.HasFlag(FileAttributes.System)) continue;
                var ext = f.Extension.ToLowerInvariant();
                if (Partial.Contains(ext) || f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.Now - f.LastWriteTime < TimeSpan.FromMinutes(2)) continue; // may still be downloading
                // A finished file can have a sibling ".crdownload" if Chrome is mid-way through a second copy; leave both.
                if (Partial.Any(p => File.Exists(f.FullName + p))) continue;
                var category = CategoryOf(f.Name);
                var dest = FreeName(Path.Combine(root, category), f.Name, taken);
                taken.Add(dest);
                moves.Add(new PlannedMove(f.FullName, dest, category));
            }
            catch { }
        }
        return moves;
    }

    static string FreeName(string dir, string name, HashSet<string> taken)
    {
        var path = Path.Combine(dir, name);
        for (int i = 2; File.Exists(path) || Directory.Exists(path) || taken.Contains(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    /// <summary>Moves the planned files. Returns how many moved; the batch is saved so it can be undone.</summary>
    public static int Apply(List<PlannedMove> moves)
    {
        var done = new List<PlannedMove>();
        foreach (var m in moves)
        {
            try
            {
                if (!File.Exists(m.From) || File.Exists(m.To)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(m.To)!);
                File.Move(m.From, m.To);
                done.Add(m);
            }
            catch { /* in use: leave it */ }
        }
        if (done.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
                File.WriteAllText(UndoFile, JsonSerializer.Serialize(done));
            }
            catch { }
        }
        return done.Count;
    }

    public static bool CanUndo => File.Exists(UndoFile);

    /// <summary>Puts the last organized batch back where it was. Returns how many went back.</summary>
    public static int Undo()
    {
        List<PlannedMove> moves;
        try { moves = JsonSerializer.Deserialize<List<PlannedMove>>(File.ReadAllText(UndoFile)) ?? []; }
        catch { return 0; }
        int back = 0;
        foreach (var m in moves)
        {
            try
            {
                if (File.Exists(m.To) && !File.Exists(m.From)) { File.Move(m.To, m.From); back++; }
                // Remove the category folder if the undo emptied it.
                var dir = Path.GetDirectoryName(m.To)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
        try { File.Delete(UndoFile); } catch { }
        return back;
    }

    /// <summary>Installers sitting in Downloads (or its Installers folder) for more than 30 days: usually safe to delete.</summary>
    public static List<OldInstaller> OldInstallers()
    {
        var list = new List<OldInstaller>();
        var root = Folder;
        foreach (var dir in new[] { root, Path.Combine(root, "Installers") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                if (!Categories[0].Extensions.Contains(f.Extension.ToLowerInvariant())) continue;
                if (DateTime.Now - f.LastWriteTime < TimeSpan.FromDays(30)) continue;
                list.Add(new OldInstaller { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime });
            }
        }
        return list.OrderByDescending(i => i.Size).ToList();
    }
}
