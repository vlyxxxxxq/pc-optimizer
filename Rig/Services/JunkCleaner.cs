using System.IO;
using System.Runtime.InteropServices;

namespace Rig.Services;

/// <summary>A folder whose matching files are junk. The folder itself is never deleted.</summary>
public sealed record JunkTarget(string Directory, string Pattern = "*", bool Recursive = true);

public sealed record ScanResult(List<string> Files, long Size, int Count);

public sealed record CleanResult(long Freed, int Deleted, List<string> Failed);

public sealed class JunkCategory : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public Func<IEnumerable<JunkTarget>> Targets { get; init; } = () => [];
    /// <summary>Files newer than this are left alone (they may still be in use).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public bool IsRecycleBin { get; init; }

    internal List<string> Files { get; private set; } = [];

    bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    bool _isScanned;
    public bool IsScanned { get => _isScanned; private set { if (Set(ref _isScanned, value)) OnPropertyChanged(nameof(SizeText)); } }

    long _sizeBytes;
    public long SizeBytes { get => _sizeBytes; private set { if (Set(ref _sizeBytes, value)) OnPropertyChanged(nameof(SizeText)); } }

    int _itemCount;
    public int ItemCount { get => _itemCount; private set => Set(ref _itemCount, value); }

    string _status = "Not scanned";
    public string Status { get => _status; set => Set(ref _status, value); }

    public string SizeText => IsScanned ? Fmt.Bytes(SizeBytes) : "—";

    public void ApplyScan(ScanResult r)
    {
        Files = r.Files;
        SizeBytes = r.Size;
        ItemCount = r.Count;
        IsScanned = true;
        Status = r.Count == 0 ? "Nothing to clean" : $"{r.Count:N0} items";
    }

    public void ApplyClean(CleanResult r)
    {
        Files = r.Failed;
        SizeBytes = Math.Max(0, SizeBytes - r.Freed);
        ItemCount = r.Failed.Count;
        Status = r.Failed.Count == 0
            ? $"Cleaned {Fmt.Bytes(r.Freed)}"
            : $"Cleaned {Fmt.Bytes(r.Freed)} · {r.Failed.Count:N0} in use or protected";
    }
}

public static class JunkCleaner
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static List<JunkCategory> CreateCategories() =>
    [
        new()
        {
            Name = "Temporary files",
            Description = "Your user temp folder. Files modified in the last 24 hours are kept.",
            Targets = () => [new(Path.GetTempPath())],
            MinAge = TimeSpan.FromDays(1),
        },
        new()
        {
            Name = "Windows temporary files",
            Description = "System temp folder. Most files need administrator rights to remove.",
            Targets = () => [new(Path.Combine(WinDir, "Temp"))],
            MinAge = TimeSpan.FromDays(1),
            IsSelected = Elevation.IsElevated,
        },
        new()
        {
            Name = "Recycle Bin",
            Description = "Permanently deletes everything in the Recycle Bin on all drives.",
            IsRecycleBin = true,
            IsSelected = false,
        },
        new()
        {
            Name = "Chrome cache",
            Description = "Cached web content for Google Chrome. Close Chrome for the best result.",
            Targets = () => ChromiumCaches(Path.Combine(Local, @"Google\Chrome\User Data")),
        },
        new()
        {
            Name = "Edge cache",
            Description = "Cached web content for Microsoft Edge. Close Edge for the best result.",
            Targets = () => ChromiumCaches(Path.Combine(Local, @"Microsoft\Edge\User Data")),
        },
        new()
        {
            Name = "Firefox cache",
            Description = "Cached web content for Mozilla Firefox.",
            Targets = () => SubfolderOfEach(Path.Combine(Local, @"Mozilla\Firefox\Profiles"), "cache2"),
        },
        new()
        {
            Name = "Thumbnail cache",
            Description = "Explorer image previews. Windows rebuilds them as needed.",
            Targets = () => [new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false)],
        },
        new()
        {
            Name = "DirectX shader cache",
            Description = "Compiled graphics shaders. Games rebuild them on next launch.",
            Targets = () => [new(Path.Combine(Local, "D3DSCache"))],
        },
        new()
        {
            Name = "Error reports & crash dumps",
            Description = "Windows Error Reporting archives and application crash dumps.",
            Targets = () =>
            [
                new(Path.Combine(Local, @"Microsoft\Windows\WER")),
                new(Path.Combine(ProgramData, @"Microsoft\Windows\WER")),
                new(Path.Combine(Local, "CrashDumps")),
            ],
        },
    ];

    static IEnumerable<JunkTarget> ChromiumCaches(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        yield return new(Path.Combine(userData, "ShaderCache"));
        yield return new(Path.Combine(userData, "GrShaderCache"));
        foreach (var profile in SafeDirectories(userData))
        {
            yield return new(Path.Combine(profile, @"Cache\Cache_Data"));
            yield return new(Path.Combine(profile, "Code Cache"));
            yield return new(Path.Combine(profile, "GPUCache"));
        }
    }

    static IEnumerable<JunkTarget> SubfolderOfEach(string parent, string sub) =>
        SafeDirectories(parent).Select(p => new JunkTarget(Path.Combine(p, sub)));

    static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch { return []; }
    }

    static EnumerationOptions Options(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        // Never follow junctions/symlinks out of the junk folder; include hidden/system files.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static ScanResult Scan(JunkCategory category)
    {
        if (category.IsRecycleBin)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            return Native.SHQueryRecycleBin(null, ref info) == 0
                ? new([], info.i64Size, (int)info.i64NumItems)
                : new([], 0, 0);
        }

        var cutoff = DateTime.UtcNow - category.MinAge;
        var files = new List<string>();
        long size = 0;
        foreach (var t in category.Targets())
        {
            if (!Directory.Exists(t.Directory)) continue;
            try
            {
                foreach (var f in new DirectoryInfo(t.Directory).EnumerateFiles(t.Pattern, Options(t.Recursive)))
                {
                    try
                    {
                        if (f.LastWriteTimeUtc > cutoff) continue;
                        size += f.Length;
                        files.Add(f.FullName);
                    }
                    catch { /* vanished or unreadable */ }
                }
            }
            catch { /* root became inaccessible */ }
        }
        return new(files, size, files.Count);
    }

    public static CleanResult Clean(JunkCategory category)
    {
        if (category.IsRecycleBin)
        {
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            // E_UNEXPECTED is returned when the bin is already empty.
            bool ok = hr == 0 || hr == unchecked((int)0x8000FFFF);
            return new(ok ? category.SizeBytes : 0, ok ? category.ItemCount : 0, ok ? [] : ["Recycle Bin"]);
        }

        long freed = 0;
        int deleted = 0;
        var failed = new List<string>();
        foreach (var path in category.Files)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                long len = fi.Length;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                freed += len;
                deleted++;
            }
            catch
            {
                failed.Add(path); // locked by a running program or access denied
            }
        }

        RemoveEmptyFolders(category);
        return new(freed, deleted, failed);
    }

    static void RemoveEmptyFolders(JunkCategory category)
    {
        var cutoff = DateTime.UtcNow - category.MinAge;
        foreach (var t in category.Targets())
        {
            if (!t.Recursive || t.Pattern != "*" || !Directory.Exists(t.Directory)) continue;
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(t.Directory, "*", Options(true)).ToList(); }
            catch { continue; }
            // Deepest first so parents become empty after their children go.
            foreach (var d in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(d) <= cutoff && !Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
                }
                catch { }
            }
        }
    }
}
