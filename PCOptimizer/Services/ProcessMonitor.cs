using System.Diagnostics;

namespace PCOptimizer.Services;

public readonly record struct ProcessSample(string Name, int Id, double Cpu, long Memory);

/// <summary>A process, or every process sharing a name when grouped. Updated in place so the grid keeps its scroll and selection.</summary>
public sealed class ProcessRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<int> Ids { get; private set; } = [];

    int _count;
    public int Count { get => _count; private set { if (Set(ref _count, value)) OnPropertyChanged(nameof(IdText)); } }

    double _cpu;
    public double Cpu { get => _cpu; private set { if (Set(ref _cpu, value)) OnPropertyChanged(nameof(CpuText)); } }

    long _memoryBytes;
    public long MemoryBytes { get => _memoryBytes; private set { if (Set(ref _memoryBytes, value)) OnPropertyChanged(nameof(MemoryText)); } }

    public string IdText => Ids.Count == 1 ? Ids[0].ToString() : $"{Ids.Count} processes";
    public string CpuText => $"{Cpu:0.0}%";
    public string MemoryText => Fmt.Bytes(MemoryBytes);

    public void Update(IReadOnlyList<int> ids, double cpu, long memory)
    {
        Ids = ids;
        Count = ids.Count;
        OnPropertyChanged(nameof(IdText));
        Cpu = Math.Round(cpu, 1);
        MemoryBytes = memory;
    }
}

/// <summary>Computes per-process CPU from TotalProcessorTime deltas between successive calls.</summary>
public sealed class ProcessSampler
{
    Dictionary<int, TimeSpan> _previous = [];
    DateTime _previousAt = DateTime.UtcNow;

    public List<ProcessSample> Sample()
    {
        var now = DateTime.UtcNow;
        double capacity = (now - _previousAt).TotalMilliseconds * Environment.ProcessorCount;
        var next = new Dictionary<int, TimeSpan>();
        var samples = new List<ProcessSample>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        next[p.Id] = t;
                        if (_previous.TryGetValue(p.Id, out var t0) && capacity > 0)
                            cpu = (t - t0).TotalMilliseconds / capacity * 100;
                    }
                    catch { /* access denied for protected processes */ }
                    samples.Add(new ProcessSample(p.ProcessName, p.Id, Math.Clamp(cpu, 0, 100), p.WorkingSet64));
                }
                catch { /* exited */ }
            }
        }

        _previous = next;
        _previousAt = now;
        return samples;
    }
}

public static class ProcessMonitor
{
    /// <summary>Killing these would crash or destabilize Windows.</summary>
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "svchost", "fontdrvhost", "dwm", "Memory Compression", "MsMpEng", "PCOptimizer",
    };

    public static bool IsProtected(ProcessRow row) => row.Ids.Any(id => id <= 4) || Protected.Contains(row.Name);

    /// <summary>Ends every process in the row. Returns how many could not be ended.</summary>
    public static int Kill(ProcessRow row)
    {
        int failed = 0;
        foreach (var id in row.Ids)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                p.Kill();
            }
            catch (ArgumentException) { /* already gone */ }
            catch { failed++; }
        }
        return failed;
    }
}
