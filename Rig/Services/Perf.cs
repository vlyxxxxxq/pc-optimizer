using System.Diagnostics;
using System.Windows.Threading;

namespace Rig.Services;

/// <summary>
/// Opt-in timings for measuring the app (RIG_TEST_TIMINGS=file): each line is "name milliseconds". Off by default;
/// nothing is collected or sent anywhere.
/// </summary>
public static class Perf
{
    static readonly string? File = Environment.GetEnvironmentVariable("RIG_TEST_TIMINGS") is { Length: > 0 } f ? f : null;
    public static bool On => File is not null;

    /// <summary>Times from now until the UI has finished the work queued so far (layout and rendering included).</summary>
    public static void UntilIdle(Dispatcher dispatcher, string name)
    {
        if (File is null) return;
        var sw = Stopwatch.StartNew();
        dispatcher.BeginInvoke(() => Write(name, sw.Elapsed.TotalMilliseconds), DispatcherPriority.ContextIdle);
    }

    public static void Write(string name, double ms)
    {
        if (File is null) return;
        try { System.IO.File.AppendAllText(File, $"{name} {ms:0.0}{Environment.NewLine}"); } catch { }
    }
}
