using Rig.Services;

namespace Rig.Tests;

/// <summary>
/// The test SDK normally generates an empty entry point. This one adds a mode that only the crash-recovery test uses:
/// it starts a synthetic capture with a recording marker, reports the recorder's process id, and waits to be killed.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["capture-parent", var report]) return 0;
        var options = new AppSettings { CaptureFps = 30, CaptureHeight = 720, CaptureMbps = 4, CaptureSystemAudio = true };
        var run = await CaptureRun.StartAsync(new ScreenArea(0, 0, 320, 180), options, synthetic: true);
        CaptureStudio.WriteMarker(run.Directory, new CaptureStudio.RecoveryRecording(0, "Crashed game"));
        File.WriteAllText(report + ".tmp", $"{run.ProcessId}\n{run.Directory}");
        File.Move(report + ".tmp", report);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
