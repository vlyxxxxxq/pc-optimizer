using System.Diagnostics;

namespace Rig.Services;

public sealed class BrowserItem : ObservableObject
{
    public required string Browser { get; init; }
    public required string Kind { get; init; }
    public required string Process { get; init; }
    public required List<string> Files { get; init; }
    public long Size => Files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
    public string Title => $"{Browser} {Kind.ToLowerInvariant()}";
    public string Effect => Kind == "Cookies" ? "Signs you out of websites" : "Clears visited sites and search suggestions";

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool IsRunning => System.Diagnostics.Process.GetProcessesByName(Process).Length > 0;
}

/// <summary>
/// Cookies and browsing history per browser. Never touches passwords, autofill or bookmarks
/// (Firefox keeps history and bookmarks in one file, so only its cookies are offered).
/// </summary>
public static class BrowserData
{
    // RIG_TEST_BROWSER_ROOT points both at a folder of fake browser profiles, for testing deletes safely.
    static readonly string? TestRoot = Environment.GetEnvironmentVariable("RIG_TEST_BROWSER_ROOT") is { Length: > 0 } t ? t : null;
    static readonly string Local = TestRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = TestRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static List<BrowserItem> Find()
    {
        var items = new List<BrowserItem>();
        Chromium(items, "Chrome", "chrome", Path.Combine(Local, @"Google\Chrome\User Data"));
        Chromium(items, "Edge", "msedge", Path.Combine(Local, @"Microsoft\Edge\User Data"));
        Chromium(items, "Brave", "brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"));
        Chromium(items, "Opera GX", "opera", Path.Combine(Roaming, @"Opera Software\Opera GX Stable"), singleProfile: true);

        var ff = Path.Combine(Roaming, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
        {
            var cookies = Directory.GetDirectories(ff)
                .SelectMany(p => new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm" }.Select(f => Path.Combine(p, f)))
                .Where(File.Exists).ToList();
            if (cookies.Count > 0)
                items.Add(new BrowserItem { Browser = "Firefox", Kind = "Cookies", Process = "firefox", Files = cookies });
        }
        return items;
    }

    static void Chromium(List<BrowserItem> items, string name, string process, string userData, bool singleProfile = false)
    {
        if (!Directory.Exists(userData)) return;
        var profiles = singleProfile ? [userData]
            : Directory.GetDirectories(userData).Where(d =>
                Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal))).ToArray();

        var cookies = profiles.SelectMany(p => new[]
        {
            Path.Combine(p, @"Network\Cookies"), Path.Combine(p, @"Network\Cookies-journal"),
            Path.Combine(p, "Cookies"), Path.Combine(p, "Cookies-journal"),
        }).Where(File.Exists).ToList();
        var history = profiles.SelectMany(p => new[]
        {
            Path.Combine(p, "History"), Path.Combine(p, "History-journal"), Path.Combine(p, "Visited Links"),
            Path.Combine(p, "Top Sites"), Path.Combine(p, "Top Sites-journal"),
        }).Where(File.Exists).ToList();

        if (cookies.Count > 0) items.Add(new BrowserItem { Browser = name, Kind = "Cookies", Process = process, Files = cookies });
        if (history.Count > 0) items.Add(new BrowserItem { Browser = name, Kind = "History", Process = process, Files = history });
    }

    /// <summary>Deletes the item's files. The browser must be closed (it locks them while open).</summary>
    public static (int Deleted, int Failed) Clear(BrowserItem item)
    {
        int deleted = 0, failed = 0;
        foreach (var f in item.Files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                File.Delete(f);
                deleted++;
            }
            catch { failed++; }
        }
        return (deleted, failed);
    }
}
