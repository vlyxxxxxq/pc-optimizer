using System.Text.RegularExpressions;

namespace Rig.Services;

public sealed record PowerPlan(string Guid, string Name, bool IsActive)
{
    public bool IsInactive => !IsActive;
}

public static partial class PowerPlans
{
    // e.g. "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *"
    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})\s+\((.+?)\)(\s*\*)?")]
    private static partial Regex SchemeLine();

    public static async Task<List<PowerPlan>> ListAsync()
    {
        var (_, output) = await Shell.RunAsync("powercfg", "/list");
        return SchemeLine().Matches(output)
            .Select(m => new PowerPlan(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success))
            .ToList();
    }

    public static async Task<bool> SetActiveAsync(string guid) =>
        (await Shell.RunAsync("powercfg", $"/setactive {guid}")).ExitCode == 0;

    /// <summary>Blocking version for app shutdown.</summary>
    public static bool SetActive(string guid)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg", $"/setactive {guid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Unhides Windows' Ultimate Performance scheme by duplicating it.</summary>
    public static async Task<(bool Ok, string Output)> AddUltimateAsync()
    {
        var (code, output) = await Shell.RunAsync("powercfg", "-duplicatescheme e9a42b02-d5df-44a8-a4b1-0d1d0b4a3a38");
        return (code == 0, output);
    }
}
