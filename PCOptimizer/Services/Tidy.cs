namespace PCOptimizer.Services;

public sealed class TidyItem : ObservableObject
{
    public required string FullPath { get; init; }
    public required string Kind { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>The scanned folder, for empty folders (so the tree can be updated after removal).</summary>
    public DiskNode? Node { get; init; }

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public static class Tidy
{
    static readonly string AppData = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))!;

    /// <summary>Folders with no files anywhere inside; only the top-most of each empty branch is listed.</summary>
    public static List<TidyItem> EmptyFolders(DiskNode root)
    {
        var list = new List<TidyItem>();
        var stack = new Stack<DiskNode>(root.Folders ?? []);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.FileCount == 0)
            {
                if (DiskAnalyzer.WhyProtected(n.FullPath) is not null) continue;
                bool appData = n.FullPath.StartsWith(AppData, StringComparison.OrdinalIgnoreCase);
                list.Add(new TidyItem
                {
                    FullPath = n.FullPath, Kind = "Empty folder", Node = n,
                    Detail = appData ? "Inside AppData: an app may expect it, so it isn't ticked" : "",
                    IsChecked = !appData,
                });
                continue; // children are empty too; removing this removes them
            }
            foreach (var c in n.Folders ?? []) stack.Push(c);
        }
        return list.OrderBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Desktop and Start menu shortcuts whose target file or folder no longer exists.</summary>
    public static List<TidyItem> BrokenShortcuts()
    {
        var places = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return [];
        dynamic shell = Activator.CreateInstance(shellType)!;
        var list = new List<TidyItem>();
        foreach (var place in places.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(place, "*.lnk", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var link in links)
            {
                try
                {
                    string target = shell.CreateShortcut(link).TargetPath;
                    if (string.IsNullOrWhiteSpace(target) || target.Length < 3 || target[1] != ':') continue; // URLs, shell items
                    var drive = new DriveInfo(target[..1]);
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue; // unplugged USB isn't "broken"
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    list.Add(new TidyItem { FullPath = link, Kind = "Broken shortcut", Detail = $"Points to {target}", IsChecked = true });
                }
                catch { }
            }
        }
        return list;
    }
}
