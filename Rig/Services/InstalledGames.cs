using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Rig.Services;

public sealed class LibraryGame : ObservableObject
{
    public required string Name { get; init; }
    public required string Store { get; init; }
    /// <summary>URI or exe path that starts the game through its launcher.</summary>
    public required string Launch { get; init; }
    public string? Folder { get; init; }
    /// <summary>Steam app id, for news.</summary>
    public string? AppId { get; init; }
    public string? Exe { get; init; }
    public string? CoverPath { get; init; }
    public TimeSpan Played { get; set; }
    public DateTime? LastPlayed { get; set; }

    public string PlayedText => Played.TotalMinutes < 1 ? "Not played yet"
        : Played.TotalHours < 1 ? $"{Played.Minutes} min played" : $"{Played.TotalHours:0.#} h played";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    ImageSource? _cover;
    public ImageSource? Cover { get => _cover; set => Set(ref _cover, value); }
    public bool HasCover => Cover is not null;

    bool _hasNews;
    /// <summary>The developer posted news in the last week (shown as a badge on the cover).</summary>
    public bool HasNews { get => _hasNews; set => Set(ref _hasNews, value); }
}

/// <summary>Finds installed games across Steam, Epic and GOG, with cover art where the launcher caches it.</summary>
public static class InstalledGames
{
    public static List<LibraryGame> Find()
    {
        var games = new List<LibraryGame>();
        try { games.AddRange(Steam()); } catch { }
        try { games.AddRange(Epic()); } catch { }
        try { games.AddRange(Gog()); } catch { }

        // Add the time Rig measured (useful for non-Steam games, which have no playtime of their own).
        var tracked = GameStats.Load();
        foreach (var g in games)
        {
            var mine = tracked.Where(s => s.Game.Equals(g.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) continue;
            var t = TimeSpan.FromTicks(mine.Sum(s => s.Duration.Ticks));
            if (t > g.Played) g.Played = t;
            var last = mine.Max(s => s.Start);
            if (g.LastPlayed is null || last > g.LastPlayed) g.LastPlayed = last;
        }
        return games.DistinctBy(g => g.Name.ToLowerInvariant()).ToList();
    }

    // Steam tools and runtimes that have an app manifest but aren't games.
    static readonly string[] SteamNotGames =
        ["Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR", "Wallpaper Engine",
         "Lossless Scaling", "Soundpad", "Steam Audio", "OVR Toolkit", "Spacewar"];

    static IEnumerable<LibraryGame> Steam()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steam) yield break;
        steam = steam.Replace('/', '\\');

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        var playtime = SteamPlaytime(steam);
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(manifest); } catch { continue; }
                string? Field(string name) => Regex.Match(text, $"\"{name}\"\\s+\"(.*?)\"") is { Success: true } m ? m.Groups[1].Value : null;
                var id = Field("appid");
                var name = Field("name");
                if (id is null || name is null || SteamNotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                playtime.TryGetValue(id, out var pt);
                yield return new LibraryGame
                {
                    Name = name, Store = "Steam", Launch = $"steam://rungameid/{id}", AppId = id,
                    Folder = Field("installdir") is { } dir ? Path.Combine(apps, "common", dir) : null,
                    CoverPath = SteamCover(steam, id),
                    Played = TimeSpan.FromMinutes(pt.Minutes),
                    LastPlayed = pt.Last,
                };
            }
        }
    }

    /// <summary>Steam's own playtime and last-played per app id, from the most recently used account's localconfig.vdf.</summary>
    static Dictionary<string, (int Minutes, DateTime? Last)> SteamPlaytime(string steam)
    {
        var result = new Dictionary<string, (int, DateTime?)>();
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return result;
        var config = Directory.GetDirectories(userdata)
            .Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (config is null) return result;
        var text = File.ReadAllText(config);
        // "<appid>" { ... "LastPlayed" "<unix>" ... "Playtime" "<minutes>" ... } (blocks may hold one level of nesting)
        foreach (Match m in Regex.Matches(text, "\"(\\d+)\"\\s*\\{((?:[^{}]|\\{[^{}]*\\})*)\\}"))
        {
            var body = m.Groups[2].Value;
            var pt = Regex.Match(body, "\"Playtime\"\\s+\"(\\d+)\"");
            var lp = Regex.Match(body, "\"LastPlayed\"\\s+\"(\\d+)\"");
            if (!pt.Success && !lp.Success) continue;
            DateTime? last = lp.Success && long.TryParse(lp.Groups[1].Value, out var unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null;
            result[m.Groups[1].Value] = (pt.Success ? int.Parse(pt.Groups[1].Value) : 0, last);
        }
        return result;
    }

    static string? SteamCover(string steam, string id)
    {
        var cache = Path.Combine(steam, @"appcache\librarycache");
        var flat = Path.Combine(cache, $"{id}_library_600x900.jpg");
        if (File.Exists(flat)) return flat;
        // Newer Steam keeps each app's art in its own folder (sometimes one level deeper).
        var dir = Path.Combine(cache, id);
        if (!Directory.Exists(dir)) return null;
        try
        {
            // Portrait art is "library_600x900" in older caches and "library_capsule" in newer ones.
            foreach (var pattern in new[] { "library_600x900*.jpg", "library_capsule*.jpg", "library_header*.jpg", "header*.jpg" })
                if (Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault() is { } found) return found;
            return null;
        }
        catch { return null; }
    }

    static IEnumerable<LibraryGame> Epic()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.GetFiles(manifests, "*.item"))
        {
            LibraryGame? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool isGame = !r.TryGetProperty("AppCategories", out var cats) ||
                              cats.EnumerateArray().Any(c => c.GetString() == "games");
                if (isGame && S("DisplayName") is { Length: > 0 } name)
                {
                    var folder = S("InstallLocation");
                    var exe = S("LaunchExecutable") is { Length: > 0 } le ? Path.Combine(folder, le) : null;
                    game = new LibraryGame
                    {
                        Name = name, Store = "Epic", Folder = folder, Exe = exe,
                        Launch = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=launch&silent=true",
                    };
                }
            }
            catch { }
            if (game is not null) yield return game;
        }
    }

    static IEnumerable<LibraryGame> Gog()
    {
        using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        foreach (var id in gog?.GetSubKeyNames() ?? [])
        {
            using var g = gog!.OpenSubKey(id);
            if (g?.GetValue("gameName") is not string name || g.GetValue("exe") is not string exe || !File.Exists(exe)) continue;
            yield return new LibraryGame { Name = name, Store = "GOG", Launch = exe, Exe = exe, Folder = g.GetValue("path") as string };
        }
    }

    /// <summary>Loads the cover (or the game's icon) off the UI thread's critical path; frozen so any thread can hand it over.</summary>
    public static ImageSource? LoadCover(LibraryGame g)
    {
        if (g.CoverPath is { } path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 300;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
        }
        return null;
    }

    public static void Start(LibraryGame g)
    {
        if (g.Launch.Contains("://")) { Shell.Open(g.Launch); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(g.Launch)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.Launch) ?? "",
            });
        }
        catch (Exception ex) { System.Windows.MessageBox.Show($"Couldn't start {g.Name}:\n{ex.Message}", "Rig"); }
    }
}
