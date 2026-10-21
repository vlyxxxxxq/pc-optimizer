using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace PCOptimizer.Services;

public sealed class InstalledApp : ObservableObject
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime? InstalledOn { get; init; }
    public string UninstallCommand { get; init; } = "";
    public string? InstallLocation { get; init; }
    public string? IconPath { get; init; }
    /// <summary>Registry key the entry lives in; gone after a successful uninstall.</summary>
    public string RegistryKey { get; init; } = "";
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }

    // Store apps
    public string? PackageFullName { get; init; }
    public string Why { get; init; } = "";

    public ImageSource? Icon { get; set; }
    public string SizeText => SizeBytes > 0 ? Fmt.Bytes(SizeBytes) : "—";
    public string InstalledText => InstalledOn?.ToString("d") ?? "—";
    public string Subtitle => Publisher.Length > 0 ? $"{Publisher}{(Version.Length > 0 ? " · " + Version : "")}" : Version;

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
}

public static class InstalledApps
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Desktop programs from the same registry keys Settings → Apps reads.</summary>
    public static List<InstalledApp> LoadPrograms()
    {
        var apps = new List<InstalledApp>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(sub);
                        if (k is null || !IsUserFacing(k)) continue;
                        apps.Add(new InstalledApp
                        {
                            Name = ((string)k.GetValue("DisplayName")!).Trim(),
                            Publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "",
                            Version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                            SizeBytes = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                            InstalledOn = ParseDate(k.GetValue("InstallDate") as string),
                            UninstallCommand = (string)k.GetValue("UninstallString")!,
                            InstallLocation = k.GetValue("InstallLocation") as string,
                            IconPath = IconFile(k.GetValue("DisplayIcon") as string),
                            RegistryKey = $@"{UninstallKey}\{sub}",
                            Hive = hive,
                            View = view,
                        });
                    }
                    catch { /* malformed entry */ }
                }
            }
            catch { }
        }

        // The same program can be registered in more than one place.
        return apps
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool IsUserFacing(RegistryKey k)
    {
        if (k.GetValue("DisplayName") is not string name || name.Trim().Length == 0) return false;
        if (k.GetValue("UninstallString") is not string u || u.Trim().Length == 0) return false;
        if (k.GetValue("SystemComponent") is int sc && sc == 1) return false;
        if (k.GetValue("ParentKeyName") is string) return false; // updates and patches of another product
        var release = k.GetValue("ReleaseType") as string ?? "";
        return release is not ("Update" or "Hotfix" or "Security Update");
    }

    static DateTime? ParseDate(string? s) =>
        s is { Length: 8 } && DateTime.TryParseExact(s, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
            ? d : null;

    static string? IconFile(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var path = displayIcon.Split(',')[0].Trim().Trim('"');
        return File.Exists(path) ? path : null;
    }

    public static void LoadIcons(IEnumerable<InstalledApp> apps)
    {
        foreach (var a in apps) a.Icon = FileIcons.For(a.IconPath);
    }

    public static bool StillInstalled(InstalledApp app)
    {
        using var root = RegistryKey.OpenBaseKey(app.Hive, app.View);
        using var k = root.OpenSubKey(app.RegistryKey);
        return k is not null;
    }

    /// <summary>
    /// Runs the program's own uninstaller (with its normal UI, so the user stays in control).
    /// Returns once it exits; some uninstallers hand off to a copy of themselves, so check
    /// <see cref="StillInstalled"/> afterwards.
    /// </summary>
    public static async Task UninstallAsync(InstalledApp app)
    {
        var (file, args) = SplitCommand(Environment.ExpandEnvironmentVariables(app.UninstallCommand));
        // MSI entries often say /I (modify); /X is the actual uninstall.
        if (Path.GetFileName(file).Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
            args = args.Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })!;
        await p.WaitForExitAsync();
    }

    static (string File, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim()) : (cmd, "");
    }

    // ---- Microsoft Store extras ----

    /// <summary>Preinstalled Store apps that most people never use. Only ones actually installed are shown.</summary>
    static readonly (string Package, string Name, string Why)[] StoreExtras =
    [
        ("Clipchamp.Clipchamp", "Clipchamp", "Video editor that ships with Windows 11."),
        ("Microsoft.BingNews", "Microsoft News", "News feed app."),
        ("Microsoft.BingWeather", "Weather", "Weather app (Widgets still show weather)."),
        ("Microsoft.BingSearch", "Bing Search", "Web search integration."),
        ("Microsoft.GetHelp", "Get Help", "Microsoft support chat."),
        ("Microsoft.Getstarted", "Tips", "Windows tips and tutorials."),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Ad-supported card games."),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", "Launcher that advertises Microsoft 365."),
        ("Microsoft.People", "People", "Contacts app."),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", "Business automation tool."),
        ("Microsoft.Todos", "Microsoft To Do", "Task list app."),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Sends feedback to Microsoft."),
        ("Microsoft.WindowsMaps", "Maps", "Offline maps app."),
        ("Microsoft.ZuneVideo", "Movies & TV", "Old video player and store."),
        ("Microsoft.YourPhone", "Phone Link", "Connects an Android phone or iPhone; runs in the background."),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", "Remote help tool (also abused by scammers)."),
        ("Microsoft.549981C3F5F10", "Cortana", "Discontinued assistant."),
        ("Microsoft.Windows.DevHome", "Dev Home", "Developer dashboard (retired by Microsoft)."),
        ("MSTeams", "Microsoft Teams (free)", "Personal Teams; starts with Windows."),
        ("Microsoft.OutlookForWindows", "Outlook (new)", "Replacement for the Mail app."),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", "Desktop notes."),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", "Voice recorder."),
        ("Microsoft.WindowsAlarms", "Clock", "Alarms, timers and Focus sessions."),
        ("Microsoft.Copilot", "Copilot", "Microsoft's AI assistant app."),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", "For Windows Mixed Reality headsets only."),
        ("Microsoft.Microsoft3DViewer", "3D Viewer", "3D model viewer."),
        ("Microsoft.SkypeApp", "Skype", "Discontinued by Microsoft."),
    ];

    public static async Task<List<InstalledApp>> LoadStoreExtrasAsync()
    {
        var (_, output) = await Shell.RunAsync("powershell",
            "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "Get-AppxPackage -PackageTypeFilter Main | Select-Object Name, PackageFullName, Version | ConvertTo-Json -Compress\"",
            Encoding.UTF8);
        var installed = new Dictionary<string, (string Full, string Version)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items)
                installed[e.GetProperty("Name").GetString()!] =
                    (e.GetProperty("PackageFullName").GetString()!, e.GetProperty("Version").GetString() ?? "");
        }
        catch { return []; }

        return StoreExtras
            .Where(x => installed.ContainsKey(x.Package))
            .Select(x => new InstalledApp
            {
                Name = x.Name,
                Why = x.Why,
                Version = installed[x.Package].Version,
                PackageFullName = installed[x.Package].Full,
                Publisher = "Microsoft Store",
            })
            .ToList();
    }

    /// <summary>Removes a Store app for the current user. It can be reinstalled from the Store.</summary>
    public static async Task<(bool Ok, string Output)> RemoveStoreAppAsync(InstalledApp app)
    {
        var (code, output) = await Shell.RunAsync("powershell",
            $"-NoProfile -Command \"Remove-AppxPackage -Package '{app.PackageFullName}'\"", Encoding.UTF8);
        return (code == 0 && output.Trim().Length == 0, output.Trim());
    }
}
