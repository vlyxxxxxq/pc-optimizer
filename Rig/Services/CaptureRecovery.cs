using System.Text.Json;

namespace Rig.Services;

public sealed record RecoverableCapture(string Directory, string Label, double Start, double End);

public static class CaptureRecovery
{
    public static IReadOnlyList<RecoverableCapture> Find(string? activeDirectory = null)
    {
        var result = new List<RecoverableCapture>();
        string root = Path.Combine(CaptureEngine.CacheRoot, "Replay");
        if (!Directory.Exists(root)) return result;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (directory.Equals(activeDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string marker = Path.Combine(directory, "recording.json");
                if (!System.IO.File.Exists(marker)) continue;
                var recording = JsonSerializer.Deserialize<CaptureStudio.RecoveryRecording>(System.IO.File.ReadAllText(marker));
                var segments = ReplayMedia.ReadSegments(directory);
                if (recording is null || segments.Count == 0) continue;
                double end = segments.Max(s => s.End);
                if (end > recording.Start) result.Add(new(directory, recording.Label, recording.Start, end));
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return result;
    }

    public static async Task<string> RecoverAsync(RecoverableCapture capture, string folder, CancellationToken cancellation = default)
    {
        string root = Path.GetFullPath(Path.Combine(CaptureEngine.CacheRoot, "Replay")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(capture.Directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a Rig recovery directory.");
        var selected = ReplayMedia.Select(ReplayMedia.ReadSegments(capture.Directory), capture.Start, capture.End);
        if (selected.Count == 0) throw new IOException("No completed segments remain in this recording.");
        string list = Path.Combine(capture.Directory, "recover-" + Guid.NewGuid().ToString("N") + ".txt");
        string temporary = Path.Combine(folder, "Recordings", ".recovery-" + Guid.NewGuid().ToString("N") + ".mp4");
        string destination = Path.Combine(folder, "Recordings", "Recovered " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + " " + Guid.NewGuid().ToString("N")[..6] + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await System.IO.File.WriteAllLinesAsync(list, selected.Select(s => ReplayMedia.ConcatLine(s.Path)), cancellation);
            var engine = await CaptureEngine.EnsureAsync();
            // Copy completed keyframe-aligned segments. Recovery can include up to one segment before the manual start.
            var (code, error) = await CaptureEngine.RunAsync(engine, ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", temporary], cancellation);
            if (code != 0) throw new IOException(error);
            _ = await ReplayMedia.InspectAsync(temporary, cancellation);
            System.IO.File.Move(temporary, destination, false);
            System.IO.File.Delete(Path.Combine(capture.Directory, "recording.json"));
            return destination;
        }
        finally { try { System.IO.File.Delete(list); System.IO.File.Delete(temporary); } catch { } }
    }
}
