using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PCOptimizer.Services;

/// <summary>Where one app's window was: matched back by program, then by title.</summary>
public sealed class SavedWindow
{
    public string Exe { get; set; } = "";
    public string Title { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    /// <summary>1 normal, 2 minimised, 3 maximised (SW_ values).</summary>
    public int Show { get; set; } = 1;
}

public sealed class WindowLayout
{
    public string Name { get; set; } = "";
    public DateTime Saved { get; set; }
    public List<SavedWindow> Windows { get; set; } = [];
    public string Detail => $"{Windows.Count} window{(Windows.Count == 1 ? "" : "s")} · " +
        string.Join(", ", Windows.Select(w => Path.GetFileNameWithoutExtension(w.Exe)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
}

/// <summary>
/// Saves where your windows are (e.g. "Gaming", "Study", "Streaming") and puts them back in one click,
/// optionally opening apps from the layout that aren't running. Kept in layouts.json.
/// </summary>
public static class WindowLayouts
{
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT { public int length, flags, showCmd; public Native.POINT min, max; public Native.RECT normal; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    static string FilePath => AppSettings.DataFile("layouts.json");

    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "TextInputHost.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "LockApp.exe", "PCOptimizer.exe", "Rig.exe",
    };

    public static List<WindowLayout> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<WindowLayout>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(List<WindowLayout> layouts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Normal app windows: visible, titled, not owned by another window, not cloaked (other virtual desktops, suspended UWP).</summary>
    static List<(IntPtr Hwnd, string Exe, string Title)> AppWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true; // GW_OWNER: dialogs, tool windows
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) == 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out int pid);
            if (Native.ProcessPath(pid) is not { } path || Skip.Contains(Path.GetFileName(path))) return true;
            long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            list.Add((hwnd, path, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowLayout Capture(string name)
    {
        var layout = new WindowLayout { Name = name, Saved = DateTime.Now };
        foreach (var (hwnd, exe, title) in AppWindows())
        {
            var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref p)) continue;
            layout.Windows.Add(new SavedWindow
            {
                Exe = exe, Title = title, Show = p.showCmd is 2 or 6 or 7 ? 2 : p.showCmd == 3 ? 3 : 1,
                Left = p.normal.Left, Top = p.normal.Top, Right = p.normal.Right, Bottom = p.normal.Bottom,
            });
        }
        return layout;
    }

    /// <summary>Moves running windows into place. Returns how many were placed and which apps weren't running.</summary>
    public static (int Placed, List<string> Missing) Apply(WindowLayout layout)
    {
        var open = AppWindows();
        var used = new HashSet<IntPtr>();
        int placed = 0;
        var missing = new List<string>();
        foreach (var w in layout.Windows)
        {
            var mine = open.Where(o => o.Exe.Equals(w.Exe, StringComparison.OrdinalIgnoreCase) && !used.Contains(o.Hwnd)).ToList();
            // Same title first (two browser windows, two editors), otherwise any window of that app.
            var match = mine.FirstOrDefault(o => o.Title == w.Title);
            if (match.Hwnd == IntPtr.Zero) match = mine.FirstOrDefault();
            if (match.Hwnd == IntPtr.Zero) { missing.Add(w.Exe); continue; }
            used.Add(match.Hwnd);
            var p = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<WINDOWPLACEMENT>(), showCmd = w.Show,
                normal = new Native.RECT { Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Bottom },
            };
            if (SetWindowPlacement(match.Hwnd, ref p)) placed++;
        }
        return (placed, missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Starts apps from the layout that aren't running; Apply again once they've opened.</summary>
    public static int Launch(IEnumerable<string> exes)
    {
        int started = 0;
        foreach (var exe in exes)
        {
            try { if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); started++; } }
            catch { }
        }
        return started;
    }
}
