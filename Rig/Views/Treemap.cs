using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// WinDirStat-style squarified treemap of a <see cref="DiskNode"/> tree, drawn directly with a
/// DrawingContext so tens of thousands of blocks stay fast. Each top-level folder gets its own hue;
/// files sitting loose in a folder are one grey block. Click selects, hover reports the path.
/// </summary>
public sealed class Treemap : FrameworkElement
{
    const double MinSide = 3; // blocks smaller than this aren't subdivided further

    static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0x8C, 0xFF), Color.FromRgb(0x3D, 0xD6, 0x8C), Color.FromRgb(0xF5, 0xA5, 0x24),
        Color.FromRgb(0xF2, 0x5F, 0x5C), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x22, 0xD3, 0xEE),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0xFB, 0x92, 0x3C),
        Color.FromRgb(0x94, 0xA3, 0xB8),
    ];
    static readonly Color LooseColor = Color.FromRgb(0x5B, 0x63, 0x77);

    readonly List<(Rect Rect, DiskNode Node, Color Color, int Depth)> _blocks = [];
    readonly Dictionary<Color, Brush> _brushes = [];
    readonly Pen _edge = new((Brush)System.Windows.Application.Current.Resources["BgBrush"], 0.6);
    readonly Pen _highlight = new(Brushes.White, 2);
    readonly Typeface _font = new("Segoe UI");
    bool _dirty = true;

    DiskNode? _root, _selected;
    public DiskNode? Root { get => _root; set { _root = value; Invalidate(); } }
    public DiskNode? Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }

    /// <summary>Raised with the node under the mouse (null when leaving).</summary>
    public event Action<DiskNode?>? Hovered;
    public event Action<DiskNode>? Clicked;

    public Treemap()
    {
        _edge.Freeze();
        _highlight.Freeze();
        ClipToBounds = true;
        SizeChanged += (_, _) => Invalidate();
    }

    /// <summary>Re-run the layout (after a scan or a delete).</summary>
    public void Invalidate()
    {
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle((Brush)System.Windows.Application.Current.Resources["InputBrush"], null, bounds);
        if (_root is null || _root.Size <= 0 || bounds.Width < 10 || bounds.Height < 10) return;

        if (_dirty)
        {
            _blocks.Clear();
            LayoutFolder(_root, bounds, null, 0);
            _dirty = false;
        }

        foreach (var (rect, node, color, depth) in _blocks)
        {
            dc.DrawRectangle(BrushFor(color), _edge, rect);
            // Label folders that are big enough to read.
            if (depth == 1 && rect.Width > 70 && rect.Height > 22)
            {
                var text = new FormattedText($"{node.Name}  {Fmt.Bytes(node.Size)}", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _font, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = Math.Max(1, rect.Width - 8),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(rect.X + 4, rect.Y + 3));
            }
        }

        if (_selected is not null)
        {
            // A folder is its own block; a file is drawn inside its folder's "loose files" block.
            foreach (var b in _blocks)
                if (_selected.IsDirectory ? ReferenceEquals(b.Node, _selected) && b.Depth >= 0
                                          : ReferenceEquals(b.Node, _selected.Parent) && b.Depth < 0)
                {
                    dc.DrawRectangle(null, _highlight, b.Rect);
                    break;
                }
        }
    }

    /// <summary>Lays out a folder's children inside rect. hue is fixed by the top-level ancestor.</summary>
    void LayoutFolder(DiskNode folder, Rect rect, Color? hue, int depth)
    {
        var items = new List<(DiskNode Node, double Size, bool Loose)>();
        if (folder.Folders is { } fs) items.AddRange(fs.Where(f => f.Size > 0).Select(f => (f, (double)f.Size, false)));
        if (folder.LooseSize > 0) items.Add((folder, folder.LooseSize, true));
        if (items.Count == 0) return;

        int index = 0;
        foreach (var (r, item) in Squarify(items, rect))
        {
            var color = hue ?? Palette[index++ % Palette.Length];
            if (item.Loose)
            {
                // Loose files: depth -1 marks "the files of folder" for hit-testing/highlighting.
                // Tinted with the folder's hue (muted) so they read as part of it.
                var tint = hue is { } h ? Mix(Shade(h, depth + 1), LooseColor, 0.45) : LooseColor;
                _blocks.Add((r, folder, tint, -1));
                continue;
            }
            var shade = Shade(color, depth);
            _blocks.Add((r, item.Node, shade, depth + 1));
            if (r.Width > MinSide * 2 && r.Height > MinSide * 2)
            {
                var inner = r;
                inner.Inflate(-1, -1);
                if (depth == 0 && inner.Height > 30) { inner.Y += 16; inner.Height -= 16; } // room for the label
                LayoutFolder(item.Node, inner, color, depth + 1);
            }
        }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Shade(Color c, int depth)
    {
        double f = Math.Max(0.45, 1.0 - depth * 0.12);
        return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }

    /// <summary>Cushion look: a soft highlight top-left fading to a darker edge.</summary>
    Brush BrushFor(Color c)
    {
        if (_brushes.TryGetValue(c, out var b)) return b;
        var light = Color.FromRgb((byte)Math.Min(255, c.R + 40), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40));
        var dark = Color.FromRgb((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));
        var brush = new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return _brushes[c] = brush;
    }

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rows that keep blocks close to square.</summary>
    static List<(Rect, (DiskNode Node, double Size, bool Loose))> Squarify(
        List<(DiskNode Node, double Size, bool Loose)> items, Rect bounds)
    {
        var result = new List<(Rect, (DiskNode, double, bool))>();
        double total = items.Sum(i => i.Size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        double scale = bounds.Width * bounds.Height / total;
        var sorted = items.OrderByDescending(i => i.Size).ToList();
        var areas = sorted.Select(i => i.Size * scale).ToList();
        var rect = bounds;
        int start = 0;

        while (start < sorted.Count && rect.Width > 0.5 && rect.Height > 0.5)
        {
            double side = Math.Min(rect.Width, rect.Height);
            int end = start;
            double sum = 0, worst = double.MaxValue;
            while (end < sorted.Count)
            {
                double newSum = sum + areas[end];
                double w = Worst(areas, start, end, newSum, side);
                if (end > start && w > worst) break;
                worst = w;
                sum = newSum;
                end++;
            }

            double thickness = sum / side;
            double offset = 0;
            bool column = rect.Width >= rect.Height; // shorter side is the height: stack a column on the left
            for (int i = start; i < end; i++)
            {
                double length = areas[i] / thickness;
                var r = column
                    ? new Rect(rect.X, rect.Y + offset, thickness, length)
                    : new Rect(rect.X + offset, rect.Y, length, thickness);
                result.Add((r, sorted[i]));
                offset += length;
            }
            rect = column
                ? new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            start = end;
        }
        return result;
    }

    static double Worst(List<double> areas, int start, int endInclusive, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i <= endInclusive; i++) { max = Math.Max(max, areas[i]); min = Math.Min(min, areas[i]); }
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    DiskNode? HitTest(Point p)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--) // last drawn = deepest
            if (_blocks[i].Rect.Contains(p)) return _blocks[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e) => Hovered?.Invoke(HitTest(e.GetPosition(this)));
    protected override void OnMouseLeave(MouseEventArgs e) => Hovered?.Invoke(null);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is { } node) Clicked?.Invoke(node);
    }
}
