using System.Diagnostics;
using System.Text.RegularExpressions;
using Rig.Services;

namespace Rig.Tests;

public class ConsistencyTests
{
    static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rig", "Rig.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir ?? throw new InvalidOperationException("repo not found"), .. parts]);
    }

    static readonly Dictionary<string, string> PageOf = new()
    {
        ["Game"] = "GameModeView", ["Library"] = "LibraryView", ["Crosshair"] = "CrosshairView", ["Controller"] = "ControllerView",
        ["Input"] = "InputStatsView", ["Clips"] = "ClipsView", ["Clipboard"] = "ClipboardView", ["ScreenTime"] = "ScreenTimeView",
        ["Layouts"] = "LayoutsView", ["Downloads"] = "DownloadsView", ["Disk"] = "DiskView", ["Cleaner"] = "CleanerView",
        ["Uninstall"] = "UninstallView", ["Startup"] = "StartupView", ["Processes"] = "ProcessesView", ["Tweaks"] = "TweaksView",
        ["Menu"] = "ContextMenuView", ["Apps"] = "AppsView", ["Hardware"] = "HardwareView", ["Network"] = "NetworkView",
        ["Security"] = "SecurityView", ["Tools"] = "ToolsView", ["Settings"] = "SettingsView", ["Timers"] = "TimersView",
        ["Renamer"] = "RenamerView", ["Photos"] = "PhotosView", ["ColorPicker"] = "ColorPickerView", ["Dashboard"] = "DashboardView",
    };

    [Fact]
    public void Every_indexed_setting_is_still_on_its_page()
    {
        var missing = new List<string>();
        foreach (var e in SettingsIndex.All)
        {
            Assert.Contains(Modules.All, m => m.Id == e.Module);
            var xaml = System.Net.WebUtility.HtmlDecode(File.ReadAllText(RepoFile("Rig", "Views", PageOf[e.Module] + ".xaml")));
            if (!xaml.Contains($"Text=\"{e.Title}\"")) missing.Add($"{e.Id} ({e.Title})");
        }
        Assert.True(missing.Count == 0, "Index entries no longer on their page: " + string.Join(", ", missing));
    }

    [Fact]
    public void Index_ids_are_unique()
    {
        var dupes = SettingsIndex.All.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void New_section_headings_are_indexed()
    {
        var indexed = SettingsIndex.All.Select(e => (e.Module, e.Title)).ToHashSet();
        var unindexed = new List<string>();
        foreach (var (module, page) in PageOf)
        {
            var xaml = File.ReadAllText(RepoFile("Rig", "Views", page + ".xaml"));
            foreach (Match m in Regex.Matches(xaml, @"<TextBlock\b([^>]*?)/?>"))
            {
                var a = m.Groups[1].Value;
                if (Regex.Match(a, "\\bText=\"([^\"{]+)\"") is not { Success: true } t) continue;
                var text = System.Net.WebUtility.HtmlDecode(t.Groups[1].Value).Trim();
                bool heading = (a.Contains("FontSize=\"15\"") && a.Contains("SemiBold")) || a.Contains("RowTitle");
                if (heading && text.Length is >= 4 and <= 48 && text != text.ToUpperInvariant() && !indexed.Contains((module, text)))
                    unindexed.Add($"{module}: {text}");
            }
        }
        Assert.True(unindexed.Count == 0, "Add to SettingsIndex (or regenerate it): " + string.Join("; ", unindexed));
    }

    [Fact]
    public async Task Process_runner_times_out_and_stops_the_program()
    {
        var sw = Stopwatch.StartNew();
        var (code, output) = await Shell.RunAsync("ping", ["-n", "30", "127.0.0.1"], timeout: TimeSpan.FromSeconds(1));
        Assert.Equal(Shell.NotRun, code);
        Assert.Contains("didn't finish in time", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Process_runner_reports_a_missing_program_instead_of_throwing()
    {
        var (code, output) = await Shell.RunAsync("definitely-not-a-program-rig", ["x"]);
        Assert.Equal(Shell.NotRun, code);
        Assert.Contains("Couldn't start", output);
    }

    [Fact]
    public async Task Process_runner_can_be_canceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var (code, output) = await Shell.RunAsync("ping", ["-n", "30", "127.0.0.1"], cancel: cts.Token);
        Assert.Equal(Shell.NotRun, code);
        Assert.Equal("Canceled.", output);
    }

    [Fact]
    public async Task Process_runner_passes_awkward_arguments_intact()
    {
        using var box = new Sandbox();
        var odd = Path.Combine(box.Dir("a folder with spaces & symbols"), "file (1).txt");
        File.WriteAllText(odd, "x");
        // "where /F" quotes what it finds; the point is the path arrives as one argument.
        var (code, output) = await Shell.RunAsync("cmd.exe", ["/c", "if", "exist", odd, "echo", "found"]);
        Assert.Equal(0, code);
        Assert.Contains("found", output);
    }
}
