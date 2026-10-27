using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Rig.Services;

public sealed record UpdateInfo(AppVersion Version, string Tag, string Notes, string DownloadUrl, long Size, string PageUrl,
    string? Sha256 = null, string? SignatureUrl = null);

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
    const string Repo = "vlyxxxxxq/rig";
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

    /// <summary>Local development builds use the installed runtime; the released EXE carries its own.</summary>
    public static bool IsDevelopmentBuild =>
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .Contains(@"\shared\Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase);

    public static string BuildLabel => IsDevelopmentBuild ? "development build" : "standalone build";
    public const string AssetName = "Rig.exe";

    /// <summary>
    /// The newest release that's newer than this build, otherwise null. Pre-releases count only with the beta channel on
    /// (or when this build is itself a beta, so beta testers get the final release and later betas).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool beta = false)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return SelectUpdate(doc.RootElement, CurrentVersion, beta);
    }

    /// <summary>Selects the one public app download, regardless of which runtime is running this copy.</summary>
    internal static UpdateInfo? SelectUpdate(JsonElement releases, AppVersion current, bool beta = false)
    {
        beta |= current.IsPreRelease;
        UpdateInfo? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            if (release.GetProperty("prerelease").GetBoolean() && !beta) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (AppVersion.Parse(tag) is not { } version || !(version > current) || (best is not null && !(version > best.Version))) continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToList();
            string? sigUrl = assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), AssetName + ".sig", StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } sig
                ? sig.GetProperty("browser_download_url").GetString() : null;
            foreach (var asset in assets)
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                // GitHub publishes a SHA-256 digest per asset ("sha256:…"); it guards against a damaged download.
                string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
                best = new UpdateInfo(version, tag, release.GetProperty("body").GetString() ?? "",
                    asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(),
                    release.GetProperty("html_url").GetString() ?? ReleasesPage, sha, sigUrl);
            }
        }
        return best;
    }

    // ---------- Authenticity ----------

    /// <summary>
    /// The release-signing public key (ECDSA P-256, SubjectPublicKeyInfo, base64). While it's empty, releases can't be
    /// authenticated and updates are checked for size and SHA-256 only; see docs/SIGNING.md for setting up a key.
    /// Once set, every update must carry a valid "&lt;asset&gt;.sig" or it's refused.
    /// </summary>
    public const string SigningKey = "";

    public static bool SigningConfigured => SigningKey.Length > 0;

    /// <summary>Checks a detached signature (base64 IEEE P1363 ECDSA over SHA-256 of the file) against a public key.</summary>
    public static bool Verify(Stream file, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(file, Convert.FromBase64String(signatureBase64.Trim()),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }

    // ---------- Installing, with rollback ----------

    /// <summary>Written while a new version proves it starts; cleared by MarkHealthy. Lives next to the EXE.</summary>
    public sealed record PendingUpdate(string Exe, string From, string To, int Attempts);

    static string MarkerFor(string exe) => exe + ".update.json";

    /// <summary>
    /// Downloads the update (bounded to its published size, cancellable), checks it, swaps it in and restarts. The
    /// previous EXE stays as ".old" until the new one reports a healthy start; if it doesn't start, the old one is put
    /// back. Returns why it couldn't, or null on success (the app is restarting).
    /// </summary>
    public static async Task<string?> InstallAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancel = default)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var fresh = exe + ".new";
        var old = exe + ".old";
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } announced && announced != update.Size)
                    return "The download isn't the size GitHub lists for it, so it wasn't used.";
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(fresh);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    done += read;
                    if (done > update.Size) return "The download was bigger than expected, so it was stopped.";
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress.Report((double)done / update.Size);
                }
            }
            if (new FileInfo(fresh).Length != update.Size) return "The download was incomplete. Try again.";
            if (update.Sha256 is { } expected && !Sha256(fresh).Equals(expected, StringComparison.OrdinalIgnoreCase))
                return "The download didn't match its published checksum, so it wasn't used.";
            if (SigningConfigured)
            {
                if (update.SignatureUrl is null) return "This release isn't signed, so it wasn't installed.";
                var signature = await Http.GetStringAsync(update.SignatureUrl, cancel);
                await using var check = File.OpenRead(fresh);
                if (!Verify(check, signature, SigningKey)) return "The update's signature didn't check out, so it wasn't installed.";
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);          // a running EXE can be renamed, just not overwritten
            File.Move(fresh, exe);
            File.WriteAllText(MarkerFor(exe), JsonSerializer.Serialize(new PendingUpdate(exe, CurrentVersion.ToString(), update.Version.ToString(), 0)));
        }
        catch (OperationCanceledException) { return "Canceled."; }
        catch (Exception ex)
        {
            if (!File.Exists(exe) && File.Exists(old)) try { File.Move(old, exe); } catch { } // put the original back
            return ex is UnauthorizedAccessException ? "Rig's folder is read-only, so it can't update itself here." : $"Couldn't update: {ex.Message}";
        }
        finally { try { if (File.Exists(fresh)) File.Delete(fresh); } catch { } }

        App.ReleaseInstanceLock();
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true, WorkingDirectory = dir });
        Application.Current.Shutdown(); // runs the normal exit: Game Mode restores, recordings finish
        return null;
    }

    public enum StartCheck { Normal, Trial, RolledBack }

    /// <summary>
    /// Runs first thing at start. After an update, counts start attempts; if the new version failed to come up
    /// three times, the previous EXE is put back (and started). The broken one is kept as ".failed".
    /// </summary>
    public static StartCheck CheckAtStart(string exe, bool launchOld = true)
    {
        var marker = MarkerFor(exe);
        try { if (File.Exists(exe + ".new")) File.Delete(exe + ".new"); } catch { } // an interrupted download
        if (!File.Exists(marker)) { CleanUpOld(exe); return StartCheck.Normal; }
        try
        {
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(marker))!;
            if (pending.Attempts >= 2 && File.Exists(exe + ".old"))
            {
                File.Move(exe, exe + ".failed", overwrite: true);
                File.Move(exe + ".old", exe);
                File.Delete(marker);
                if (launchOld) Process.Start(new ProcessStartInfo(exe, "--rolled-back") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                return StartCheck.RolledBack;
            }
            File.WriteAllText(marker, JsonSerializer.Serialize(pending with { Attempts = pending.Attempts + 1 }));
            return StartCheck.Trial;
        }
        catch { return StartCheck.Normal; }
    }

    /// <summary>The new version is up and running: forget the old one.</summary>
    public static void MarkHealthy(string exe)
    {
        try { File.Delete(MarkerFor(exe)); } catch { }
        CleanUpOld(exe);
    }

    /// <summary>Removes the previous version, but never while an update is still on trial.</summary>
    public static void CleanUpOld(string? exe = null)
    {
        exe ??= Environment.ProcessPath!;
        if (File.Exists(MarkerFor(exe))) return;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch { /* still locked; next launch */ }
    }
}
