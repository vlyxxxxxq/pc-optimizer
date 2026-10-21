using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public sealed record DriveRow(string Name, string Label, string Detail, double Percent, Brush Brush);

public partial class DashboardView : UserControl
{
    const int HistoryLength = 60;

    readonly CpuSampler _cpu = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Queue<double> _cpuHistory = new(), _memHistory = new();
    int _tick;

    /// <summary>Raised with a page key ("clean", "startup", "disk", "processes") when a finding's button is clicked.</summary>
    public event EventHandler<string>? ActionRequested;
    public event EventHandler? RecheckRequested;

    public DashboardView()
    {
        InitializeComponent();
        OsText.Text = SystemInfo.OsName();
        CpuNameText.Text = SystemInfo.CpuName();
        CoresText.Text = $"{Environment.ProcessorCount} logical processors";

        _cpu.Sample(); // prime the delta
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { Update(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void ShowChecking()
    {
        RecheckButton.IsEnabled = false;
        ScoreText.Text = "…";
        ScoreLabel.Text = "Checking";
        HealthSummary.Text = "Checking for junk files, startup load and disk space…";
        ScoreArc.Data = null;
    }

    public void ShowHealth(HealthReport report)
    {
        RecheckButton.IsEnabled = true;
        ScoreText.Text = report.Score.ToString();
        ScoreLabel.Text = report.Label;
        ScoreArc.Stroke = report.Brush;
        ScoreArc.Data = Arc(report.Score / 100.0, 140, 12);
        int issues = report.Findings.Count(f => f.Severity != Severity.Good);
        HealthSummary.Text = issues == 0
            ? "Everything looks great. Nothing needs your attention."
            : $"{issues} {(issues == 1 ? "thing" : "things")} could make your PC faster.";
        FindingsList.ItemsSource = report.Findings;
    }

    /// <summary>A clockwise ring segment starting at 12 o'clock, sized to sit on the track ellipse.</summary>
    static Geometry Arc(double fraction, double size, double thickness)
    {
        if (fraction <= 0) return Geometry.Empty;
        double r = (size - thickness) / 2, c = size / 2;
        double angle = Math.Min(fraction, 0.9999) * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    void Update()
    {
        double cpu = _cpu.Sample();
        CpuText.Text = $"{cpu:0}%";
        Push(_cpuHistory, cpu);
        Draw(CpuLine, CpuFill, CpuChart, _cpuHistory);

        var (total, available) = SystemInfo.Memory();
        if (total > 0)
        {
            double used = total - available;
            double pct = used / total * 100;
            MemText.Text = $"{pct:0}%";
            MemDetail.Text = $"{Fmt.Bytes(used)} of {Fmt.Bytes(total)} in use";
            Push(_memHistory, pct);
            Draw(MemLine, MemFill, MemChart, _memHistory);
        }

        UptimeText.Text = Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));

        if (_tick++ % 10 == 0) RefreshDrives();
    }

    void RefreshDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed || d.TotalSize == 0) continue;
                double pct = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize * 100;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                rows.Add(new DriveRow(d.Name.TrimEnd('\\'), label,
                    $"{Fmt.Bytes(d.TotalFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", pct, Fmt.LoadBrush(pct)));
            }
            catch { /* drive went away */ }
        }
        DrivesList.ItemsSource = rows;
    }

    static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
    }

    static void Draw(Polyline line, Polygon fill, Canvas canvas, Queue<double> history)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0 || history.Count == 0) return;
        double step = w / (HistoryLength - 1);
        int offset = HistoryLength - history.Count; // right-align so new samples enter from the right
        var points = new PointCollection(history.Count);
        int i = 0;
        foreach (var v in history)
            points.Add(new Point((offset + i++) * step, h - 1 - v / 100 * (h - 2)));
        line.Points = points;

        var area = new PointCollection(points) { new Point(points[^1].X, h), new Point(points[0].X, h) };
        fill.Points = area;
    }

    void Finding_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string action) ActionRequested?.Invoke(this, action);
    }

    void Recheck_Click(object sender, RoutedEventArgs e) => RecheckRequested?.Invoke(this, EventArgs.Empty);
}
