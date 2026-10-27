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
}
