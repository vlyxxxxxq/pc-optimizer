using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PCOptimizer.Services;

public sealed class CaptureItem : ObservableObject
{
    public required string Path { get; init; }
    public required DateTime Taken { get; init; }
    public required long Size { get; init; }
    public bool IsVideo => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".webm";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool IsGif => System.IO.Path.GetExtension(Path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    public bool IsRecording => IsVideo && System.IO.Path.GetDirectoryName(Path)!.Equals(ScreenRecorder.Folder, StringComparison.OrdinalIgnoreCase);
    public string Kind => IsGif ? "GIF" : IsRecording ? "Recording" : IsVideo ? "Clip" : "Screenshot";
    public bool HasBadge => IsVideo || IsGif;
    public string Detail => $"{Kind} · {Taken:g} · {Fmt.Bytes(Size)}";
    ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
}

/// <summary>
/// Clips come from Windows' own Game Bar recorder ("Record what happened"), so they work in any game with
/// no extra overhead from us. Screenshots are taken directly and saved to Pictures\PC Optimizer.
/// </summary>
public static class Captures
{
    const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    public static string ScreenshotFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PC Optimizer");

    /// <summary>Game Bar's save folder ("Captures" known folder, normally Videos\Captures).</summary>
    public static string ClipFolder
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key?.GetValue("{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}") is string p) return Environment.ExpandEnvironmentVariables(p);
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
        }
    }

    /// <summary>Whether Game Bar keeps recording in the background so the last 30 seconds can be saved.</summary>
    public static bool BackgroundRecording
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(DvrKey);
            return key?.GetValue("AppCaptureEnabled") is 1 && key.GetValue("HistoricalCaptureEnabled") is 1;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(DvrKey);
            key.SetValue("AppCaptureEnabled", value ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("HistoricalCaptureEnabled", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Asks Game Bar to save the last 30 seconds (its own Win+Alt+G shortcut).</summary>
    public static void SaveClip() => KeySender.Chord(Key.LWin, Key.LeftAlt, Key.G);

    /// <summary>Saves the monitor the focused window is on as a PNG. Returns the file, or null.</summary>
    public static string? Screenshot(string? game)
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref info)) return null;
            var r = info.rcMonitor;
            using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            Directory.CreateDirectory(ScreenshotFolder);
            var label = string.Concat((game ?? "Screenshot").Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(ScreenshotFolder, $"{label} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch { return null; }
    }

    public static List<CaptureItem> List()
    {
        var items = new List<CaptureItem>();
        foreach (var (folder, pattern) in new[] { (ClipFolder, "*.mp4"), (ClipFolder, "*.png"), (ScreenshotFolder, "*.png"), (ScreenRecorder.Folder, "*.mp4"), (ScreenRecorder.Folder, "*.gif") })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles(pattern))
                    items.Add(new CaptureItem { Path = f.FullName, Taken = f.LastWriteTime, Size = f.Length });
            }
            catch { }
        }
        return items.DistinctBy(i => i.Path.ToLowerInvariant()).OrderByDescending(i => i.Taken).Take(300).ToList();
    }
}

/// <summary>Explorer's thumbnail for any file (video frames included).</summary>
public static class Thumbnails
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    public static ImageSource? For(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_BIGGERSIZEOK: any cached size at least this big is fine.
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1, out var hbmp) != 0) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
    }
}
