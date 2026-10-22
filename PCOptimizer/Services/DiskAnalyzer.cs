using System.Collections.Concurrent;

namespace PCOptimizer.Services;

public sealed record FileEntry(string FullPath, long Size, DateTime Modified)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public string SizeText => Fmt.Bytes(Size);
    public string ModifiedText => Modified.ToString("d");
}

/// <summary>
/// A folder (from the scan) or a file (loaded on demand when its folder is expanded).
/// Folder sizes include everything below them.
/// </summary>
public sealed class DiskNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public DiskNode? Parent { get; set; }

    public long Size { get; set; }
    /// <summary>Files anywhere below (folders only).</summary>
    public int FileCount { get; set; }
    /// <summary>Files sitting directly in this folder.</summary>
    public long LooseSize { get; set; }
    public int LooseCount { get; set; }

    /// <summary>Subfolders, largest first. Null when there are none.</summary>
    public List<DiskNode>? Folders { get; set; }
    /// <summary>Files directly in this folder, largest first; null until loaded.</summary>
    public List<DiskNode>? Files { get; set; }

    public bool HasChildren => Folders is { Count: > 0 } || LooseCount > 0;
}

public sealed record DiskScanResult(DiskNode Root, List<FileEntry> LargestFiles);

public static class DiskAnalyzer
{
    const int KeepLargest = 200;

    static EnumerationOptions Options() => new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // don't double-count via junctions/symlinks
    };

    /// <summary>
    /// Builds the whole folder tree under root. Folders are walked in parallel from a shared queue,
    /// so one huge folder is split across all workers. Progress reports files counted so far.
    /// </summary>
    public static DiskScanResult Scan(string root, IProgress<int> progress, CancellationToken ct)
    {
        var rootNode = new DiskNode { Name = root, FullPath = root, IsDirectory = true };
        var queue = new ConcurrentQueue<DiskNode>();
        queue.Enqueue(rootNode);
        int pending = 1, count = 0;
        var heaps = new List<PriorityQueue<FileEntry, long>>();

        void Worker()
        {
            var heap = new PriorityQueue<FileEntry, long>();
            while (Volatile.Read(ref pending) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out var node)) { Thread.Sleep(1); continue; }
                try
                {
                    long loose = 0;
                    int looseCount = 0;
                    List<DiskNode>? folders = null;
                    foreach (var entry in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", Options()))
                    {
                        if (entry is FileInfo f)
                        {
                            long len = f.Length;
                            loose += len;
                            looseCount++;
                            if (heap.Count < KeepLargest || len > heap.Peek().Size)
                            {
                                heap.Enqueue(new FileEntry(f.FullName, len, f.LastWriteTime), len);
                                if (heap.Count > KeepLargest) heap.Dequeue();
                            }
                            int c = Interlocked.Increment(ref count);
                            if (c % 5000 == 0) progress.Report(c);
                        }
                        else
                        {
                            var child = new DiskNode { Name = entry.Name, FullPath = entry.FullName, IsDirectory = true, Parent = node };
                            (folders ??= []).Add(child);
                            Interlocked.Increment(ref pending); // before enqueue, so pending never hits 0 early
                            queue.Enqueue(child);
                        }
                    }
                    node.LooseSize = loose;
                    node.LooseCount = looseCount;
                    node.Folders = folders;
                }
                catch { /* access denied or vanished: counts as empty */ }
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

        Aggregate(rootNode);
        progress.Report(count);
        return new DiskScanResult(rootNode,
            heaps.SelectMany(h => h.UnorderedItems.Select(i => i.Element))
                 .OrderByDescending(f => f.Size).Take(KeepLargest).ToList());
    }

    /// <summary>Totals sizes bottom-up and sorts every folder's subfolders largest first.</summary>
    static void Aggregate(DiskNode root)
    {
        // Iterative post-order so very deep trees can't overflow the stack.
        var stack = new Stack<(DiskNode Node, bool Done)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, done) = stack.Pop();
            if (!done)
            {
                stack.Push((node, true));
                if (node.Folders is { } fs) foreach (var f in fs) stack.Push((f, false));
                continue;
            }
            node.Size = node.LooseSize;
            node.FileCount = node.LooseCount;
            if (node.Folders is { } folders)
            {
                foreach (var f in folders) { node.Size += f.Size; node.FileCount += f.FileCount; }
                folders.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
        }
    }

    /// <summary>Lists the files directly in a folder (on demand, when it's expanded).</summary>
    public static void LoadFiles(DiskNode folder)
    {
        if (folder.Files is not null) return;
        var files = new List<DiskNode>();
        try
        {
            foreach (var f in new DirectoryInfo(folder.FullPath).EnumerateFiles("*", Options()))
            {
                try { files.Add(new DiskNode { Name = f.Name, FullPath = f.FullName, Size = f.Length, Parent = folder }); }
                catch { }
            }
        }
        catch { }
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        folder.Files = files;
    }

    /// <summary>Finds the scanned folder for a path, or null if it's outside the scan.</summary>
    public static DiskNode? FindFolder(DiskNode root, string path)
    {
        var rootPath = root.FullPath.TrimEnd('\\');
        path = path.TrimEnd('\\');
        if (path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) return root;
        if (!path.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var node = root;
        foreach (var part in path[(rootPath.Length + 1)..].Split('\\'))
        {
            node = node.Folders?.FirstOrDefault(f => f.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Takes a deleted file or folder out of the tree and shrinks every parent's totals.</summary>
    public static void Remove(DiskNode node)
    {
        var parent = node.Parent;
        if (parent is null) return;
        long size = node.Size;
        int files = node.IsDirectory ? node.FileCount : 1;
        if (node.IsDirectory) parent.Folders?.Remove(node);
        else
        {
            parent.Files?.Remove(node);
            parent.LooseSize -= size;
            parent.LooseCount -= 1;
        }
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= size;
            p.FileCount -= files;
        }
    }

    /// <summary>Removes a deleted file known only by path (e.g. from the Largest files or Duplicates lists).</summary>
    public static void RemoveFile(DiskNode root, string path, long size)
    {
        var folder = FindFolder(root, Path.GetDirectoryName(path) ?? "");
        if (folder is null) return;
        if (folder.Files?.FirstOrDefault(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } node)
        {
            Remove(node);
            return;
        }
        folder.LooseSize -= size;
        folder.LooseCount -= 1;
        for (var p = folder; p is not null; p = p.Parent)
        {
            p.Size -= size;
            p.FileCount -= 1;
        }
    }

    static readonly string[] ProtectedTrees =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    // Folders that may be emptied but must never be deleted themselves.
    static readonly string[] ProtectedFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "", // C:\Users
    ];

    /// <summary>Why a path can't be deleted here, or null if it can.</summary>
    public static string? WhyProtected(string path)
    {
        var p = path.TrimEnd('\\');
        if (p.Length <= 2) return "it's a whole drive";
        if (ProtectedTrees.Any(r => r.Length > 0 &&
                (p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))))
            return "it's part of Windows or an installed program (uninstall the program instead)";
        if (ProtectedFolders.Any(r => r.Length > 0 && p.Equals(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return "it's a Windows user folder (you can delete what's inside it)";
        try
        {
            if (File.Exists(p) && (File.GetAttributes(p) & FileAttributes.System) != 0 && Path.GetDirectoryName(p)?.Length <= 3)
                return "it's a Windows system file";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Sends files or folders to the Recycle Bin with Windows' progress dialog. Windows warns before
    /// anything too big for the Recycle Bin would be permanently deleted. Returns false if aborted.
    /// </summary>
    public static bool Recycle(IntPtr owner, IEnumerable<string> paths)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join('\0', paths) + '\0',
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_WANTNUKEWARNING),
        };
        return Native.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
