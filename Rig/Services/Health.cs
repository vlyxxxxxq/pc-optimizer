using System.Windows;
using System.Windows.Media;

namespace Rig.Services;

public enum Severity { Good, Unknown, Warn, Bad }

/// <summary>One line of the health report. Action is a page key the main window navigates to.</summary>
public sealed record Finding(Severity Severity, string Title, string Detail, string? ActionLabel = null, string? Action = null)
{
    public Brush Brush => (Brush)Application.Current.Resources[Severity switch
    {
        Severity.Bad => "DangerBrush",
        Severity.Warn => "WarnBrush",
        Severity.Unknown => "MutedBrush",
        _ => "GoodBrush",
    }];

    public string Glyph => Severity switch { Severity.Good => "\uE73E", Severity.Unknown => "\uE9CE", _ => "\uE7BA" }; // check / unknown / warning
    public bool HasAction => ActionLabel is not null;
}

public sealed record HealthReport(int Score, List<Finding> Findings)
{
    public DateTime Checked { get; init; } = DateTime.Now;
    /// <summary>What the score is (and isn't) based on.</summary>
    public const string Basis = "Based on junk files, startup apps, free disk space, memory in use right now and graphics driver age. It's a maintenance checklist, not a speed test.";
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

    /// <summary>junkBytes/startupEnabled below 0 mean that check didn't run or failed, which is reported, not scored.</summary>
    public static HealthReport Evaluate(long junkBytes, int startupEnabled)
    {
        int score = 100;
        var findings = new List<Finding>();

        if (junkBytes < 0)
            findings.Add(new(Severity.Unknown, "Junk files weren't checked", "The scan didn't finish. Open Junk Cleaner to scan again.", "Open", "clean"));
        else if (junkBytes >= 200 * MB)
        {
            score -= (int)Math.Min(30, 5 + 5.0 * junkBytes / GB);
            findings.Add(new(junkBytes >= GB ? Severity.Bad : Severity.Warn,
                $"{Fmt.Bytes(junkBytes)} of junk files",
                "Review temporary files and caches before choosing what to remove.", "Review cleanup", "clean"));
        }
        else
        {
            findings.Add(new(Severity.Good, "Junk files are under control", "Less than 200 MB of removable junk."));
        }

        if (startupEnabled < 0)
            findings.Add(new(Severity.Unknown, "Startup apps weren't checked", "Couldn't read the startup list.", "Open", "startup"));
        else if (startupEnabled > 6)
        {
            score -= Math.Min(20, 4 + (startupEnabled - 6) * 3);
            findings.Add(new(startupEnabled > 12 ? Severity.Bad : Severity.Warn,
                $"{startupEnabled} apps launch at sign-in",
                "Review what starts at sign-in and keep the apps you need.", "Review startup", "startup"));
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
                findings.Add(new(Severity.Warn, $"Memory is {load:0}% used right now",
                    "A moment-in-time reading: closing programs you aren't using frees it up.", "View processes", "processes"));
            }
        }

        try
        {
            if (GpuInfo.Load().FirstOrDefault() is { IsOld: true } gpu)
            {
                score -= 5;
                findings.Add(new(Severity.Warn, $"{gpu.Vendor} graphics driver is {gpu.AgeDays / 30} months old",
                    $"{gpu.Name}. Newer drivers fix bugs and often help with new games.", "Get driver", "drivers"));
            }
        }
        catch
        {
            findings.Add(new(Severity.Unknown, "Graphics driver wasn't checked", "Windows didn't report the driver's date."));
        }

        return new HealthReport(Math.Clamp(score, 0, 100),
            findings.OrderByDescending(f => f.Severity).ToList());
    }
}
