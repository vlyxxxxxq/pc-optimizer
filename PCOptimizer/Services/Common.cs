using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;

namespace PCOptimizer.Services;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        int i = 0;
        while (bytes >= 1024 && i < Units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} B" : $"{bytes:0.#} {Units[i]}";
    }

    public static string Duration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{d.Days}d {d.Hours}h {d.Minutes}m" : $"{d.Hours}h {d.Minutes}m";

    /// <summary>Accent below 75%, amber up to 90%, red above.</summary>
    public static Brush LoadBrush(double percent) =>
        (Brush)Application.Current.Resources[percent >= 90 ? "DangerBrush" : percent >= 75 ? "WarnBrush" : "AccentBrush"];
}

public static class Elevation
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Relaunches the app with a UAC prompt. Returns false if the user declined.</summary>
    public static bool RestartElevated()
    {
        App.ReleaseInstanceLock();
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined; we keep running (the lock stays released, which is harmless)
        }
    }
}

public static class FileIcons
{
    /// <summary>The shell's icon for a file (works for .exe and .lnk), or null.</summary>
    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var info = new Native.SHFILEINFO();
        if (Native.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(),
                Native.SHGFI_ICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally { Native.DestroyIcon(info.hIcon); }
    }
}

public static class Shell
{
    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { MessageBox.Show($"Couldn't open the location:\n{ex.Message}", "PC Optimizer"); }
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string file, string args,
        System.Text.Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    /// <summary>Runs commands in a visible elevated console so the user can watch progress. False if UAC was declined.</summary>
    public static bool RunElevatedConsole(string commands)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{commands}\"") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Couldn't open {target}:\n{ex.Message}", "PC Optimizer"); }
    }
}
