using System.Diagnostics;
using System.Text.Json;

namespace Rig.Services;

/// <summary>One recording session feeds both replay clips and manual recordings. Nothing starts until requested.</summary>
public sealed class CaptureStudio
{
    readonly AppSettings _settings;
    readonly Func<ScreenArea, AppSettings, Task<CaptureRun>> _start;
    readonly SemaphoreSlim _gate = new(1);
    CaptureRun? _run;
    double? _manualStart;
    string _label = "Desktop";
    volatile bool _replay;
    int _operation;
    public bool Busy => Volatile.Read(ref _operation) != 0;
    public bool ReplayEnabled => _replay;
    public bool IsRecording => _manualStart is not null;
    public bool Active => _run is not null;
    public TimeSpan Elapsed => _run is null ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, _run.Elapsed.TotalSeconds - (_manualStart ?? 0)));
    public double BufferedSeconds => _run is null ? 0 : Math.Min(_settings.ReplaySeconds, _run.Elapsed.TotalSeconds);
    public string Encoder => _run?.Encoder ?? "Automatic hardware selection";
    public string Status { get; private set; } = "Ready when you are. Enable replay before the moment happens.";
    public string? LastSaved { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<RecoverableCapture> Recoverable => CaptureRecovery.Find(_run?.Directory);
    public event Action? Changed;
    public event Action<string>? Saved;
    public CaptureStudio(AppSettings settings) : this(settings, (area, options) =>
        Environment.GetEnvironmentVariable("RIG_TEST_CAPTURE_SYNTHETIC") == "1"
            ? CaptureRun.StartAsync(new ScreenArea(0, 0, 320, 180), options, synthetic: true)
            : CaptureRun.StartAsync(area, options)) { }
    internal CaptureStudio(AppSettings settings, Func<ScreenArea, AppSettings, Task<CaptureRun>> start)
    {
        _settings = settings; _start = start;
        _ = Task.Run(MaintainAsync);
    }
    bool _shutdown;
    public string Folder => string.IsNullOrWhiteSpace(_settings.CaptureFolder)
        ? ScreenRecorder.Folder : _settings.CaptureFolder;

    public Task ToggleReplayAsync(ScreenArea area, string label) => ExecuteAsync(async () =>
    {
        if (_replay)
        {
            _replay = false;
            if (!IsRecording) await CloseRunAsync();
            Status = IsRecording ? "Replay off. Your manual recording is still running." : "Replay off. Saved clips are kept.";
        }
        else
        {
            await EnsureRunAsync(area, label);
            _replay = true;
            Status = "Replay is on. Save a clip whenever you want to keep the moment.";
        }
    });

    public Task ToggleRecordingAsync(ScreenArea area, string label) => ExecuteAsync(async () =>
    {
        if (_manualStart is not null) { await FinishRecordingAsync(); return; }
        bool fresh = _run is null;
        await EnsureRunAsync(area, label);
        _manualStart = fresh ? 0 : _run!.Elapsed.TotalSeconds;
        WriteMarker(_run!.Directory, new RecoveryRecording(_manualStart.Value, _label));
        Status = "Recording. Game audio and microphone follow your selected settings.";
    });

    public Task SaveReplayAsync() => ExecuteAsync(async () =>
    {
        if (!_replay || _run is null) throw new InvalidOperationException("Enable replay first. Rig can only save what it has already buffered.");
        double end = _run.Elapsed.TotalSeconds;
        if (end < .5) throw new InvalidOperationException("Replay is warming up. Give it a second, then save your clip.");
        Status = "Saving your replay clip…"; Changed?.Invoke();
        await SaveRangeAsync(_run, Math.Max(0, end - _settings.ReplaySeconds), end, "Clips");
        Status = "Clip saved. Replay is still running.";
    });

    public Task RecoverAsync() => ExecuteAsync(async () =>
    {
        foreach (var capture in Recoverable)
        {
            Status = "Recovering completed recording segments…"; Changed?.Invoke();
            LastSaved = await CaptureRecovery.RecoverAsync(capture, Folder);
            Saved?.Invoke(LastSaved);
        }
        Status = "Recovered recordings are in your library.";
    });

    async Task EnsureRunAsync(ScreenArea area, string label)
    {
        if (_run is not null) return;
        CheckSpace(Folder);
        CheckSpace(Places.LocalAppData);
        Status = "Preparing capture and audio…"; Changed?.Invoke();
        // A snapshot keeps running capture settings stable while other preferences change.
        var options = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        options.Normalize(); _label = label;
        _run = await _start(area with { Width = area.Width & ~1, Height = area.Height & ~1 }, options);
    }

    async Task FinishRecordingAsync()
    {
        if (_run is not { } run || _manualStart is not { } start) return;
        Status = "Finishing and saving your recording…"; Changed?.Invoke();
        double end = run.Elapsed.TotalSeconds;
        if (!_replay) { await run.StopAsync(); end = run.Elapsed.TotalSeconds; }
        await SaveRangeAsync(run, start, end, "Recordings");
        System.IO.File.Delete(Path.Combine(run.Directory, "recording.json"));
        _manualStart = null;
        if (!_replay) await CloseRunAsync();
        Status = "Recording saved.";
    }

    async Task SaveRangeAsync(CaptureRun run, double start, double end, string category)
    {
        var timeout = Stopwatch.StartNew();
        IReadOnlyList<ReplaySegment> all;
        while (true)
        {
            all = ReplayMedia.ReadSegments(run.Directory);
            if (all.Any(s => s.End >= end - .05) || run.HasExited) break;
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new IOException("The next replay segment didn't finish in time. Your buffer has been kept; try saving again.");
            await Task.Delay(100);
        }
        var selected = ReplayMedia.Select(all, start, end);
        if (selected.Count == 0) throw new IOException("No completed video was available to save.");
        string folder = Path.Combine(Folder, category);
        Directory.CreateDirectory(folder);
        string name = string.Concat(_label.Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) name = "Capture";
        name = name[..Math.Min(80, name.Length)];
        string destination = Path.Combine(folder, $"{name} {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid().ToString("N")[..5]}.mp4");
        string list = Path.Combine(run.Directory, "export-" + Guid.NewGuid().ToString("N") + ".txt");
        await System.IO.File.WriteAllLinesAsync(list, selected.Select(s => ReplayMedia.ConcatLine(s.Path)));
        string temporary = Path.Combine(folder, ".saving-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            double offset = Math.Max(0, start - selected[0].Start);
            double duration = Math.Min(end, selected[^1].End) - Math.Max(start, selected[0].Start);
            if (duration <= 0) throw new IOException("The requested recording interval was empty.");
            var engine = await CaptureEngine.EnsureAsync();
            bool requiresAudio = run.Options.CaptureSystemAudio || run.Options.CaptureMicrophone;
            string? problem = null;
            // Segments begin on forced keyframes, so a recording can be copied from its first segment without re-encoding an
            // hour of video; it may start up to one segment early. Clips stay frame-accurate, and a copy that fails is re-encoded.
            if (category == "Recordings")
            {
                double copied = Math.Min(end, selected[^1].End) - selected[0].Start;
                problem = await FinalizeAsync(engine, ["-f", "concat", "-safe", "0", "-i", list, "-t", ReplayMedia.Number(copied), "-c", "copy"],
                    temporary, TimeSpan.FromMinutes(30), requiresAudio);
            }
            if (category != "Recordings" || problem is not null)
            {
                problem = await FinalizeAsync(engine, ["-f", "concat", "-safe", "0", "-i", list, "-ss", ReplayMedia.Number(offset), "-t", ReplayMedia.Number(duration),
                    "-c:v", run.Encoder, "-b:v", $"{run.Options.CaptureMbps}M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"],
                    temporary, TimeSpan.FromMinutes(Math.Max(30, duration / 20)), requiresAudio);
            }
            if (problem is not null) throw new IOException(problem);
            System.IO.File.Move(temporary, destination, false);
            LastSaved = destination; Saved?.Invoke(destination);
        }
        finally { try { System.IO.File.Delete(list); System.IO.File.Delete(temporary); } catch { } }
    }

    /// <summary>Writes and verifies one MP4. Returns why it failed, leaving the source segments untouched either way.</summary>
    internal static async Task<string?> FinalizeAsync(string engine, IEnumerable<string> input, string temporary, TimeSpan limit, bool requiresAudio)
    {
        try { System.IO.File.Delete(temporary); } catch (IOException) { }
        using var deadline = new CancellationTokenSource(limit);
        int code; string error;
        try { (code, error) = await CaptureEngine.RunAsync(engine, ["-hide_banner", "-loglevel", "error", "-nostdin", .. input, "-movflags", "+faststart", "-y", temporary], deadline.Token); }
        catch (OperationCanceledException) { return "Finishing the video took too long. Recovery segments have been kept."; }
        if (code != 0) return "Could not finish the video. " + error;
        try
        {
            var details = await ReplayMedia.InspectAsync(temporary);
            if (details.Duration <= 0 || requiresAudio && !details.HasAudio) return "The saved video is missing a requested track. Recovery segments have been kept.";
        }
        catch (Exception problem) when (problem is not OperationCanceledException) { return "The saved video couldn't be read back. " + problem.Message; }
        return null;
    }

    async Task ExecuteAsync(Func<Task> action)
    {
        if (_shutdown || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
        Changed?.Invoke();
        await _gate.WaitAsync();
        LastError = null;
        try { await action(); }
        catch (Exception error) { LastError = error.Message; Status = error.Message; }
        finally { Interlocked.Exchange(ref _operation, 0); _gate.Release(); Changed?.Invoke(); }
    }

    async Task MaintainAsync()
    {
        while (!_shutdown)
        {
            await Task.Delay(2000);
            if (!await _gate.WaitAsync(0)) continue;
            try
            {
                if (_run is not { } run) continue;
                if (run.HasExited || run.AudioError is not null)
                {
                    Status = "Capture stopped unexpectedly. " + (run.AudioError?.Message ?? run.Error);
                    LastError = Status; _replay = false; _manualStart = null;
                    await CloseRunAsync(keepRecovery: true); Changed?.Invoke(); continue;
                }
                CheckSpace(Folder); CheckSpace(Places.LocalAppData);
                double cutoff = run.Elapsed.TotalSeconds - run.Options.ReplaySeconds - 6;
                if (_manualStart is { } manual) cutoff = Math.Min(cutoff, manual);
                foreach (var segment in ReplayMedia.ReadSegments(run.Directory).Where(s => s.End < cutoff))
                    try { System.IO.File.Delete(segment.Path); } catch (IOException) { }
            }
            catch (Exception error)
            {
                LastError = error.Message; Status = error.Message; _replay = false; _manualStart = null;
                try { await CloseRunAsync(keepRecovery: true); } catch { }
                Changed?.Invoke();
            }
            finally { _gate.Release(); }
        }
    }

    internal static void CheckSpace(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < 512L * 1024 * 1024)
            throw new IOException("Less than 512 MB of free disk space remains. Capture has stopped to keep your PC usable. Free some space before recording again.");
    }
    async Task CloseRunAsync(bool keepRecovery = false)
    {
        if (_run is not { } run) return;
        try { await run.DisposeAsync(); }
        finally { _run = null; }
        if (!keepRecovery && !System.IO.File.Exists(Path.Combine(run.Directory, "recording.json")))
        {
            // This directory is created by CaptureRun under our replay cache; no arbitrary user path is accepted.
            string cache = Path.GetFullPath(Path.Combine(CaptureEngine.CacheRoot, "Replay")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(run.Directory).StartsWith(cache, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(run.Directory, true); } catch (IOException) { }
        }
    }
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _shutdown = true; _replay = false;
            if (IsRecording) await FinishRecordingAsync();
            else await CloseRunAsync();
        }
        finally { _gate.Release(); }
    }
    public async Task SuspendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _replay = false;
            if (IsRecording) await FinishRecordingAsync();
            else await CloseRunAsync();
            Status = "Capture paused. Saved files are kept.";
        }
        catch (Exception error) { LastError = error.Message; Status = error.Message; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    public bool StopAndWait(TimeSpan timeout) => Task.Run(ShutdownAsync).Wait(timeout);
    internal sealed record RecoveryRecording(double Start, string Label);
    /// <summary>The marker is what makes an interrupted recording recoverable, so a crash mid-write must not leave half of one.</summary>
    internal static void WriteMarker(string directory, RecoveryRecording recording)
    {
        string marker = Path.Combine(directory, "recording.json"), tmp = marker + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(recording));
        System.IO.File.Move(tmp, marker, true);
    }
}
