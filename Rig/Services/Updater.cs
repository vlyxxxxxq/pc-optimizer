using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Rig.Services;

public sealed record UpdateInfo(AppVersion Version, string Tag, string Notes, string DownloadUrl, long Size, string PageUrl);

/// <summary>A version like 2.0.0 or 2.0.0-beta.1. A pre-release sorts before its final release.</summary>
public sealed record AppVersion(Version Core, string? Pre) : IComparable<AppVersion>
{
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        if (!Version.TryParse(core, out var v)) return null;
        return new AppVersion(new Version(v.Major, v.Minor, Math.Max(0, v.Build)), dash < 0 ? null : text[(dash + 1)..]);
    }

    public bool IsPreRelease => Pre is not null;

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Core.CompareTo(other.Core);
        if (c != 0) return c;
        if (Pre == other.Pre) return 0;
        if (Pre is null) return 1;
        if (other.Pre is null) return -1;
        // beta.2 < beta.10: compare dot-separated parts, numbers as numbers.
        var a = Pre.Split('.'); var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool na = int.TryParse(a[i], out var ia), nb = int.TryParse(b[i], out var ib);
            int d = na && nb ? ia.CompareTo(ib) : string.CompareOrdinal(a[i], b[i]);
            if (d != 0) return d;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public override string ToString() => Core.ToString(3) + (Pre is null ? "" : "-" + Pre);
}

/// <summary>
/// Checks GitHub for a newer release and updates in place: download next to the EXE, rename the
/// running EXE to .old (Windows allows that), move the new one in, restart. The .old file is
/// cleaned up on the next launch.
/// </summary>
public static class Updater
{
    const string Repo = "vlyxxxxxq/pc-optimizer";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Rig-Updater");
        return c;
    }

    public static AppVersion CurrentVersion { get; } =
        AppVersion.Parse(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new AppVersion(new Version(0, 0, 0), null);

    /// <summary>The lite build uses the installed .NET runtime; the full build carries its own.</summary>
    public static bool IsLite =>
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .Contains(@"\shared\Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);

    public static string AssetName => IsLite ? "Rig-lite.exe" : "Rig.exe";

    /// <summary>
    /// The newest release that's newer than this build, otherwise null. Pre-releases count only with the beta channel on
    /// (or when this build is itself a beta, so beta testers get the final release and later betas).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool beta = false)
    {
        beta |= CurrentVersion.IsPreRelease;
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        UpdateInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            if (release.GetProperty("prerelease").GetBoolean() && !beta) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (AppVersion.Parse(tag) is not { } version || !(version > CurrentVersion) || (best is not null && !(version > best.Version))) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                best = new UpdateInfo(version, tag, release.GetProperty("body").GetString() ?? "",
                    asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                    release.GetProperty("html_url").GetString() ?? ReleasesPage);
            }
        }
        return best;
    }

    /// <summary>Downloads and swaps in the new EXE, then restarts. Returns false if it couldn't (e.g. no write access).</summary>
    public static async Task<bool> InstallAsync(UpdateInfo update, IProgress<double> progress)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var fresh = exe + ".new";
        var old = exe + ".old";
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? update.Size;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(fresh);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress.Report((double)done / total);
                }
            }
            if (new FileInfo(fresh).Length != update.Size) throw new IOException("Download was incomplete.");

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);          // a running EXE can be renamed, just not overwritten
            File.Move(fresh, exe);
        }
        catch
        {
            try { if (File.Exists(fresh)) File.Delete(fresh); } catch { }
            if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); // put the original back
            return false;
        }

        App.ReleaseInstanceLock();
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>Removes the previous version left behind by an update.</summary>
    public static void CleanUpOld()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* still locked for a moment; next launch gets it */ }
    }
}
