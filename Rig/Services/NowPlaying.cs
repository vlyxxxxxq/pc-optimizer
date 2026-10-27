using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Rig.Services;

public sealed record Track(string Title, string? Artist, string App);

/// <summary>
/// What's playing, read from music apps' window titles (Spotify, Apple Music, foobar2000, MusicBee, TIDAL) and
/// YouTube / YouTube Music tabs in any browser. Play/pause and skip use the keyboard media keys, so they control
/// whatever app Windows routes media keys to.
/// </summary>
public static class NowPlaying
{
    static readonly Dictionary<string, string> Players = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spotify"] = "Spotify", ["AppleMusic"] = "Apple Music", ["foobar2000"] = "foobar2000", ["MusicBee"] = "MusicBee", ["TIDAL"] = "TIDAL",
    };

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
    };

    // Titles players show while idle or paused, which aren't a track.
    static readonly HashSet<string> Idle = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spotify", "Spotify Free", "Spotify Premium", "Apple Music", "foobar2000", "MusicBee", "TIDAL",
    };

    public static Track? Current()
    {
        Track? fromBrowser = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var title = p.MainWindowTitle.Trim();
                if (title.Length == 0) continue;
                if (Players.TryGetValue(p.ProcessName, out var app))
                {
                    if (Idle.Contains(title) || title.StartsWith(app + " ", StringComparison.OrdinalIgnoreCase)) continue;
                    // foobar2000 and MusicBee put their own name at the end.
                    title = Regex.Replace(title, @"\s*[-–]\s*(foobar2000|MusicBee).*$", "");
                    return Split(title, app, artistFirst: app == "Spotify");
                }
                if (fromBrowser is null && Browsers.Contains(p.ProcessName))
                    fromBrowser = FromBrowser(title);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return fromBrowser;
    }

    /// <summary>"(3) Song - YouTube - Google Chrome" → Song on YouTube. Only the active tab's title is visible.</summary>
    static Track? FromBrowser(string title)
    {
        title = Regex.Replace(title, @"^\(\d+\)\s*", "");
        title = Regex.Replace(title, @"\s*[-–—]\s*(Google Chrome|Microsoft​? Edge|Mozilla Firefox|Brave|Opera|Vivaldi|Arc)$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*(and \d+ more pages?|- Personal|- Work)$", "", RegexOptions.IgnoreCase).Trim();
        if (title.EndsWith(" - YouTube Music", StringComparison.OrdinalIgnoreCase))
            return Split(title[..^" - YouTube Music".Length], "YouTube Music", artistFirst: false);
        if (title.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
            return new Track(title[..^" - YouTube".Length].Trim(), null, "YouTube");
        return null;
    }

    static Track Split(string title, string app, bool artistFirst)
    {
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return new Track(title, null, app);
        var (a, b) = (title[..dash].Trim(), title[(dash + 3)..].Trim());
        return artistFirst ? new Track(b, a, app) : new Track(a, b, app);
    }

    public static void PlayPause() => KeySender.Chord(System.Windows.Input.Key.MediaPlayPause);
    public static void Next() => KeySender.Chord(System.Windows.Input.Key.MediaNextTrack);
    public static void Previous() => KeySender.Chord(System.Windows.Input.Key.MediaPreviousTrack);
}
