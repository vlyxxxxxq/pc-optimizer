using System.Numerics;
using System.Security.Cryptography;
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
    /// <summary>64-bit difference hash of the upright picture.</summary>
    public ulong Hash { get; init; }
    /// <summary>16×16 grey thumbnail, for a second, finer comparison.</summary>
    public byte[] Grey { get; init; } = [];
    /// <summary>Average colour of each cell of a 4×4 grid (R,G,B × 16).</summary>
    public byte[] Colors { get; init; } = [];
    /// <summary>Too plain to compare by shape (a flat colour, a near-uniform background).</summary>
    public bool LowDetail { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Detail => $"{Width} × {Height} · {Format} · {Fmt.Bytes(Size)} · {Modified:d}";
    bool _remove;
    public bool Remove { get => _remove; set => Set(ref _remove, value); }
    bool _keeper;
    /// <summary>The copy that stays; at least one per group always does.</summary>
    public bool Keeper { get => _keeper; set => Set(ref _keeper, value); }
    ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
}

public sealed class PhotoGroup
{
    public required List<PhotoItem> Photos { get; init; }
    /// <summary>Byte-for-byte the same file; otherwise a "looks similar" suggestion.</summary>
    public required bool Exact { get; init; }
    public required string Why { get; init; }
    public string Kind => Exact ? "Exact copies" : "Looks similar · check before removing";
    public string Title => $"{Kind} · {Photos.Count} photos · {Fmt.Bytes(Photos.Where(p => p.Remove).Sum(p => p.Size))} ticked";
    public string KeeperText => Exact
        ? "These files are identical, so any one can stay. Suggested: the oldest copy."
        : "Suggested keeper: the one with the most pixels. Size and resolution don't prove which is the better photo; look before you tick.";
}

public sealed record PhotoScan(List<PhotoGroup> Groups, int Scanned, List<string> Unreadable);

/// <summary>
/// Finds exact duplicate photos (same bytes) and, separately, photos that look alike (resized or re-saved copies).
/// Exact copies are ticked for the Recycle Bin, keeping one. Look-alikes are only suggestions: nothing in them is
/// ticked, and each is compared directly with its group's suggested keeper, so a chain of near-matches (A≈B≈C)
/// never puts very different photos together. Plain pictures (flat colours, near-empty frames) are only ever
/// matched as exact copies. No similarity score can prove a photo is expendable.
/// </summary>
public static class SimilarPhotos
{
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic"];
    const int MaxHashDistance = 6;      // of 64 bits
    const double MaxGreyDifference = 12; // average of 0–255 per pixel on the 16×16 thumbnails
    const double MaxColorDifference = 18; // average per channel on the 4×4 colour grid
    const double MinDetail = 6;          // standard deviation of grey levels; below this a picture is "plain"

    public static PhotoScan Find(string folder, bool subfolders, IProgress<string>? progress, CancellationToken cancel)
    {
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = subfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                 }))
        {
            cancel.ThrowIfCancellationRequested();
            if (Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) files.Add(f);
        }

        var photos = new List<PhotoItem>();
        var unreadable = new List<string>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, file =>
        {
            var p = Read(file);
            lock (photos) { if (p is null) unreadable.Add(file); else photos.Add(p); }
            if (Interlocked.Increment(ref done) % 25 == 0) progress?.Report($"Looking at photos… {done} of {files.Count}");
        });

        var groups = new List<PhotoGroup>();
        var used = new HashSet<PhotoItem>();

        // 1. Exact copies: same size, then same full hash.
        progress?.Report("Checking for exact copies…");
        foreach (var sameSize in photos.GroupBy(p => p.Size).Where(g => g.Count() > 1))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var same in sameSize.GroupBy(p => FullHash(p.Path)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var list = same.OrderBy(p => p.Modified).ThenBy(p => p.Path.Length).ToList();
                list[0].Keeper = true;
                foreach (var p in list.Skip(1)) p.Remove = true;
                foreach (var p in list) used.Add(p);
                groups.Add(new PhotoGroup { Photos = list, Exact = true, Why = "Byte-for-byte identical files." });
            }
        }

        // 2. Look-alikes, only among detailed pictures. Candidates come from shared 8-bit slices of the hash
        //    (any two hashes within 7 bits share at least one slice), so most pairs are never compared.
        progress?.Report("Comparing pictures…");
        var pool = photos.Where(p => !used.Contains(p) && !p.LowDetail).OrderByDescending(p => (long)p.Width * p.Height).ThenByDescending(p => p.Size).ToList();
        var buckets = new Dictionary<(int, byte), List<PhotoItem>>();
        foreach (var p in pool)
            for (int band = 0; band < 8; band++)
            {
                var key = (band, (byte)(p.Hash >> (band * 8)));
                if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = [];
                b.Add(p);
            }
        foreach (var keeper in pool)
        {
            cancel.ThrowIfCancellationRequested();
            if (used.Contains(keeper)) continue;
            var candidates = new HashSet<PhotoItem>();
            for (int band = 0; band < 8; band++)
                foreach (var c in buckets[(band, (byte)(keeper.Hash >> (band * 8)))]) if (c != keeper && !used.Contains(c)) candidates.Add(c);
            // Every member must match the keeper itself, not just some other member.
            var matches = candidates.Select(c => (c, why: Match(keeper, c))).Where(x => x.why is not null).ToList();
            if (matches.Count == 0) continue;
            keeper.Keeper = true;
            used.Add(keeper);
            foreach (var (c, _) in matches) used.Add(c);
            var (_, firstWhy) = matches[0];
            groups.Add(new PhotoGroup { Photos = [keeper, .. matches.Select(m => m.c)], Exact = false, Why = firstWhy! });
        }
        return new PhotoScan(groups.OrderByDescending(g => g.Exact).ThenByDescending(g => g.Photos.Sum(p => p.Size)).ToList(), photos.Count, unreadable);
    }

    /// <summary>A plain-English reason the two look alike, or null if they don't (all three checks must agree).</summary>
    internal static string? Match(PhotoItem a, PhotoItem b)
    {
        int bits = BitOperations.PopCount(a.Hash ^ b.Hash);
        if (bits > MaxHashDistance || !SameShape(a, b)) return null;
        double grey = Average(a.Grey, b.Grey), color = Average(a.Colors, b.Colors);
        if (grey > MaxGreyDifference || color > MaxColorDifference) return null;
        return $"Same layout ({64 - bits} of 64 shape checks agree), similar detail (grey difference {grey:0.#}) and colours (difference {color:0.#}).";
    }

    static double Average(byte[] a, byte[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return double.MaxValue;
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    /// <summary>Very different proportions (a crop, a panorama) aren't the same photo.</summary>
    static bool SameShape(PhotoItem a, PhotoItem b)
    {
        double ra = (double)a.Width / Math.Max(1, a.Height), rb = (double)b.Width / Math.Max(1, b.Height);
        return Math.Abs(ra - rb) / Math.Max(ra, rb) < 0.1;
    }

    static string? FullHash(string path)
    {
        try { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
        catch { return null; }
    }

    internal static PhotoItem? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            int width, height, rotation;
            using (var stream = File.OpenRead(file))
            {
                // Header only: size and orientation without decoding the whole picture.
                var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                width = header.PixelWidth; height = header.PixelHeight;
                rotation = Orientation(header);
            }
            // A small copy is plenty for comparing, and keeps memory flat for large libraries.
            var small = new BitmapImage();
            small.BeginInit();
            small.UriSource = new Uri(file);
            small.CacheOption = BitmapCacheOption.OnLoad;
            small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            small.DecodePixelWidth = 64;
            small.EndInit();
            small.Freeze();
            // Upright, as a viewer shows it (EXIF orientation), so a rotated copy is compared the right way up.
            BitmapSource upright = rotation == 0 ? small : new TransformedBitmap(small, new RotateTransform(rotation));
            if (rotation is 90 or 270) (width, height) = (height, width);

            var grey9 = Pixels(upright, 9, 8, PixelFormats.Gray8);
            ulong hash = 0;
            for (int y = 0, bit = 0; y < 8; y++)
                for (int x = 0; x < 8; x++, bit++)
                    if (grey9[y * 9 + x] > grey9[y * 9 + x + 1]) hash |= 1UL << bit;

            var grey16 = Pixels(upright, 16, 16, PixelFormats.Gray8);
            var bgr = Pixels(upright, 4, 4, PixelFormats.Bgr24);
            double mean = grey16.Average(v => (double)v);
            double sd = Math.Sqrt(grey16.Average(v => (v - mean) * (v - mean)));
            return new PhotoItem
            {
                Path = file, Size = info.Length, Width = width, Height = height, Modified = info.LastWriteTime, Hash = hash,
                Grey = grey16, Colors = bgr, LowDetail = sd < MinDetail,
            };
        }
        catch { return null; } // unsupported format or damaged file: reported separately
    }

    static byte[] Pixels(BitmapSource src, int w, int h, PixelFormat format)
    {
        var scaled = new TransformedBitmap(src, new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, format, null, 0);
        int stride = (w * format.BitsPerPixel + 7) / 8;
        var px = new byte[stride * h];
        converted.CopyPixels(px, stride, 0);
        return px;
    }

    static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata m && m.GetQuery("System.Photo.Orientation") is ushort o)
                return o switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
        }
        catch { }
        return 0;
    }
}
