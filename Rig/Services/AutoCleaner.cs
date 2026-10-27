using System.Windows.Threading;

namespace Rig.Services;

/// <summary>
/// Runs the junk cleaner on a schedule while the app is open (it lives in the tray), using only the
/// categories that are ticked by default, so never the Recycle Bin. Skips runs while a game is on.
/// </summary>
public sealed class AutoCleaner
{
    readonly AppSettings _settings;
    readonly Func<bool> _isGaming;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    bool _running;

    /// <summary>(title, message) for a notification after a run.</summary>
    public event Action<string, string>? Notify;
    public event EventHandler? Ran;

    public AutoCleaner(AppSettings settings, Func<bool> isGaming)
    {
        _settings = settings;
        _isGaming = isGaming;
        _timer.Tick += async (_, _) => await RunIfDueAsync();
    }

    bool _stopped;

    public void Stop() { _stopped = true; _timer.Stop(); }

    public void Start()
    {
        _stopped = false;
        _timer.Start();
        // First check shortly after startup, not during it.
        var once = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        once.Tick += async (_, _) => { once.Stop(); await RunIfDueAsync(); };
        once.Start();
    }

    public DateTime? NextRun => _settings.AutoClean
        ? (_settings.LastAutoClean ?? DateTime.Now).AddDays(_settings.LastAutoClean is null ? 0 : _settings.AutoCleanDays)
        : null;

    async Task RunIfDueAsync()
    {
        if (_stopped || !_settings.AutoClean || _running || _isGaming()) return;
        if (_settings.LastAutoClean is { } last && DateTime.Now - last < TimeSpan.FromDays(_settings.AutoCleanDays)) return;
        await RunNowAsync();
    }

    public async Task<long> RunNowAsync()
    {
        if (_running) return 0;
        _running = true;
        try
        {
            var categories = JunkCleaner.CreateCategories().Where(c => c.IsSelected && !c.IsRecycleBin).ToList();
            long freed = await Task.Run(() =>
            {
                long total = 0;
                foreach (var c in categories)
                {
                    var scan = JunkCleaner.Scan(c);
                    if (scan.Count == 0) continue;
                    c.ApplyScan(scan);
                    total += JunkCleaner.Clean(c).Freed;
                }
                return total;
            });
            _settings.LastAutoClean = DateTime.Now;
            _settings.Save();
            if (freed > 0) Notify?.Invoke("Automatic cleanup done", $"Freed {Fmt.Bytes(freed)} of temporary files and caches.");
            Ran?.Invoke(this, EventArgs.Empty);
            return freed;
        }
        finally { _running = false; }
    }
}
