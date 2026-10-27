using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Rig.Services;

/// <summary>
/// Resolution and refresh rate of the main display, for game profiles that want e.g. 1080p at 360 Hz.
/// Changes are temporary (CDS_FULLSCREEN): Restore puts back the mode saved in Windows' display settings.
/// </summary>
public static class DisplayModes
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

    public const string Unchanged = "Same as desktop";
    const int ENUM_CURRENT_SETTINGS = -1, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_FULLSCREEN = 0x4;

    static List<string>? _choices;

    /// <summary>"Same as desktop" then every 32-bit mode of the main display, biggest and fastest first.</summary>
    public static List<string> Choices => _choices ??= [Unchanged, .. Modes().Select(m => Format(m.W, m.H, m.Hz))];

    public static string Format(int w, int h, int hz) => $"{w} × {h} @ {hz} Hz";

    static DEVMODE New() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    public static List<(int W, int H, int Hz)> Modes()
    {
        var set = new HashSet<(int, int, int)>();
        var dm = New();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmBitsPerPel == 32 && dm.dmPelsWidth >= 800) set.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return set.OrderByDescending(m => m.Item1 * m.Item2).ThenByDescending(m => m.Item3).ToList();
    }

    public static (int W, int H, int Hz)? Current()
    {
        var dm = New();
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    public static (int W, int H, int Hz)? Parse(string? text)
    {
        var m = Regex.Match(text ?? "", @"^(\d+) × (\d+) @ (\d+) Hz$");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>Switches the main display. False if the driver refused (the mode stays as it was).</summary>
    public static bool Apply(int w, int h, int hz)
    {
        var dm = New();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero) == 0;
    }

    /// <summary>Back to the mode saved in Windows' settings.</summary>
    public static void Restore() => ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
}
