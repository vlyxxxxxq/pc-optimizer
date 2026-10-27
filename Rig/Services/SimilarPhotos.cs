using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rig.Services;

public sealed class PhotoItem : ObservableObject
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTime Modified { get; init; }
    public ulong Hash { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Width} × {Height} · {Fmt.Bytes(Size)} · {Modified:d}";
    bool _remove;
    public bool Remove { get => _remove; set => Set(ref _remove, value); }
    ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
}

public sealed class PhotoGroup
{
    public required List<PhotoItem> Photos { get; init; }
    public string Title => $"{Photos.Count} similar photos · {Fmt.Bytes(Photos.Where(p => p.Remove).Sum(p => p.Size))} to free";
}

/// <summary>
/// Finds photos that look the same even when they aren't identical files (resized, re-saved, screenshots of the
/// same thing): each image is shrunk to 9×8 grey pixels and compared by which neighbours are brighter (a "dHash").
/// The best copy of each group (most pixels, then biggest file) is kept; the rest are ticked for the Recycle Bin.
/// </summary>
public static class SimilarPhotos
{
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic"];
    const int MaxDistance = 6; // of 64 bits

    public static List<PhotoGroup> Find(string folder, bool subfolders, IProgress<string>? progress, CancellationToken cancel)
    {
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = subfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        }).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();

        var photos = new List<PhotoItem>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, file =>
        {
            if (Read(file) is { } p) lock (photos) photos.Add(p);
            if (Interlocked.Increment(ref done) % 25 == 0) progress?.Report($"Looking at photos… {done} of {files.Count}");
        });

        // Group anything within a few bits of another photo (union-find, so A~B and B~C end up together).
        var parent = Enumerable.Range(0, photos.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < photos.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int j = i + 1; j < photos.Count; j++)
                if (BitOperations.PopCount(photos[i].Hash ^ photos[j].Hash) <= MaxDistance && SameShape(photos[i], photos[j]))
                    parent[Root(j)] = Root(i);
        }

        var groups = photos.Select((p, i) => (p, i)).GroupBy(x => Root(x.i)).Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.p).OrderByDescending(p => (long)p.Width * p.Height).ThenByDescending(p => p.Size).ToList())
            .Select(list => { foreach (var p in list.Skip(1)) p.Remove = true; return new PhotoGroup { Photos = list }; })
            .OrderByDescending(g => g.Photos.Skip(1).Sum(p => p.Size)).ToList();
        return groups;
    }

    /// <summary>Very different proportions (a crop, a panorama) aren't the same photo even if the hash is close.</summary>
    static bool SameShape(PhotoItem a, PhotoItem b)
    {
        double ra = (double)a.Width / Math.Max(1, a.Height), rb = (double)b.Width / Math.Max(1, b.Height);
        return Math.Abs(ra - rb) / Math.Max(ra, rb) < 0.1;
    }

    static PhotoItem? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            int width, height;
            using (var stream = File.OpenRead(file))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth; height = frame.PixelHeight;
            }
            var small = new BitmapImage();
            small.BeginInit();
            small.UriSource = new Uri(file);
            small.CacheOption = BitmapCacheOption.OnLoad;
            small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            small.DecodePixelWidth = 9;
            small.DecodePixelHeight = 8;
            small.EndInit();
            small.Freeze();
            var grey = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            var px = new byte[9 * 8];
            grey.CopyPixels(px, 9, 0);
            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++, bit++)
                    if (px[y * 9 + x] > px[y * 9 + x + 1]) hash |= 1UL << bit;
            return new PhotoItem { Path = file, Size = info.Length, Width = width, Height = height, Modified = info.LastWriteTime, Hash = hash };
        }
        catch { return null; } // unsupported format or damaged file
    }
}
