using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCOptimizer.Services;

/// <summary>Folder is the game's install folder (used for "Not a game" so all its exes are ignored).</summary>
public sealed record DetectedGame(int Pid, string Name, string Path, string Source, string Folder);

/// <summary>A folder that contains installed games. Name is set when the folder is one specific game.</summary>
public sealed record GameLibrary(string Root, string Source, string? Name = null)
{
    public string Display => Name is null ? $"{Source} · {Root.TrimEnd('\\')}" : $"{Source} · {Name}";
}

/// <summary>
/// Decides whether the app you're actually using is a game. Only the foreground window's process
/// is considered, so things that merely run from a game library (Wallpaper Engine, idle apps,
/// launchers in the background) never count. A foreground process is a game when it's on the
/// user's list, or lives in an installed game library (Steam, Epic, GOG, Ubisoft, EA, Riot, Xbox)
/// and isn't a known non-game, or optionally when it's any fullscreen app.
/// </summary>
public sealed class GameDetector
{
    // Steam/Epic "apps" that install next to games but aren't games. Matched against the install folder name.
    static readonly string[] NotGameFolderFragments =
    [
        "wallpaper_engine", "wallpaper engine", "steamworks shared", "steamvr", "steam controller configs",
        "proton ", "proton -", "steamlinuxruntime", "steam linux runtime", "soundpad", "lossless scaling",
        "bongo cat", "bongocat", "obs studio", "desktop mate", "desktopmate", "fps monitor", "voicemod",
        "vtube studio", "rpg maker", "gamemaker", "redistributable", "dedicated server", "rivatuner",
        "msi afterburner", "unity hub", "steam audio", "deskmates",
    ];

    // Short names that would be too risky as fragments, so they must match the folder exactly.
    static readonly HashSet<string> NotGameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Banana", "Egg", "Cats", "Spacewar", "Blender", "Krita", "Aseprite", "Godot", "Shotcut",
    };

    // Helpers, launchers and anti-cheat services that live inside game folders but aren't the game.
    static readonly string[] NotGameFragments =
    [
        "launcher", "crash", "report", "helper", "setup", "unins", "redist", "install", "update", "anticheat",
        "battleye", "beservice", "overlay", "webhelper", "cefprocess", "prereq", "dxsetup", "service",
        "leagueclient", "riotclient", "vanguard", "eaclient", "uplay", "ubisoftconnect", "galaxyclient",
    ];

    static readonly HashSet<string> FullscreenIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe",
        "vivaldi.exe", "vlc.exe", "mpc-hc64.exe", "mpv.exe", "PotPlayerMini64.exe", "ApplicationFrameHost.exe",
        "Teams.exe", "ms-teams.exe", "Zoom.exe", "obs64.exe", "PCOptimizer.exe", "devenv.exe", "Code.exe",
        "LockApp.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
    };

    List<GameLibrary> _libraries = [];

    public IReadOnlyList<GameLibrary> Libraries => _libraries;

    public void RefreshLibraries() => _libraries = FindLibraries();

    /// <summary>The game you're playing right now, or null. Cheap: looks at one process per call.</summary>
    public DetectedGame? Detect(AppSettings settings)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Native.GetShellWindow()) return null;
        Native.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid <= 4 || pid == Environment.ProcessId) return null;
        var path = Native.ProcessPath(pid);
        if (path is null) return null;

        return Classify(pid, path, settings)
               ?? (settings.DetectFullscreen ? FullscreenApp(hwnd, pid, path, settings) : null);
    }

    DetectedGame? Classify(int pid, string path, AppSettings settings)
    {
        if (IsIgnored(path, settings)) return null;
        var exe = Path.GetFileName(path);

        if (settings.CustomGames.Any(g =>
                g.Equals(exe, StringComparison.OrdinalIgnoreCase) || g.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return new DetectedGame(pid, FriendlyName(path), path, "My games", Path.GetDirectoryName(path) ?? path);

        if (IsHelper(exe)) return null;
        var library = _libraries.FirstOrDefault(l => path.StartsWith(l.Root, StringComparison.OrdinalIgnoreCase));
        if (library is null) return null;

        var folder = GameFolder(path, library);
        var folderName = Path.GetFileName(folder.TrimEnd('\\'));
        if (IsKnownNonGame(folderName)) return null;
        return new DetectedGame(pid, library.Name ?? folderName, path, library.Source, folder);
    }

    static bool IsIgnored(string path, AppSettings settings) =>
        settings.IgnoredGames.Any(i =>
            path.StartsWith(i.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals(i, StringComparison.OrdinalIgnoreCase));

    static bool IsKnownNonGame(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        return NotGameFolders.Contains(folderName) || NotGameFolderFragments.Any(lower.Contains);
    }

    /// <summary>The top-level folder of the game inside its library (e.g. ...\common\Cyberpunk 2077\).</summary>
    static string GameFolder(string path, GameLibrary library)
    {
        if (library.Name is not null) return library.Root;
        var relative = path[library.Root.Length..].TrimStart('\\');
        int slash = relative.IndexOf('\\');
        return slash > 0 ? library.Root + relative[..slash] + "\\" : Path.GetDirectoryName(path) + "\\";
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    static bool IsHelper(string exe)
    {
        var lower = exe.ToLowerInvariant();
        return NotGameFragments.Any(lower.Contains);
    }

    static string FriendlyName(string path)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product)) return product;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Foreground window covering its whole monitor, excluding browsers, players and the shell.</summary>
    static DetectedGame? FullscreenApp(IntPtr hwnd, int pid, string path, AppSettings settings)
    {
        if (IsIgnored(path, settings) || FullscreenIgnore.Contains(Path.GetFileName(path))) return null;
        if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Native.GetWindowRect(hwnd, out var r)) return null;
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref info)) return null;
        var m = info.rcMonitor;
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return null;
        return new DetectedGame(pid, FriendlyName(path), path, "Fullscreen app", Path.GetDirectoryName(path) ?? path);
    }

    static List<GameLibrary> FindLibraries()
    {
        var list = new List<GameLibrary>();
        void Add(string? dir, string source, string? name = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                if (root.Length <= 3) return; // never treat a whole drive as a game
                if (!list.Any(l => l.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new GameLibrary(root, source, name));
            }
            catch { }
        }

        // Steam: main install plus every library listed in libraryfolders.vdf.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steam)
            {
                steam = steam.Replace('/', '\\');
                Add(Path.Combine(steam, @"steamapps\common"), "Steam");
                var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                        Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common"), "Steam");
            }
        }
        catch { }

        // Epic: one manifest per installed game.
        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
                foreach (var file in Directory.GetFiles(manifests, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("InstallLocation", out var loc))
                        Add(loc.GetString(), "Epic",
                            root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null);
                }
        }
        catch { }

        // GOG Galaxy and Ubisoft Connect register their installs.
        try
        {
            using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            foreach (var id in gog?.GetSubKeyNames() ?? [])
            {
                using var g = gog!.OpenSubKey(id);
                Add(g?.GetValue("path") as string, "GOG", g?.GetValue("gameName") as string);
            }
            using var ubi = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher");
            if (ubi?.GetValue("InstallDir") is string ubiDir) Add(Path.Combine(ubiDir, "games"), "Ubisoft");
        }
        catch { }

        // Well-known default folders on every fixed drive.
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var r = d.RootDirectory.FullName;
            Add(Path.Combine(r, "XboxGames"), "Xbox");
            Add(Path.Combine(r, @"SteamLibrary\steamapps\common"), "Steam");
            Add(Path.Combine(r, @"Program Files\Epic Games"), "Epic");
            Add(Path.Combine(r, "Epic Games"), "Epic");
            Add(Path.Combine(r, "Riot Games"), "Riot");
            Add(Path.Combine(r, @"Program Files\EA Games"), "EA");
            Add(Path.Combine(r, @"Program Files (x86)\Origin Games"), "EA");
            Add(Path.Combine(r, @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games"), "Ubisoft");
            Add(Path.Combine(r, @"Program Files (x86)\GOG Galaxy\Games"), "GOG");
            Add(Path.Combine(r, "Games"), "Games folder");
        }

        return list;
    }
}
