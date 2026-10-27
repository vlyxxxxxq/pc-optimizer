using System.Globalization;
using Microsoft.Win32;

namespace Rig.Services;

public sealed record GpuDriver(string Name, string Vendor, string Version, DateTime? Date, string DownloadUrl)
{
    public int AgeDays => Date is { } d ? (int)(DateTime.Today - d).TotalDays : -1;
    public bool IsOld => AgeDays > 180;
    public string AgeText => Date is { } d
        ? AgeDays switch
        {
            < 31 => $"{d:d} (this month)",
            < 365 => $"{d:d} ({AgeDays / 30} months old)",
            _ => $"{d:d} ({AgeDays / 365.0:0.#} years old)",
        }
        : "unknown date";
}

/// <summary>Reads installed display drivers from the registry (the same data Device Manager shows).</summary>
public static class GpuInfo
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static List<GpuDriver> Load()
    {
        var list = new List<GpuDriver>();
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (cls is null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name) continue;
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                var provider = k.GetValue("ProviderName") as string ?? "";
                var (vendor, url) = Vendor(name + " " + provider);
                DateTime? date = DateTime.TryParseExact(k.GetValue("DriverDate") as string, ["M-d-yyyy", "MM-dd-yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                if (list.Any(g => g.Name == name)) continue;
                list.Add(new GpuDriver(name, vendor, k.GetValue("DriverVersion") as string ?? "?", date, url));
            }
            catch { }
        }
        // Dedicated GPUs first: the integrated one ("Radeon(TM) Graphics", Intel UHD/Iris) is rarely what games use.
        return list.OrderBy(g => IsIntegrated(g.Name) ? 1 : 0).ToList();
    }

    public static bool IsIntegrated(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"Radeon(\(TM\))? Graphics$|Intel.*(UHD|Iris|HD Graphics)|Vega \d+ Graphics",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static (string Vendor, string Url) Vendor(string text) => text.ToUpperInvariant() switch
    {
        var t when t.Contains("NVIDIA") || t.Contains("GEFORCE") => ("NVIDIA", "https://www.nvidia.com/en-us/software/nvidia-app/"),
        var t when t.Contains("AMD") || t.Contains("RADEON") || t.Contains("ADVANCED MICRO") =>
            ("AMD", "https://www.amd.com/en/support/download/drivers.html"),
        var t when t.Contains("INTEL") => ("Intel", "https://www.intel.com/content/www/us/en/support/detect.html"),
        _ => ("Other", "ms-settings:windowsupdate-optionalupdates"),
    };
}
