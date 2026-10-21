using System.Diagnostics;

namespace PCOptimizer.Services;

/// <summary>A known background app that Game Mode can close.</summary>
public sealed class CloseSuggestion : ObservableObject
{
    public required string Name { get; init; }
    public required string Why { get; init; }
    public required string[] Processes { get; init; }
    public bool Recommended { get; init; }
    public bool IsRunning { get; init; }

    /// <summary>Called when the user flips the switch.</summary>
    public Action<CloseSuggestion, bool>? Toggled { get; set; }

    bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set { if (Set(ref _isOn, value)) Toggled?.Invoke(this, value); }
    }

    /// <summary>Sets the switch without triggering Toggled (for syncing from settings).</summary>
    public void Sync(bool on) => Set(ref _isOn, on, nameof(IsOn));
}

/// <summary>An app with an open window right now, offered for one-click adding.</summary>
public sealed record OpenApp(string Process, string Display);

public static class BackgroundApps
{
    // Never list voice chat, overlays, launchers, RGB/peripheral software or browsers: closing those breaks games or loses work.
    static readonly (string Name, string Why, string[] Processes, bool Recommended)[] Known =
    [
        ("OneDrive", "Pauses file syncing that competes with your game for disk and bandwidth.", ["OneDrive"], true),
        ("Dropbox", "Pauses file syncing that competes for disk and bandwidth.", ["Dropbox"], true),
        ("Google Drive", "Pauses file syncing that competes for disk and bandwidth.", ["GoogleDriveFS"], true),
        ("iCloud", "Stops iCloud Drive and Photos syncing in the background.", ["iCloudDrive", "iCloudServices", "iCloudPhotos"], true),
        ("Adobe Creative Cloud", "Its helper services hold memory and constantly check for updates.",
            ["Creative Cloud", "CCXProcess", "CoreSync", "Adobe Desktop Service", "AdobeIPCBroker"], true),
        ("Microsoft Teams", "Holds hundreds of MB of memory even when idle.", ["ms-teams", "Teams"], true),
        ("Slack", "Keeps a full browser engine running in the background.", ["slack"], true),
        ("Zoom", "Background meeting client.", ["Zoom"], true),
        ("Skype", "Background messaging client.", ["Skype"], true),
        ("Phone Link", "Keeps a live connection to your phone open.", ["PhoneExperienceHost"], true),
        ("Windows Widgets", "Refreshes news and weather in the background.", ["Widgets"], true),
        ("qBittorrent", "Torrents can saturate your connection and cause lag spikes.", ["qbittorrent"], true),
        ("µTorrent", "Torrents can saturate your connection and cause lag spikes.", ["uTorrent"], true),
        ("BitTorrent", "Torrents can saturate your connection and cause lag spikes.", ["BitTorrent"], true),
        ("Spotify", "Only if you don't listen to music while playing.", ["Spotify"], false),
        ("Wallpaper Engine", "It pauses itself in fullscreen games; close it if you play windowed.",
            ["wallpaper64", "wallpaper32"], false),
    ];

    // Windows' own UI processes, never offered.
    static readonly HashSet<string> System = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "dwm", "PCOptimizer", "WindowsTerminal",
        "Taskmgr", "SecHealthUI", "CompPkgSrv",
    };

    static HashSet<string> RunningNames() =>
        Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Known background apps that are running right now. Ones the user already picked stay listed
    /// (so they can be switched off) even when they aren't running at the moment.
    /// </summary>
    public static List<CloseSuggestion> Detect(IEnumerable<string> alreadySelected)
    {
        var running = RunningNames();
        var selected = alreadySelected.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Known
            .Select(k => (k, isRunning: k.Processes.Any(running.Contains)))
            .Where(x => x.isRunning || x.k.Processes.Any(selected.Contains))
            .Select(x => new CloseSuggestion
            {
                Name = x.k.Name,
                Why = x.k.Why,
                Processes = x.k.Processes,
                Recommended = x.k.Recommended,
                IsRunning = x.isRunning,
            })
            .OrderByDescending(s => s.IsRunning).ThenByDescending(s => s.Recommended).ThenBy(s => s.Name)
            .ToList();
    }

    /// <summary>Every other app with an open window, for adding to the close list in one click.</summary>
    public static List<OpenApp> OpenApps(IEnumerable<string> exclude)
    {
        var skip = exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        var apps = new Dictionary<string, OpenApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle.Length == 0) continue;
                    var name = p.ProcessName;
                    if (System.Contains(name) || skip.Contains(name) || apps.ContainsKey(name)) continue;
                    var path = Native.ProcessPath(p.Id);
                    if (path is not null &&
                        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                        continue;
                    string display = name;
                    try
                    {
                        if (path is not null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) display = d.Trim();
                    }
                    catch { }
                    apps[name] = new OpenApp(name, display);
                }
                catch { /* exited or access denied */ }
            }
        }
        return apps.Values.OrderBy(a => a.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
