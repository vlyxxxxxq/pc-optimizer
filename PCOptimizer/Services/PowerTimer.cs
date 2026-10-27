using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PCOptimizer.Services;

public enum PowerAction { Shutdown, Restart, Sleep }

/// <summary>
/// Shut down, restart or sleep after a delay, or once downloads have finished: no partial browser downloads in
/// the Downloads folder, nothing in Steam's download folders, and no file written there for a minute.
/// A one-minute countdown with Cancel always comes first.
/// </summary>
public sealed class PowerTimer
{
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

    static readonly string[] Partial = [".crdownload", ".part", ".partial", ".download", ".opdownload", ".!ut", ".!qb", ".tmp"];
    static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Func<IEnumerable<string>> _steamLibraries;
    DateTime? _at;
    bool _afterDownloads;
    DateTime _quietSince;

    public PowerAction Action { get; private set; }
    public bool Armed => _at is not null || _afterDownloads;
    /// <summary>Raised when it's time; the window shows the countdown and calls Execute.</summary>
    public event Action<PowerAction>? Due;
    public event EventHandler? Changed;

    public PowerTimer(Func<IEnumerable<string>> steamLibraries)
    {
        _steamLibraries = steamLibraries;
        _timer.Tick += (_, _) => Tick();
    }

    public void ArmIn(PowerAction action, TimeSpan delay) { Action = action; _at = DateTime.Now + delay; _afterDownloads = false; _timer.Start(); Changed?.Invoke(this, EventArgs.Empty); }

    public void ArmAfterDownloads(PowerAction action)
    {
        Action = action; _at = null; _afterDownloads = true; _quietSince = DateTime.Now;
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel() { _at = null; _afterDownloads = false; _timer.Stop(); Changed?.Invoke(this, EventArgs.Empty); }

    public string Status => _at is { } at
        ? $"{Verb(Action)} at {at:t} (in {Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes))} min)."
        : _afterDownloads ? $"{Verb(Action)} when downloads finish. {Downloading()}" : "Off.";

    public static string Verb(PowerAction a) => a switch { PowerAction.Restart => "Restart", PowerAction.Sleep => "Sleep", _ => "Shut down" };

    void Tick()
    {
        if (_at is { } at && DateTime.Now >= at) { Fire(); return; }
        if (_afterDownloads)
        {
            if (Busy()) _quietSince = DateTime.Now;
            else if (DateTime.Now - _quietSince >= Quiet) { Fire(); return; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Fire()
    {
        var action = Action;
        Cancel();
        Due?.Invoke(action);
    }

    string Downloading()
    {
        var (files, steam) = Activity();
        if (files == 0 && steam == 0) return "Nothing is downloading right now.";
        var parts = new List<string>();
        if (files > 0) parts.Add($"{files} browser download{(files == 1 ? "" : "s")}");
        if (steam > 0) parts.Add($"{steam} Steam download{(steam == 1 ? "" : "s")}");
        return $"Waiting for {string.Join(" and ", parts)}.";
    }

    bool Busy()
    {
        var (files, steam) = Activity();
        if (files > 0 || steam > 0) return true;
        try
        {
            var folder = DownloadOrganizer.Folder;
            return Directory.Exists(folder) && new DirectoryInfo(folder).EnumerateFiles().Any(f => DateTime.Now - f.LastWriteTime < Quiet);
        }
        catch { return false; }
    }

    (int Files, int Steam) Activity()
    {
        int files = 0, steam = 0;
        try
        {
            var folder = DownloadOrganizer.Folder;
            if (Directory.Exists(folder))
                files = Directory.EnumerateFiles(folder).Count(f => Partial.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch { }
        foreach (var common in _steamLibraries())
        {
            try
            {
                // ...\steamapps\common\ → ...\steamapps\downloading\<appid> while an update or install is in progress.
                var downloading = Path.Combine(Path.GetDirectoryName(common.TrimEnd('\\'))!, "downloading");
                if (Directory.Exists(downloading)) steam += Directory.EnumerateDirectories(downloading).Count(d => Directory.EnumerateFileSystemEntries(d).Any());
            }
            catch { }
        }
        return (files, steam);
    }

    static void Run(string args) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

    /// <summary>Does it. PCOPTIMIZER_TEST_POWER=file writes the action there instead (for tests).</summary>
    public static void Execute(PowerAction action)
    {
        if (Environment.GetEnvironmentVariable("PCOPTIMIZER_TEST_POWER") is { Length: > 0 } test)
        {
            File.AppendAllText(test, action + Environment.NewLine);
            return;
        }
        switch (action)
        {
            case PowerAction.Sleep: SetSuspendState(false, false, false); break;
            case PowerAction.Restart: Run("/r /t 0"); break;
            default: Run("/s /t 0"); break;
        }
    }
}
