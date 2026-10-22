using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PCOptimizer.Services;

public sealed class DuplicateFile : ObservableObject
{
    public required string FullPath { get; init; }
    public DateTime Modified { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string ModifiedText => Modified.ToString("g");

    bool _isChecked;
    /// <summary>Ticked = will be sent to the Recycle Bin.</summary>
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public sealed class DuplicateGroup
{
    public required long Size { get; init; }
    public required List<DuplicateFile> Files { get; init; }
    public string Title => $"{Files.Count} copies · {Fmt.Bytes(Size)} each · {Fmt.Bytes(Wasted)} wasted";
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>Finds identical files: same size, then same first 64 KB, then same full content (MD5).</summary>
public static class DuplicateFinder
{
    const int PeekBytes = 64 * 1024;

    public static List<DuplicateGroup> Find(string root, long minSize, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Listing files…");
        var bySize = new Dictionary<long, List<FileInfo>>();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            long len;
            try { len = f.Length; } catch { continue; }
            if (len < minSize) continue;
            if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = [];
            list.Add(f);
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int done = 0;
        var groups = new ConcurrentBag<DuplicateGroup>();

        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, kv =>
        {
            // Cheap pass on the first bytes, then confirm with a full hash.
            foreach (var quick in kv.Value.GroupBy(f => Hash(f.FullName, PeekBytes)).Where(g => g.Key is not null && g.Count() > 1))
            foreach (var full in quick.GroupBy(f => kv.Key <= PeekBytes ? quick.Key : Hash(f.FullName, long.MaxValue))
                                      .Where(g => g.Key is not null && g.Count() > 1))
            {
                groups.Add(new DuplicateGroup
                {
                    Size = kv.Key,
                    Files = full.OrderByDescending(f => f.LastWriteTime)
                                .Select(f => new DuplicateFile { FullPath = f.FullName, Modified = f.LastWriteTime })
                                .ToList(),
                });
            }
            int n = Interlocked.Increment(ref done);
            if (n % 20 == 0) progress.Report($"Comparing files… {n:N0} of {candidates.Count:N0} size groups");
        });

        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    static string? Hash(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var md5 = MD5.Create();
            if (maxBytes >= stream.Length) return Convert.ToHexString(md5.ComputeHash(stream));
            var buffer = new byte[maxBytes];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(md5.ComputeHash(buffer, 0, read));
        }
        catch { return null; } // locked or unreadable: never report it as a duplicate
    }
}
