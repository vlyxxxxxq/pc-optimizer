using System.Globalization;
using System.Text.Json;

namespace Rig.Services;

public sealed record ReplaySegment(string Path, double Start, double End);
public sealed record MediaDetails(double Duration, int Width, int Height, bool HasAudio);

public static class ReplayMedia
{
    public static IReadOnlyList<ReplaySegment> ReadSegments(string directory)
    {
        var result = new List<ReplaySegment>();
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "segments.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length != 3 || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double start)
                    || !double.TryParse(parts[2], CultureInfo.InvariantCulture, out double end) || end <= start) continue;
                string name = parts[0].Trim('"');
                if (Path.GetFileName(name) != name) continue;
                var path = Path.Combine(directory, name);
                if (System.IO.File.Exists(path)) result.Add(new(path, start, end));
            }
        }
        catch (IOException) { }
        return result;
    }

    public static IReadOnlyList<ReplaySegment> Select(IReadOnlyList<ReplaySegment> segments, double start, double end) =>
        segments.Where(s => s.End > start && s.Start < end).OrderBy(s => s.Start).ToList();

    public static string ConcatLine(string path) => "file '" + Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''") + "'";

    public static async Task<MediaDetails> InspectAsync(string file, CancellationToken cancellation = default)
    {
        var engine = await CaptureEngine.EnsureAsync();
        var probe = Path.Combine(Path.GetDirectoryName(engine)!, "ffprobe.exe");
        var (code, json) = await CaptureEngine.RunAsync(probe, ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", file], cancellation);
        if (code != 0) throw new IOException("This media file couldn't be read.");
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind != JsonValueKind.Object) throw new IOException("No video track was found.");
        var duration = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        return new(double.Parse(duration!, CultureInfo.InvariantCulture), video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"));
    }

    public static async Task<string> ExportAsync(string source, string destination, double start, double duration, int mbps, bool gif, CancellationToken cancellation = default)
    {
        if (start < 0 || duration <= 0 || !double.IsFinite(start + duration)) throw new ArgumentOutOfRangeException(nameof(duration));
        var engine = await CaptureEngine.EnsureAsync();
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-ss", Number(start), "-t", Number(duration) };
        if (gif) arguments.AddRange(["-an", "-filter_complex", "fps=15,scale=960:-1:force_original_aspect_ratio=decrease,split[a][b];[a]palettegen[p];[b][p]paletteuse"]);
        else arguments.AddRange(["-c:v", "libopenh264", "-b:v", $"{mbps}M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Guid.NewGuid().ToString("N") + (gif ? ".gif" : ".mp4"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        arguments.Add(temporary);
        try
        {
            var (code, error) = await CaptureEngine.RunAsync(engine, arguments, cancellation);
            if (code != 0) throw new IOException("Export failed. " + error);
            if (!gif) _ = await InspectAsync(temporary, cancellation);
            System.IO.File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally { try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); } catch { } }
    }
    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
