using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Rig.Services;

public sealed record SecurityStatus(
    string Antivirus, bool DefenderActive, bool RealTime, bool TamperProtected,
    DateTime? DefinitionsUpdated, DateTime? LastQuickScan, DateTime? LastFullScan, string RunningMode)
{
    public bool DefinitionsOld => DefinitionsUpdated is { } d && DateTime.Now - d > TimeSpan.FromDays(3);
    public bool Protected => RealTime || !DefenderActive; // another antivirus is fine
}

/// <summary>Microsoft Defender status and actions via its own tools (Get-MpComputerStatus, MpCmdRun).</summary>
public static class Security
{
    static string MpCmdRun => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");

    public static async Task<SecurityStatus> StatusAsync()
    {
        const string script =
            "$ErrorActionPreference='SilentlyContinue'; $s = Get-MpComputerStatus; " +
            "$av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct | ForEach-Object displayName) -join ', '; " +
            "function D($d) { if ($d -and $d.Year -gt 2000) { $d.ToString('o') } else { $null } }; " +
            "[pscustomobject]@{ Av=$av; Enabled=[bool]$s.AntivirusEnabled; RealTime=[bool]$s.RealTimeProtectionEnabled; " +
            "Tamper=[bool]$s.IsTamperProtected; Mode=[string]$s.AMRunningMode; Defs=(D $s.AntivirusSignatureLastUpdated); " +
            "Quick=(D $s.QuickScanEndTime); Full=(D $s.FullScanEndTime) } | ConvertTo-Json -Compress";
        var (_, output) = await Shell.RunAsync("powershell", $"-NoProfile -Command \"{script}\"", Encoding.UTF8);
        using var doc = JsonDocument.Parse(output.Trim());
        var j = doc.RootElement;
        DateTime? Date(string p) => j.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var d) ? d : null;
        var mode = j.GetProperty("Mode").GetString() ?? "";
        return new SecurityStatus(
            j.GetProperty("Av").GetString() is { Length: > 0 } av ? av : "Microsoft Defender",
            j.GetProperty("Enabled").GetBoolean() && !mode.Contains("Passive", StringComparison.OrdinalIgnoreCase),
            j.GetProperty("RealTime").GetBoolean(), j.GetProperty("Tamper").GetBoolean(),
            Date("Defs"), Date("Quick"), Date("Full"), mode);
    }

    /// <summary>Runs a Defender scan (1 = quick, 2 = full). Returns (finished, threats found, output).</summary>
    public static async Task<(bool Ok, bool Threats, string Output)> ScanAsync(int type, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(MpCmdRun, $"-Scan -ScanType {type}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        // MpCmdRun: 0 = clean, 2 = threats found (Windows Security handles them).
        return (p.ExitCode is 0 or 2, p.ExitCode == 2, output.Trim());
    }

    public static async Task<(bool Ok, string Output)> UpdateDefinitionsAsync()
    {
        var (code, output) = await Shell.RunAsync(MpCmdRun, "-SignatureUpdate");
        return (code == 0, output.Trim());
    }

    // ---- Game folder exclusions (admin) ----

    public static async Task<List<string>> ExclusionsAsync()
    {
        var (_, output) = await Shell.RunAsync("powershell",
            "-NoProfile -Command \"(Get-MpPreference).ExclusionPath | ConvertTo-Json -Compress\"", Encoding.UTF8);
        try
        {
            using var doc = JsonDocument.Parse(output.Trim());
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList(),
                JsonValueKind.String => [doc.RootElement.GetString()!],
                _ => [],
            };
        }
        catch { return []; }
    }

    /// <summary>Adds or removes Defender exclusions for folders. Needs administrator mode.</summary>
    public static async Task<(bool Ok, string Output)> SetExclusionsAsync(IEnumerable<string> folders, bool add)
    {
        var list = string.Join(",", folders.Select(f => $"'{f.TrimEnd('\\').Replace("'", "''")}'"));
        var (code, output) = await Shell.RunAsync("powershell",
            $"-NoProfile -Command \"{(add ? "Add" : "Remove")}-MpPreference -ExclusionPath {list} -ErrorAction Stop\"", Encoding.UTF8);
        return (code == 0, output.Trim());
    }
}
