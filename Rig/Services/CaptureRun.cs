using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Rig.Services;

internal sealed class CaptureRun : IAsyncDisposable
{
    readonly Process _process;
    readonly CaptureAudio? _audio;
    readonly StringBuilder _errors = new();
    readonly Task _readError, _readProgress;
    readonly SemaphoreSlim _stop = new(1);
    long _microseconds;
    public string Directory { get; }
    public string Encoder { get; }
    internal int ProcessId => _process.Id;
    public AppSettings Options { get; }
    public bool HasExited => _process.HasExited;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _microseconds) * 10);
    public string Error { get { lock (_errors) return _errors.ToString(); } }
    public Exception? AudioError => _audio?.Error;
    const string Software = "libopenh264";
    static readonly string[] Encoders = ["h264_nvenc", "h264_amf", "h264_qsv", Software];
    // Per process: the encoder that passed a probe at each output size/rate, and hardware encoders that later failed a real session.
    static readonly ConcurrentDictionary<string, string> Chosen = new();
    static readonly ConcurrentDictionary<string, bool> Demoted = new();

    CaptureRun(Process process, CaptureAudio? audio, string directory, string encoder, AppSettings options)
    {
        (_process, _audio, Directory, Encoder, Options) = (process, audio, directory, encoder, options);
        _readError = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                lock (_errors) { if (_errors.Length > 12000) _errors.Remove(0, 6000); _errors.AppendLine(line); }
        });
        _readProgress = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long time)) Interlocked.Exchange(ref _microseconds, Math.Max(0, time));
        });
    }

    public static async Task<CaptureRun> StartAsync(ScreenArea area, AppSettings options, bool synthetic = false)
    {
        var engine = await CaptureEngine.EnsureAsync();
        if (synthetic) return await LaunchAsync(engine, area, options, Software, synthetic: true);
        var (width, height) = OutputSize(area, options);
        string key = $"{width}x{height}@{options.CaptureFps}/{options.CaptureMbps}";
        string encoder = Chosen.TryGetValue(key, out var known) && !Demoted.ContainsKey(known)
            ? known : Chosen[key] = await ChooseEncoderAsync(engine, width, height, options);
        return await StartWithFallbackAsync(encoder, e => LaunchAsync(engine, area, options, e), failed =>
        {
            Demoted[failed] = true;
            Chosen[key] = Software;
        });
    }

    /// <summary>
    /// A hardware encoder can pass the probe yet fail with a real display and audio feed (driver limits, another app holding
    /// the encoder). Rig then tries the software encoder once, after the failed process has fully exited, so two capture
    /// sessions never run together. Audio failures aren't retried because a different encoder can't fix them.
    /// </summary>
    internal static async Task<T> StartWithFallbackAsync<T>(string encoder, Func<string, Task<T>> launch, Action<string> demote)
    {
        try { return await launch(encoder); }
        catch (IOException first) when (encoder != Software && first is not AudioStartException)
        {
            demote(encoder);
            try { return await launch(Software); }
            catch (IOException second) { throw new IOException($"Recording couldn't start with {encoder} or the software encoder. {second.Message}", second); }
        }
    }

    internal static async Task<CaptureRun> LaunchAsync(string engine, ScreenArea area, AppSettings options, string encoder, bool synthetic = false)
    {
        var directory = Path.Combine(CaptureEngine.CacheRoot, "Replay", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        CaptureAudio? audio = null;
        try
        {
            if (!synthetic && (options.CaptureSystemAudio || options.CaptureMicrophone)) audio = new CaptureAudio(options);
            var arguments = Arguments(area, options, directory, encoder, audio?.PipePath, synthetic);
            var process = CaptureEngine.Launch(engine, arguments);
            var run = new CaptureRun(process, audio, directory, encoder, options);
            try
            {
                audio?.Start();
                var started = Stopwatch.StartNew();
                while (run.Elapsed <= TimeSpan.Zero && !run.HasExited && run.AudioError is null && started.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
                if (run.AudioError is not null) throw new AudioStartException("Recording couldn't start. " + run.AudioError.Message);
                if (run.HasExited || run.Elapsed <= TimeSpan.Zero) throw new IOException("Recording couldn't start. " + run.Error);
                return run;
            }
            catch
            {
                // Stop the child before the caller retries, so a fallback never overlaps a half-started session.
                try { await run.DisposeAsync(); } catch (IOException) { }
                throw;
            }
        }
        catch
        {
            audio?.Dispose();
            // Nothing in this new folder was ever announced as recording, so it isn't recoverable data.
            try { System.IO.Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    sealed class AudioStartException(string message) : IOException(message);

    /// <summary>The size the scale filter in <see cref="Arguments"/> produces: height capped at the preset, width kept even.</summary>
    internal static (int Width, int Height) OutputSize(ScreenArea area, AppSettings options)
    {
        int height = Math.Min(area.Height, options.CaptureHeight);
        int width = (int)Math.Round(area.Width * (double)height / Math.Max(1, area.Height) / 2) * 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    internal static List<string> Arguments(ScreenArea area, AppSettings options, string directory, string encoder, string? pipe, bool synthetic = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-stats_period", "0.25", "-progress", "pipe:1" };
        if (synthetic) args.AddRange(["-re", "-f", "lavfi", "-i", $"testsrc2=size={area.Width}x{area.Height}:rate={options.CaptureFps}"]);
        else args.AddRange(["-thread_queue_size", "512", "-f", "gdigrab", "-framerate", options.CaptureFps.ToString(), "-draw_mouse", options.RecordCursor ? "1" : "0",
            "-offset_x", area.X.ToString(), "-offset_y", area.Y.ToString(), "-video_size", $"{area.Width}x{area.Height}", "-i", "desktop"]);
        if (synthetic && options.CaptureSystemAudio) args.AddRange(["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        else if (pipe is not null) args.AddRange(["-thread_queue_size", "1024", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", pipe]);
        args.AddRange(["-map", "0:v:0"]);
        if (pipe is not null || synthetic && options.CaptureSystemAudio) args.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);
        else args.Add("-an");
        args.AddRange(["-vf", $"scale=-2:min(ih\\,{options.CaptureHeight}):flags=lanczos,format=yuv420p", "-c:v", encoder, "-b:v", $"{options.CaptureMbps}M",
            "-g", (options.CaptureFps * 2).ToString(), "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced*2)",
            "-f", "segment", "-segment_time", "2", "-segment_format", "mpegts", "-reset_timestamps", "1", "-segment_list", Path.Combine(directory, "segments.csv"),
            "-segment_list_type", "csv", Path.Combine(directory, "part-%08d.ts")]);
        return args;
    }

    /// <summary>Probes each encoder with half a second at the real output size, frame rate, bitrate and GOP, not a token frame.</summary>
    static async Task<string> ChooseEncoderAsync(string engine, int width, int height, AppSettings options)
    {
        foreach (var encoder in Encoders.Where(e => !Demoted.ContainsKey(e)))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var (code, _) = await CaptureEngine.RunAsync(engine, ProbeArguments(encoder, width, height, options), timeout.Token);
                if (code == 0) return encoder;
            }
            // Software encoding is the last resort: a slow probe at a large size is not a reason to refuse to record.
            catch (OperationCanceledException) when (encoder == Software) { return encoder; }
            catch (OperationCanceledException) { }
        }
        throw new IOException("No compatible H.264 encoder is available on this PC.");
    }

    internal static List<string> ProbeArguments(string encoder, int width, int height, AppSettings options) =>
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={options.CaptureFps}",
            "-frames:v", (options.CaptureFps / 2).ToString(), "-pix_fmt", "yuv420p", "-c:v", encoder, "-b:v", $"{options.CaptureMbps}M",
            "-g", (options.CaptureFps * 2).ToString(), "-bf", "0", "-f", "null", "-"];

    public async Task StopAsync()
    {
        await _stop.WaitAsync();
        try
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("q"); await _process.StandardInput.FlushAsync(); } catch (IOException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _process.Kill(true); await _process.WaitForExitAsync(); throw new IOException("Recording took too long to finish. Recovery files have been kept."); }
            }
            await Task.WhenAll(_readError, _readProgress);
        }
        finally { _stop.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { _audio?.Dispose(); _process.Dispose(); }
    }
}
