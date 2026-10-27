using System.Windows;
using System.Windows.Media;

namespace Rig.Services;

public enum Severity { Good, Warn, Bad }

/// <summary>One line of the health report. Action is a page key the main window navigates to.</summary>
public sealed record Finding(Severity Severity, string Title, string Detail, string? ActionLabel = null, string? Action = null)
{
    public Brush Brush => (Brush)Application.Current.Resources[Severity switch
    {
        Severity.Bad => "DangerBrush",
        Severity.Warn => "WarnBrush",
        _ => "GoodBrush",
    }];

    public string Glyph => Severity == Severity.Good ? "" : ""; // checkmark / warning
    public bool HasAction => ActionLabel is not null;
}

public sealed record HealthReport(int Score, List<Finding> Findings)
{
    public string Label => Score switch
    {
        >= 85 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        _ => "Needs attention",
    };

    public Brush Brush => (Brush)Application.Current.Resources[
        Score >= 70 ? "GoodBrush" : Score >= 50 ? "WarnBrush" : "DangerBrush"];
}

public static class Health
{
    const long MB = 1024 * 1024, GB = 1024 * MB;

    public static HealthReport Evaluate(long junkBytes, int startupEnabled)
    {
        int score = 100;
        var findings = new List<Finding>();

        if (junkBytes >= 200 * MB)
        {
            score -= (int)Math.Min(30, 5 + 5.0 * junkBytes / GB);
            findings.Add(new(junkBytes >= GB ? Severity.Bad : Severity.Warn,
                $"{Fmt.Bytes(junkBytes)} of junk files",
                "Temporary files and caches that can be safely removed.", "Clean now", "clean"));
        }
        else
        {
            findings.Add(new(Severity.Good, "Junk files are under control", "Less than 200 MB of removable junk."));
        }

        if (startupEnabled > 6)
        {
            score -= Math.Min(20, 4 + (startupEnabled - 6) * 3);
            findings.Add(new(startupEnabled > 12 ? Severity.Bad : Severity.Warn,
                $"{startupEnabled} apps launch at sign-in",
                "Each one slows down startup. Disable the ones you don't need right away.", "Review", "startup"));
        }
        else
        {
            findings.Add(new(Severity.Good, $"{startupEnabled} startup apps", "A lean startup list. Nice."));
        }

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed || d.TotalSize == 0) continue;
                double used = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                string name = d.Name.TrimEnd('\\');
                if (used >= 80)
                {
                    score -= used >= 90 ? 20 : 10;
                    findings.Add(new(used >= 90 ? Severity.Bad : Severity.Warn,
                        $"Drive {name} is {used:0}% full",
                        $"Only {Fmt.Bytes(d.TotalFreeSpace)} free. Low space slows Windows and blocks updates.",
                        "Find large files", "disk"));
                }
            }
            catch { }
        }

        var (total, available) = SystemInfo.Memory();
        if (total > 0)
        {
            double load = 100.0 * (total - available) / total;
            if (load >= 85)
            {
                score -= 10;
                findings.Add(new(Severity.Warn, $"Memory is {load:0}% used",
                    "Closing programs you aren't using will make things snappier.", "View processes", "processes"));
            }
        }

        try
        {
            if (GpuInfo.Load().FirstOrDefault() is { IsOld: true } gpu)
            {
                score -= 5;
                findings.Add(new(Severity.Warn, $"{gpu.Vendor} graphics driver is {gpu.AgeDays / 30} months old",
                    $"{gpu.Name}. Newer drivers often improve FPS and fix crashes in new games.", "Get driver", "drivers"));
            }
        }
        catch { }

        return new HealthReport(Math.Clamp(score, 0, 100),
            findings.OrderByDescending(f => f.Severity).ToList());
    }
}
