using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rig.Services;

internal static class CaptureEngine
{
    static readonly SemaphoreSlim Gate = new(1);
    static string? _ready;
    internal static string CacheRoot => Environment.GetEnvironmentVariable("RIG_SETTINGS") is { Length: > 0 }
        ? AppSettings.DataFile("capture-cache") : Path.Combine(Places.LocalAppData, "Rig");
    public static async Task<string> EnsureAsync()
    {
        if (_ready is not null && System.IO.File.Exists(_ready) && _ready.StartsWith(Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return _ready;
        await Gate.WaitAsync();
        try
        {
            return _ready = await Task.Run(() =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var resource = assembly.GetManifestResourceStream("Rig.CaptureEngine.zip")
                    ?? throw new InvalidOperationException("This development build has no capture engine. Run scripts/Prepare-CaptureEngine.ps1, then rebuild Rig.");
                var folder = Path.Combine(CacheRoot, "CaptureEngine", "9.0.2-6882969ce9");
                Directory.CreateDirectory(folder);
                using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
                using var manifest = zip.GetEntry("manifest.json")!.Open();
                var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
                foreach (var (name, expected) in hashes)
                {
                    if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid capture engine filename.");
                    var target = Path.Combine(folder, name);
                    if (System.IO.File.Exists(target) && Hash(target) == expected) continue;
                    var temporary = target + ".tmp";
                    using (var input = zip.GetEntry(name)!.Open())
                    using (var output = System.IO.File.Create(temporary)) input.CopyTo(output);
                    if (Hash(temporary) != expected) throw new InvalidDataException("Capture engine verification failed.");
                    System.IO.File.Move(temporary, target, true);
                }
                return Path.Combine(folder, "ffmpeg.exe");
            });
        }
        finally { Gate.Release(); }
    }
    static string Hash(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static Process Launch(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException("The recording engine could not start.");
        try { CaptureProcessLifetime.Attach(process); return process; }
        catch { process.Dispose(); throw; }
    }
    public static async Task<(int Code, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        using var process = Launch(executable, arguments);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch { try { process.Kill(true); } catch { } throw; }
        return (process.ExitCode, await output + await error);
    }
}
