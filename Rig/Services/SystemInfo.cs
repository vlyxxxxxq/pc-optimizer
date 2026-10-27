using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Rig.Services;

/// <summary>Samples total CPU load from GetSystemTimes deltas between calls.</summary>
public sealed class CpuSampler
{
    ulong _prevIdle, _prevTotal;

    public double Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong idleNow = idle.Value, totalNow = kernel.Value + user.Value; // kernel time includes idle
        ulong dIdle = idleNow - _prevIdle, dTotal = totalNow - _prevTotal;
        _prevIdle = idleNow;
        _prevTotal = totalNow;
        return dTotal == 0 ? 0 : Math.Clamp((1.0 - (double)dIdle / dTotal) * 100, 0, 100);
    }
}

public static class SystemInfo
{
    public static (ulong Total, ulong Available) Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref m) ? (m.ullTotalPhys, m.ullAvailPhys) : (0, 0);
    }

    public static string OsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = key?.GetValue("ProductName") as string ?? "Windows";
        string version = key?.GetValue("DisplayVersion") as string ?? "";
        string build = key?.GetValue("CurrentBuild") as string ?? "";
        // ProductName still says "Windows 10" on Windows 11.
        if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name} {version} (build {build})".Trim();
    }

    public static string CpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown processor";
    }
}
