using System.Drawing;
using System.Drawing.Imaging;
using Xunit.Abstractions;
using Rig.Services;

namespace Rig.Tests;

/// <summary>Similar Photos on a labelled set of generated pictures (label = which "scene" a file really is).</summary>
public class PhotoTests(ITestOutputHelper output)
{
    static void Scene(string path, int seed, int w = 800, int h = 600, Action<Graphics>? extra = null, int brighten = 0, ImageFormat? format = null)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            var rnd = new Random(seed);
            g.Clear(Color.FromArgb(rnd.Next(40, 200), rnd.Next(40, 200), rnd.Next(40, 200)));
            g.ScaleTransform(w / 800f, h / 600f);
            for (int i = 0; i < 14; i++)
            {
                using var b = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
                g.FillEllipse(b, rnd.Next(-100, 700), rnd.Next(-100, 500), rnd.Next(80, 400), rnd.Next(80, 300));
            }
            extra?.Invoke(g);
        }
        if (brighten != 0)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    bmp.SetPixel(x, y, Color.FromArgb(Math.Min(255, c.R + brighten), Math.Min(255, c.G + brighten), Math.Min(255, c.B + brighten)));
                }
        bmp.Save(path, format ?? ImageFormat.Png);
    }

    static void Flat(string path, Color c) { using var b = new Bitmap(300, 300); using (var g = Graphics.FromImage(b)) g.Clear(c); b.Save(path, ImageFormat.Png); }

    static void Gradient(string path, Color c)
    {
        using var b = new Bitmap(400, 300);
        using (var g = Graphics.FromImage(b))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 400, 300), c, Color.Black, 0f))
            g.FillRectangle(brush, 0, 0, 400, 300);
        b.Save(path, ImageFormat.Png);
    }

    [Fact]
    public void Flat_colours_and_gradients_never_match_each_other()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        Flat(Path.Combine(d, "red.png"), Color.Red);
        Flat(Path.Combine(d, "green.png"), Color.Lime);
        Flat(Path.Combine(d, "blue.png"), Color.Blue);
        Gradient(Path.Combine(d, "red-grad.png"), Color.Red);
        Gradient(Path.Combine(d, "blue-grad.png"), Color.Blue);
        var scan = SimilarPhotos.Find(d, false, null, default);
        Assert.Empty(scan.Groups);
        Assert.Equal(5, scan.Scanned);
    }

    [Fact]
    public void Exact_copies_are_ticked_keeping_one_and_look_alikes_are_never_ticked()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        Scene(Path.Combine(d, "a.png"), 1);
        File.Copy(Path.Combine(d, "a.png"), Path.Combine(d, "a copy.png"));
        Scene(Path.Combine(d, "b.png"), 2);
        Scene(Path.Combine(d, "b small.png"), 2, 400, 300);
        var scan = SimilarPhotos.Find(d, false, null, default);
        var exact = Assert.Single(scan.Groups, g => g.Exact);
        Assert.Equal(1, exact.Photos.Count(p => p.Remove));
        Assert.Single(exact.Photos, p => p.Keeper && !p.Remove);
        var similar = Assert.Single(scan.Groups, g => !g.Exact);
        Assert.DoesNotContain(similar.Photos, p => p.Remove);
        Assert.Equal("b.png", similar.Photos.Single(p => p.Keeper).Name);
    }

    [Fact]
    public void Every_look_alike_matches_its_keeper_directly()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        // A chain: each step is a little brighter. Neighbours look alike; the ends don't.
        Scene(Path.Combine(d, "chain-0.png"), 3, 400, 300);
        Scene(Path.Combine(d, "chain-1.png"), 3, 400, 300, brighten: 10);
        Scene(Path.Combine(d, "chain-2.png"), 3, 400, 300, brighten: 20);
        Scene(Path.Combine(d, "chain-3.png"), 3, 400, 300, brighten: 30);
        var scan = SimilarPhotos.Find(d, false, null, default);
        var ends = scan.Groups.SelectMany(g => g.Photos).ToList();
        foreach (var g in scan.Groups.Where(g => !g.Exact))
        {
            var keeper = g.Photos.Single(p => p.Keeper);
            foreach (var p in g.Photos.Where(p => p != keeper)) Assert.NotNull(SimilarPhotos.Match(keeper, p));
        }
        Assert.False(scan.Groups.Any(g => g.Photos.Any(p => p.Name == "chain-0.png") && g.Photos.Any(p => p.Name == "chain-3.png")),
            "the two ends of the chain were put together");
    }

    [Fact]
    public void Unreadable_files_are_reported_not_hidden()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        File.WriteAllText(Path.Combine(d, "broken.jpg"), "not a picture");
        Scene(Path.Combine(d, "ok.png"), 4);
        var scan = SimilarPhotos.Find(d, false, null, default);
        Assert.Equal(1, scan.Scanned);
        Assert.Single(scan.Unreadable);
    }

    [Fact]
    public void Cancel_stops_the_scan()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        for (int i = 0; i < 5; i++) Scene(Path.Combine(d, $"{i}.png"), i, 200, 150);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SimilarPhotos.Find(d, false, null, cts.Token));
    }

    /// <summary>Precision on a labelled set: every pair the finder puts together must really be the same scene.</summary>
    [Fact]
    public void Labelled_fixture_precision()
    {
        using var box = new Sandbox();
        var d = box.Dir("p");
        var label = new Dictionary<string, string>();
        void Add(string name, string scene, Action<string> make) { make(Path.Combine(d, name)); label[name] = scene; }

        for (int s = 10; s < 16; s++)
        {
            int seed = s;
            Add($"s{s}.png", $"scene{s}", p => Scene(p, seed));
            Add($"s{s}-small.png", $"scene{s}", p => Scene(p, seed, 400, 300));
            Add($"s{s}-q.jpg", $"scene{s}", p => Scene(p, seed, format: ImageFormat.Jpeg));
        }
        Add("s10-copy.png", "scene10", p => File.Copy(Path.Combine(d, "s10.png"), p));
        // Same scene with an obvious change: a different photo that should be kept.
        Add("s11-edited.png", "scene11-edited", p => Scene(p, 11, extra: g => { using var b = new SolidBrush(Color.White); g.FillRectangle(b, 0, 0, 400, 600); }));
        // Rotated pixels (not an EXIF flag): a different framing.
        Add("s12-rotated.png", "scene12-rotated", p => { Scene(p, 12); using var img = Image.FromFile(p); using var bmp = new Bitmap(img); bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); img.Dispose(); bmp.Save(p + ".tmp.png"); File.Move(p + ".tmp.png", p, true); });
        for (int s = 20; s < 26; s++) { int seed = s; Add($"other{s}.png", $"other{s}", p => Scene(p, seed)); }
        Add("flat-red.png", "flat-red", p => Flat(p, Color.Red));
        Add("flat-blue.png", "flat-blue", p => Flat(p, Color.Blue));

        var scan = SimilarPhotos.Find(d, false, null, default);
        var pairs = scan.Groups.SelectMany(g => g.Photos.Where(p => !p.Keeper).Select(p => (g.Photos.Single(k => k.Keeper).Name, p.Name))).ToList();
        int correct = pairs.Count(p => label[p.Item1] == label[p.Item2]);
        int expectedPairs = 6 * 2 + 1; // each scene's two variants, plus the exact copy
        double precision = pairs.Count == 0 ? 1 : (double)correct / pairs.Count, recall = (double)correct / expectedPairs;
        output.WriteLine($"{label.Count} files, {scan.Groups.Count} groups, {pairs.Count} pairs: precision {precision:P0}, recall {recall:P0}");
        foreach (var (a, b) in pairs) output.WriteLine($"  {a} ~ {b}{(label[a] == label[b] ? "" : "   <-- WRONG")}");
        Assert.Equal(1.0, precision);
        Assert.True(recall >= 0.8, $"recall {recall:P0}");
    }
}
