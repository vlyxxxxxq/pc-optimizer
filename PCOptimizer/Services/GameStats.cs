using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCOptimizer.Services;

/// <summary>
/// Total 3D-engine GPU load from Windows' "GPU Engine" performance counters (what Task Manager shows),
/// read through PDH so no extra packages are needed. Returns the busiest GPU's load, 0–100.
/// </summary>
public sealed class GpuLoad : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;
    readonly IntPtr _query, _counter;
    readonly bool _ok;

    public GpuLoad()
    {
        _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 &&
              PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) == 0 &&
              PdhCollectQueryData(_query) == 0;
    }

    public double Sample()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) return 0;
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out uint count, buffer) != 0) return 0;
            // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then status (4) + padding (4) + double.
            int itemSize = IntPtr.Size + 16;
            var perGpu = new Dictionary<string, double>();
            for (int i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                // Instance names look like "pid_123_luid_0x0_0xD1C2_phys_0_eng_0_engtype_3D"; group by GPU (luid).
                int at = name.IndexOf("luid_", StringComparison.Ordinal);
                int end = name.IndexOf("_phys", StringComparison.Ordinal);
                var gpu = at >= 0 && end > at ? name[at..end] : "gpu";
                perGpu[gpu] = perGpu.GetValueOrDefault(gpu) + value;
            }
            return perGpu.Count == 0 ? 0 : Math.Clamp(perGpu.Values.Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_ok) PdhCloseQuery(_query);
    }
}

public sealed record GameSession(string Game, DateTime Start, TimeSpan Duration,
    double AvgCpu, double PeakCpu, double AvgGpu, double PeakGpu, long PeakGameRam,
    double? AvgFps = null, double? LowFps = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string When => Start.ToString("g");
    [System.Text.Json.Serialization.JsonIgnore] public string DurationText => Fmt.Duration(Duration);
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => (AvgFps is { } f ? $"{f:0} FPS avg / {LowFps:0} 1% low · " : "") +
                            $"CPU {AvgCpu:0}% avg / {PeakCpu:0}% peak · GPU {AvgGpu:0}% avg / {PeakGpu:0}% peak · " +
                            $"game RAM {Fmt.Bytes(PeakGameRam)} peak";
}

public sealed record GameTotal(string Game, TimeSpan Played, int Sessions)
{
    public string Text => $"{Fmt.Duration(Played)} · {Sessions} session{(Sessions == 1 ? "" : "s")}";
}

/// <summary>Collects samples during one game session and keeps the history in %AppData%\PCOptimizer\sessions.json.</summary>
public sealed class GameStats
{
    static string FilePath => Path.Combine(
        Path.GetDirectoryName(Environment.GetEnvironmentVariable("PCOPTIMIZER_SETTINGS") is { Length: > 0 } s ? s
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCOptimizer", "x"))!,
        "sessions.json");

    readonly CpuSampler _cpu = new();
    GpuLoad? _gpu;
    string? _game;
    int _pid;
    DateTime _start;
    int _samples;
    double _cpuSum, _cpuPeak, _gpuSum, _gpuPeak;
    long _ramPeak;
    bool _fpsHeld;
    int _fpsSamples;
    double _fpsSum, _lowSum;

    public bool Running => _game is not null;
    public double LastCpu { get; private set; }
    public double LastGpu { get; private set; }
    public long LastGameRam { get; private set; }

    public void Start(string game, int pid)
    {
        _game = game;
        _pid = pid;
        _start = DateTime.Now;
        _samples = 0;
        _cpuSum = _cpuPeak = _gpuSum = _gpuPeak = 0;
        _ramPeak = 0;
        _cpu.Sample();
        _gpu ??= new GpuLoad();
        _gpu.Sample();
        // Frame rate needs admin (Windows' rule for event tracing); without it the recap just leaves FPS out.
        _fpsSamples = 0; _fpsSum = _lowSum = 0;
        if (Elevation.IsElevated && !_fpsHeld) { FpsMonitor.Shared.Acquire(); _fpsHeld = true; }
    }

    public void Sample()
    {
        if (_game is null) return;
        LastCpu = _cpu.Sample();
        LastGpu = _gpu?.Sample() ?? 0;
        try
        {
            using var p = Process.GetProcessById(_pid);
            LastGameRam = p.WorkingSet64;
        }
        catch { }
        _samples++;
        _cpuSum += LastCpu; _cpuPeak = Math.Max(_cpuPeak, LastCpu);
        _gpuSum += LastGpu; _gpuPeak = Math.Max(_gpuPeak, LastGpu);
        _ramPeak = Math.Max(_ramPeak, LastGameRam);
        if (_fpsHeld && FpsMonitor.Shared.Read(_pid) is { } fps)
        {
            _fpsSamples++;
            _fpsSum += fps.Fps;
            _lowSum += fps.Low1;
        }
    }

    /// <summary>Ends the session; sessions shorter than a minute aren't kept.</summary>
    public GameSession? Stop()
    {
        if (_game is null) return null;
        var duration = DateTime.Now - _start;
        var session = _samples == 0 ? null : new GameSession(_game, _start, duration,
            _cpuSum / _samples, _cpuPeak, _gpuSum / _samples, _gpuPeak, _ramPeak,
            _fpsSamples > 0 ? _fpsSum / _fpsSamples : null, _fpsSamples > 0 ? _lowSum / _fpsSamples : null);
        _game = null;
        if (_fpsHeld) { FpsMonitor.Shared.Release(); _fpsHeld = false; }
        if (session is null || duration < TimeSpan.FromMinutes(1)) return null;
        var all = Load();
        all.Insert(0, session);
        Save(all.Take(300).ToList());
        return session;
    }

    public static List<GameSession> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<GameSession>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    static void Save(List<GameSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(sessions));
        }
        catch { }
    }

    public static List<GameTotal> Totals(IEnumerable<GameSession> sessions) =>
        sessions.GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameTotal(g.Key, TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)), g.Count()))
            .OrderByDescending(t => t.Played).ToList();
}
