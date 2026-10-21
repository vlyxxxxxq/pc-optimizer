using System.Runtime.InteropServices;

namespace PCOptimizer.Services;

public sealed record FileEntry(string FullPath, long Size, DateTime Modified)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string SizeText => Fmt.Bytes(Size);
    public string ModifiedText => Modified.ToString("d");
}

public sealed record FolderEntry(string FullPath, string Name, long Size, double Percent)
{
    public string SizeText => Fmt.Bytes(Size);
    public bool IsFolder => Directory.Exists(FullPath);
}

public sealed record DiskScanResult(List<FolderEntry> Folders, List<FileEntry> Files, long TotalSize, int FileCount);

public static class DiskAnalyzer
{
    const int KeepLargest = 200;

    static EnumerationOptions Options(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // don't double-count via junctions
    };

    /// <summary>
    /// Sizes every immediate subfolder of root and keeps the largest files found anywhere below it.
    /// Directories are walked in parallel from a shared queue, so one huge folder (like AppData)
    /// is split across all workers instead of pinning a single thread.
    /// </summary>
    public static DiskScanResult Scan(string root, IProgress<int> progress, CancellationToken ct)
    {
        var dir = new DirectoryInfo(root);
        var subdirs = dir.EnumerateDirectories("*", Options(false)).ToList();
        // Slot n holds files sitting directly in root.
        var sizes = new long[subdirs.Count + 1];
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(string Dir, int Slot)>();
        for (int i = 0; i < subdirs.Count; i++) queue.Enqueue((subdirs[i].FullName, i));
        int pending = subdirs.Count;
        int count = 0;
        var heaps = new List<PriorityQueue<FileEntry, long>>();

        var rootHeap = new PriorityQueue<FileEntry, long>();
        foreach (var f in dir.EnumerateFiles("*", Options(false)))
        {
            Keep(rootHeap, f);
            sizes[^1] += f.Length;
            count++;
        }
        heaps.Add(rootHeap);

        void Worker()
        {
            var heap = new PriorityQueue<FileEntry, long>();
            while (Volatile.Read(ref pending) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out var item)) { Thread.Sleep(1); continue; }
                try
                {
                    long sum = 0;
                    foreach (var entry in new DirectoryInfo(item.Dir).EnumerateFileSystemInfos("*", Options(false)))
                    {
                        if (entry is FileInfo f)
                        {
                            Keep(heap, f);
                            sum += f.Length;
                            int c = Interlocked.Increment(ref count);
                            if (c % 5000 == 0) progress.Report(c);
                        }
                        else
                        {
                            Interlocked.Increment(ref pending); // before enqueue, so pending never hits 0 early
                            queue.Enqueue((entry.FullName, item.Slot));
                        }
                    }
                    Interlocked.Add(ref sizes[item.Slot], sum);
                }
                catch { /* folder vanished or access denied */ }
                finally { Interlocked.Decrement(ref pending); }
            }
            lock (heaps) heaps.Add(heap);
        }

        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8))
            .Select(_ => Task.Factory.StartNew(Worker, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try { Task.WaitAll(workers); }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.Any(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }

        var folders = subdirs.Select((d, i) => (Path: d.FullName, d.Name, Size: sizes[i])).ToList();
        if (sizes[^1] > 0) folders.Add((root, "(files in this folder)", sizes[^1]));
        long max = Math.Max(1, folders.Count == 0 ? 1 : folders.Max(f => f.Size));
        progress.Report(count);

        return new DiskScanResult(
            folders.OrderByDescending(f => f.Size).Select(f => new FolderEntry(f.Path, f.Name, f.Size, 100.0 * f.Size / max)).ToList(),
            heaps.SelectMany(h => h.UnorderedItems.Select(i => i.Element))
                 .OrderByDescending(f => f.Size).Take(KeepLargest).ToList(),
            sizes.Sum(), count);
    }

    static void Keep(PriorityQueue<FileEntry, long> heap, FileInfo f)
    {
        long len = f.Length;
        if (heap.Count >= KeepLargest && len <= heap.Peek().Size) return; // too small to matter
        heap.Enqueue(new FileEntry(f.FullName, len, f.LastWriteTime), len);
        if (heap.Count > KeepLargest) heap.Dequeue(); // min-heap: drops the smallest
    }

    static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    /// <summary>System and installed-program files must be removed by uninstalling, not by deleting.</summary>
    public static bool IsProtected(string path) =>
        ProtectedRoots.Any(r => r.Length > 0 && path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
        || (Path.GetDirectoryName(path)?.Length <= 3 && (File.GetAttributes(path) & FileAttributes.System) != 0);

    /// <summary>Sends a file to the Recycle Bin so the user can restore it.</summary>
    public static bool Recycle(string path)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + '\0',
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
        };
        return Native.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }
}
