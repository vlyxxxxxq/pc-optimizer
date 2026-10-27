using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Rig.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string PageUrl);

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

    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Version.TryParse(info?.Split('+')[0], out var v) ? v : new Version(0, 0);
        }
    }

    /// <summary>The lite build uses the installed .NET runtime; the full build carries its own.</summary>
    public static bool IsLite =>
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .Contains(@"\shared\Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);

    public static string AssetName => IsLite ? "PCOptimizer-lite.exe" : "PCOptimizer.exe";

    /// <summary>Latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateInfo(latest, tag, root.GetProperty("body").GetString() ?? "",
                asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                root.GetProperty("html_url").GetString() ?? ReleasesPage);
        }
        return null; // newer release exists but not for this flavour
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
