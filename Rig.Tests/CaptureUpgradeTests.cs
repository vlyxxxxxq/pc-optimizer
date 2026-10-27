using Rig.Services;

namespace Rig.Tests;

public class CaptureUpgradeTests
{
    [Fact]
    public async Task User_capture_action_waits_for_background_cleanup_instead_of_being_dropped()
    {
        using var box = new Sandbox();
        int starts = 0;
        var studio = new CaptureStudio(new AppSettings { CaptureFolder = box.Dir("captures") }, (_, _) =>
        { starts++; return Task.FromException<CaptureRun>(new IOException("Expected test startup failure")); });
        var gate = (SemaphoreSlim)typeof(CaptureStudio).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(studio)!;
        await gate.WaitAsync();
        Task pending;
        try
        {
            pending = studio.ToggleReplayAsync(new ScreenArea(0, 0, 320, 180), "Test");
            Assert.False(pending.IsCompleted);
        }
        finally { gate.Release(); }
        try { await pending; Assert.Equal(1, starts); }
        finally { await studio.ShutdownAsync(); }
    }

    [Fact]
    public void Dragging_moves_before_or_after_without_losing_hidden_cards()
    {
        string[] cards = ["gamemode", "cpu", "music", "pins"];
        Assert.Equal(new[] { "cpu", "music", "gamemode", "pins" }, HomeLayout.Reorder(cards, "gamemode", "pins", false));
        Assert.Equal(new[] { "cpu", "music", "pins", "gamemode" }, HomeLayout.Reorder(cards, "gamemode", "pins", true));
        Assert.Equal(cards, HomeLayout.Reorder(cards, "unknown", "pins", true));
    }
    [Fact]
    public void Legacy_heat_counts_survive_the_higher_resolution_grid()
    {
        var old = new int[64 * 36]; old[0] = 10; old[63] = 3; old[^1] = 100;
        var upgraded = Heatmaps.Upgrade(old, 320, 180);
        Assert.Equal(113, upgraded.Sum());
        Assert.Equal(320 * 180, upgraded.Length);
        var smoothed = Heatmaps.Smooth(upgraded, 320, 180);
        Assert.True(smoothed.Count(v => v > 0) > 3);
        Assert.All(smoothed, v => Assert.True(double.IsFinite(v) && v >= 0));
    }
    [Fact]
    public void Shortcut_edits_detect_cross_page_conflicts_and_unbinding_survives_load()
    {
        var settings = new AppSettings { PaletteHotkey = "", ClipHotkey = "Alt+F8" };
        settings.Normalize();
        Assert.Equal("", settings.PaletteHotkey);
        Assert.Equal("Save replay clip", Shortcuts.Conflict(settings, nameof(AppSettings.CrosshairHotkey), Hotkey.Parse("Alt+F8")!.Value));
        Assert.Equal("Win+Shift+F4", Hotkey.Parse("Shift+Win+F4")!.Value.ToString().Replace("Shift+Win", "Win+Shift"));
    }
    [Fact]
    public void Capture_import_does_not_silently_enable_microphone_or_automatic_replay()
    {
        var target = new AppSettings();
        SettingsPolicy.Apply(new AppSettings { CaptureMicrophone = true, ReplayWithGameMode = true, ReplaySeconds = 60 }, target, new ImportChoices());
        Assert.False(target.CaptureMicrophone); Assert.False(target.ReplayWithGameMode); Assert.Equal(60, target.ReplaySeconds);
    }
    [Fact]
    public async Task Synthetic_replay_contains_readable_video_and_audio_without_capturing_the_desktop()
    {
        using var box = new Sandbox();
        var options = new AppSettings { CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true, CaptureMicrophone = false };
        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        await using var run = await CaptureRun.StartAsync(new ScreenArea(0, 0, 320, 180), options, synthetic: true);
        await Task.Delay(4500);
        await run.StopAsync();
        // Startup and graceful shutdown can take longer on a busy CI runner; the
        // generated media must still advance in real time rather than run ahead.
        Assert.InRange(run.Elapsed.TotalSeconds, 4, wallClock.Elapsed.TotalSeconds + 1);
        var segments = ReplayMedia.ReadSegments(run.Directory);
        Assert.True(segments.Count >= 2, run.Error);
        var details = await ReplayMedia.InspectAsync(segments[0].Path);
        Assert.True(details.HasAudio);
        Assert.Equal(320, details.Width);
        Assert.True(details.Duration > 1);
        var exported = Path.Combine(box.Dir("exports"), "trimmed.mp4");
        await ReplayMedia.ExportAsync(segments[0].Path, exported, .2, 1, 4, false);
        var trimmed = await ReplayMedia.InspectAsync(exported);
        Assert.InRange(trimmed.Duration, .9, 1.2);
        Assert.True(trimmed.HasAudio);
    }

    [Fact]
    public async Task Replay_and_manual_recording_share_a_session_and_both_save_valid_files()
    {
        using var box = new Sandbox();
        var settings = new AppSettings { CaptureFolder = box.Dir("captures"), CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true };
        var studio = new CaptureStudio(settings, (area, options) => CaptureRun.StartAsync(area, options, synthetic: true));
        var area = new ScreenArea(0, 0, 320, 180);
        var saved = new List<string>(); studio.Saved += file => saved.Add(file);
        try
        {
            await studio.ToggleReplayAsync(area, "Test Game");
            Assert.Null(studio.LastError); Assert.True(studio.ReplayEnabled);
            await Task.Delay(2200);
            await studio.ToggleRecordingAsync(area, "Test Game");
            Assert.True(studio.IsRecording);
            await Task.Delay(2200);
            await studio.SaveReplayAsync();
            Assert.Null(studio.LastError); Assert.True(studio.IsRecording);
            await studio.ToggleReplayAsync(area, "Test Game");
            Assert.False(studio.ReplayEnabled); Assert.True(studio.IsRecording);
            await studio.ToggleRecordingAsync(area, "Test Game");
            Assert.Null(studio.LastError); Assert.False(studio.IsRecording); Assert.False(studio.Active);
            Assert.Equal(2, saved.Count);
            foreach (var file in saved)
            {
                var media = await ReplayMedia.InspectAsync(file);
                Assert.True(media.HasAudio); Assert.True(media.Duration > 1);
            }
        }
        finally { await studio.ShutdownAsync(); }
    }

    [Fact]
    public async Task Interrupted_recording_can_be_recovered_without_touching_its_source_segments()
    {
        using var box = new Sandbox();
        var options = new AppSettings { CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true };
        await using var run = await CaptureRun.StartAsync(new ScreenArea(0, 0, 320, 180), options, synthetic: true);
        await Task.Delay(2300); await run.StopAsync();
        var marker = Path.Combine(run.Directory, "recording.json");
        File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new CaptureStudio.RecoveryRecording(.1, "Interrupted game")));
        var recovery = Assert.Single(CaptureRecovery.Find());
        int segments = ReplayMedia.ReadSegments(run.Directory).Count;
        var file = await CaptureRecovery.RecoverAsync(recovery, box.Dir("recovered"));
        Assert.True((await ReplayMedia.InspectAsync(file)).HasAudio);
        Assert.False(File.Exists(marker));
        Assert.Empty(CaptureRecovery.Find());
        Assert.Equal(segments, ReplayMedia.ReadSegments(run.Directory).Count);
    }

    [Fact, Trait("Category", "Desktop")]
    public async Task Windows_system_audio_reaches_the_recording_pipe_without_saving_audio()
    {
        using var audio = new CaptureAudio(new AppSettings { CaptureSystemAudio = true, CaptureMicrophone = false });
        audio.Start();
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", Path.GetFileName(audio.PipePath), System.IO.Pipes.PipeDirection.In, System.IO.Pipes.PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(timeout.Token);
        var bytes = new byte[3840];
        Assert.True(await client.ReadAsync(bytes, timeout.Token) > 0);
        Assert.Null(audio.Error);
    }

    [Fact]
    public void Input_stats_save_atomically_and_load_the_last_good_copy_after_damage()
    {
        using var box = new Sandbox();
        string path = Path.Combine(box.Profile, "inputstats.json");
        var first = new InputStatsData(); first.Days["2026-10-26"] = new InputDay { Keys = 41 };
        InputStats.Write(path, System.Text.Json.JsonSerializer.Serialize(first));
        var second = new InputStatsData(); second.Days["2026-10-27"] = new InputDay { Keys = 42 };
        InputStats.Write(path, System.Text.Json.JsonSerializer.Serialize(second));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(42, InputStats.Load(path).Days["2026-10-27"].Keys);

        File.WriteAllText(path, "{\"Days\":{\"2026-10-27\":"); // a write torn by power loss
        var recovered = InputStats.Load(path);
        Assert.Equal(41, recovered.Days["2026-10-26"].Keys);
        Assert.Single(Directory.GetFiles(box.Profile, "inputstats.corrupt-*.json"));
        Assert.Equal(InputStatsData.HeatW * InputStatsData.HeatH, recovered.MoveHeat.Length);
    }

    [Fact]
    public async Task Encoder_probe_uses_the_requested_output_size_rate_and_bitrate()
    {
        using var box = new Sandbox();
        var options = new AppSettings { CaptureFps = 60, CaptureHeight = 1080, CaptureMbps = 24 };
        Assert.Equal((1920, 1080), CaptureRun.OutputSize(new ScreenArea(0, 0, 2560, 1440), options));
        Assert.Equal((2580, 1080), CaptureRun.OutputSize(new ScreenArea(0, 0, 3440, 1440), options));
        Assert.Equal((1280, 720), CaptureRun.OutputSize(new ScreenArea(0, 0, 1280, 720), options));
        var args = CaptureRun.ProbeArguments("libopenh264", 1920, 1080, options);
        Assert.Contains("testsrc2=size=1920x1080:rate=60", args);
        Assert.Equal("24M", args[args.IndexOf("-b:v") + 1]);
        Assert.Equal("120", args[args.IndexOf("-g") + 1]);
        // The probe must be accepted by the pinned engine, otherwise every encoder would look unavailable.
        var (code, output) = await CaptureEngine.RunAsync(await CaptureEngine.EnsureAsync(), args);
        Assert.True(code == 0, output);
    }

    [Fact]
    public async Task A_hardware_encoder_that_fails_a_real_start_is_demoted_and_software_is_tried_once()
    {
        var launched = new List<string>(); var demoted = new List<string>();
        var result = await CaptureRun.StartWithFallbackAsync("h264_nvenc", encoder =>
        {
            launched.Add(encoder);
            return encoder == "h264_nvenc" ? Task.FromException<string>(new IOException("OpenEncodeSessionEx failed")) : Task.FromResult(encoder);
        }, demoted.Add);
        Assert.Equal("libopenh264", result);
        Assert.Equal(new[] { "h264_nvenc", "libopenh264" }, launched);
        Assert.Equal(new[] { "h264_nvenc" }, demoted);

        launched.Clear(); demoted.Clear();
        var both = await Assert.ThrowsAsync<IOException>(() => CaptureRun.StartWithFallbackAsync("h264_amf", encoder =>
        { launched.Add(encoder); return Task.FromException<string>(new IOException("no frames")); }, demoted.Add));
        Assert.Contains("h264_amf", both.Message);
        Assert.Equal(2, launched.Count);

        launched.Clear();
        await Assert.ThrowsAsync<IOException>(() => CaptureRun.StartWithFallbackAsync("libopenh264", encoder =>
        { launched.Add(encoder); return Task.FromException<string>(new IOException("no frames")); }, demoted.Add));
        Assert.Single(launched);
    }

    [Fact]
    public async Task A_real_failed_start_is_cleaned_up_before_the_software_retry_records()
    {
        using var box = new Sandbox();
        var options = new AppSettings { CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true };
        var engine = await CaptureEngine.EnsureAsync();
        var area = new ScreenArea(0, 0, 320, 180);
        var demoted = new List<string>();
        await using var run = await CaptureRun.StartWithFallbackAsync("h264_rig_missing",
            encoder => CaptureRun.LaunchAsync(engine, area, options, encoder, synthetic: true), demoted.Add);
        Assert.Equal("libopenh264", run.Encoder);
        Assert.Equal(new[] { "h264_rig_missing" }, demoted);
        // The failed attempt's empty cache folder is gone; only the running session remains.
        Assert.Equal(new[] { run.Directory }, Directory.GetDirectories(Path.Combine(CaptureEngine.CacheRoot, "Replay")));
        await Task.Delay(2300); await run.StopAsync();
        Assert.NotEmpty(ReplayMedia.ReadSegments(run.Directory));
    }

    [Fact]
    public async Task Killing_the_parent_stops_the_recorder_and_leaves_a_recoverable_recording()
    {
        using var box = new Sandbox();
        string report = Path.Combine(box.Root, "parent.txt");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("capture-parent");
        start.ArgumentList.Add(report);
        start.Environment["RIG_SETTINGS"] = Environment.GetEnvironmentVariable("RIG_SETTINGS");
        using var parent = System.Diagnostics.Process.Start(start)!;
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(report) && !parent.HasExited && waited.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
            Assert.True(File.Exists(report), "The capture parent didn't start recording.");
            var lines = File.ReadAllLines(report);
            using var recorder = System.Diagnostics.Process.GetProcessById(int.Parse(lines[0]));
            await Task.Delay(4500); // let a couple of segments complete
            parent.Kill(entireProcessTree: false); // a crash: no shutdown code runs in the parent
            await parent.WaitForExitAsync();
            using (var gone = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await recorder.WaitForExitAsync(gone.Token); // Windows closes the job and ends the recorder

            var recovery = Assert.Single(CaptureRecovery.Find());
            Assert.Equal(lines[1], recovery.Directory);
            var file = await CaptureRecovery.RecoverAsync(recovery, box.Dir("recovered"));
            var media = await ReplayMedia.InspectAsync(file);
            Assert.True(media.Duration > 1); Assert.True(media.HasAudio);
        }
        finally { if (!parent.HasExited) parent.Kill(true); }
    }

    [Fact]
    public async Task Long_recordings_are_copied_not_re_encoded_and_sources_survive()
    {
        using var box = new Sandbox();
        var settings = new AppSettings { CaptureFolder = box.Dir("captures"), CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true };
        CaptureRun? started = null;
        var studio = new CaptureStudio(settings, async (area, options) => started = await CaptureRun.StartAsync(area, options, synthetic: true));
        var area = new ScreenArea(0, 0, 320, 180);
        string? saved = null; studio.Saved += file => saved = file;
        try
        {
            await studio.ToggleReplayAsync(area, "Copy test");
            await Task.Delay(3000);
            await studio.ToggleRecordingAsync(area, "Copy test"); // starts mid-segment while replay runs
            await Task.Delay(4500);
            int before = ReplayMedia.ReadSegments(started!.Directory).Count;
            await studio.ToggleRecordingAsync(area, "Copy test");
            Assert.Null(studio.LastError);
            var media = await ReplayMedia.InspectAsync(saved!);
            Assert.True(media.HasAudio); Assert.InRange(media.Duration, 3.5, 9);
            Assert.True(ReplayMedia.ReadSegments(started.Directory).Count >= before); // finalizing never removes buffered sources
            Assert.Empty(Directory.GetFiles(Path.Combine(settings.CaptureFolder, "Recordings"), ".saving-*"));
        }
        finally { await studio.ShutdownAsync(); }
    }

    [Fact]
    public async Task A_failed_copy_falls_back_and_a_failed_finalize_reports_without_leaving_partial_files()
    {
        using var box = new Sandbox();
        var engine = await CaptureEngine.EnsureAsync();
        string temporary = Path.Combine(box.Dir("out"), ".saving-test.mp4");
        string? problem = await CaptureStudio.FinalizeAsync(engine, ["-f", "concat", "-safe", "0", "-i", Path.Combine(box.Root, "missing.txt"), "-c", "copy"],
            temporary, TimeSpan.FromSeconds(30), requiresAudio: true);
        Assert.StartsWith("Could not finish the video.", problem);
        problem = await CaptureStudio.FinalizeAsync(engine, ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "1", "-c:v", "libopenh264"],
            temporary, TimeSpan.FromSeconds(30), requiresAudio: true);
        Assert.Equal("The saved video is missing a requested track. Recovery segments have been kept.", problem);
        Assert.Null(await CaptureStudio.FinalizeAsync(engine, ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "1", "-c:v", "libopenh264"],
            temporary, TimeSpan.FromSeconds(30), requiresAudio: false));
    }
}
