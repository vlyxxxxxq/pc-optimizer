using System.Text;
using Rig.Services;

namespace Rig.Tests;

/// <summary>
/// Needs an interactive desktop and Windows' Media Foundation H.264 encoder (it records a 64×64 corner of the
/// screen; nothing on screen changes). Excluded from CI with: dotnet test --filter Category!=Desktop
/// </summary>
[Trait("Category", "Desktop")]
public class RecorderTests
{
    static bool HasIndex(string file)
    {
        // An MP4 is playable once its index ("moov" box) has been written, which happens when the writer finishes.
        // Shared read: the writer may still have it open (which is exactly the unfinished case).
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Encoding.ASCII.GetString(ms.ToArray()).Contains("moov");
    }

    [Fact]
    public void Stopping_without_waiting_leaves_an_unfinished_file_at_that_moment()
    {
        using var box = new Sandbox();
        box.Env("RIG_TEST_RECORDINGS", box.Dir("rec"));
        var rec = new ScreenRecorder();
        rec.Start(new ScreenArea(0, 0, 64, 64), gif: false, cursor: false, "test");
        Thread.Sleep(1500);
        rec.Stop();                       // the old exit path: signal and move on
        var file = rec.File!;
        bool finishedRightAway = File.Exists(file) && HasIndex(file);
        Assert.True(rec.StopAndWait(TimeSpan.FromSeconds(20)));
        Assert.False(finishedRightAway, "the file was already complete; the race couldn't be reproduced on this machine");
        Assert.True(HasIndex(file));
    }

    [Fact]
    public void Stop_and_wait_leaves_a_complete_mp4()
    {
        using var box = new Sandbox();
        box.Env("RIG_TEST_RECORDINGS", box.Dir("rec"));
        var rec = new ScreenRecorder();
        string? finished = null, error = null;
        rec.Finished += (f, e) => { finished = f; error = e; };
        rec.Start(new ScreenArea(0, 0, 64, 64), gif: false, cursor: false, "test");
        Thread.Sleep(1500);
        Assert.True(rec.StopAndWait(TimeSpan.FromSeconds(20)));
        Assert.Null(error);
        Assert.NotNull(finished);
        Assert.True(HasIndex(finished!));
        Assert.False(rec.IsRecording);
    }
}
