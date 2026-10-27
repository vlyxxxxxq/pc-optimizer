using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services;

/// <summary>
/// Real frame rate for any DirectX game, the same way PresentMon / the Xbox Game Bar do it: a real-time
/// ETW session counts the "Present" events each process makes (DXGI for DX10–12, D3D9 for old games).
/// Windows only lets administrators start such a session.
/// </summary>
public sealed unsafe class FpsMonitor : IDisposable
{
    const string SessionName = "PCOptimizer-FPS";
    static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    // --- ETW interop ---
    [StructLayout(LayoutKind.Sequential)]
    struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName;
        public fixed byte TimeZone[172];
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE_LOGFILE
    {
        public IntPtr LogFileName, LoggerName;
        public long CurrentTime;
        public uint BuffersRead, ProcessTraceMode;
        public fixed byte CurrentEvent[88];
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    delegate void EventRecordCallback(byte* record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32.dll")]
    static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    const uint ERROR_ALREADY_EXISTS = 183, ERROR_ACCESS_DENIED = 5, EVENT_TRACE_CONTROL_STOP = 1;
    const ulong INVALID_HANDLE = ulong.MaxValue;
    const int PropsSize = 120, NameBytes = 1024;

    // --- state ---
    readonly object _lock = new();
    readonly Dictionary<int, Queue<long>> _frames = [];   // pid -> recent present timestamps (QPC ticks)
    readonly EventRecordCallback _callback;                // kept alive for the native side
    ulong _session, _trace = INVALID_HANDLE;
    Thread? _thread;

    public bool Running { get; private set; }
    /// <summary>Why it isn't running, for display ("needs admin").</summary>
    public string? Problem { get; private set; }

    FpsMonitor() => _callback = OnEvent;

    /// <summary>Windows allows one trace session per name, so the overlay and session stats share this one.</summary>
    public static FpsMonitor Shared { get; } = new();
    int _users;
    readonly object _usersLock = new(); // separate from _lock: Stop() waits for the trace thread, which takes _lock

    /// <summary>Starts the trace for the first user; each Acquire needs a matching Release.</summary>
    public void Acquire() { lock (_usersLock) { if (_users++ == 0) Start(); } }

    public void Release() { lock (_usersLock) { if (_users > 0 && --_users == 0) Stop(); } }

    public void Start()
    {
        if (Running) return;
        if (!Elevation.IsElevated) { Problem = "admin"; return; }

        var props = NewProperties();
        try
        {
            uint err = StartTraceW(out _session, SessionName, props);
            if (err == ERROR_ALREADY_EXISTS)
            {
                // Left over from a crash: stop it and start fresh.
                ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
                Marshal.FreeHGlobal(props);
                props = NewProperties();
                err = StartTraceW(out _session, SessionName, props);
            }
            if (err != 0) { Problem = err == ERROR_ACCESS_DENIED ? "admin" : $"error {err}"; return; }
        }
        finally { Marshal.FreeHGlobal(props); }

        EnableTraceEx2(_session, DxgiProvider, 1, 4, 0, 0, 0, IntPtr.Zero);
        EnableTraceEx2(_session, D3D9Provider, 1, 4, 0, 0, 0, IntPtr.Zero);

        var name = Marshal.StringToHGlobalUni(SessionName);
        var logfile = new EVENT_TRACE_LOGFILE
        {
            LoggerName = name,
            ProcessTraceMode = 0x100 | 0x10000000, // REAL_TIME | EVENT_RECORD
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        _trace = OpenTraceW(ref logfile);
        Marshal.FreeHGlobal(name);
        if (_trace == INVALID_HANDLE) { StopSession(); Problem = "error"; return; }

        Running = true;
        Problem = null;
        _thread = new Thread(() =>
        {
            ulong h = _trace;
            ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero); // blocks until the session stops
        }) { IsBackground = true, Name = "FPS trace" };
        _thread.Start();
    }

    static IntPtr NewProperties()
    {
        int size = PropsSize + NameBytes;
        var p = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)p, size).Clear();
        Marshal.WriteInt32(p, 0, size);           // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext: QPC timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);    // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);            // BufferSize (KB)
        Marshal.WriteInt32(p, 64, 0x100);         // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);             // FlushTimer (s)
        Marshal.WriteInt32(p, 116, PropsSize);    // LoggerNameOffset
        return p;
    }

    void OnEvent(byte* rec)
    {
        // EVENT_HEADER: ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40.
        ushort id = *(ushort*)(rec + 40);
        var provider = *(Guid*)(rec + 24);
        bool present = provider == DxgiProvider ? id is DxgiPresentStart or DxgiPresentMpoStart
                     : provider == D3D9Provider && id == D3D9PresentStart;
        if (!present) return;
        int pid = (int)*(uint*)(rec + 12);
        long ts = *(long*)(rec + 16);
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<long>();
            q.Enqueue(ts);
            long keep = ts - Stopwatch.Frequency * 10; // 10 s of history for the 1% low
            while (q.Count > 0 && q.Peek() < keep) q.Dequeue();
        }
    }

    /// <summary>Frames per second over the last second, and the 1% low over the last 10 seconds.</summary>
    public (double Fps, double Low1)? Read(int pid)
    {
        long[] ts;
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var q) || q.Count < 2) return null;
            ts = q.ToArray();
        }
        long now = Stopwatch.GetTimestamp();
        if (now - ts[^1] > Stopwatch.Frequency * 2) return null; // stopped presenting (minimised, closed)
        long oneSecAgo = now - Stopwatch.Frequency;
        int lastSecond = ts.Count(t => t >= oneSecAgo);
        var frameTimes = new double[ts.Length - 1];
        for (int i = 1; i < ts.Length; i++) frameTimes[i - 1] = (ts[i] - ts[i - 1]) * 1000.0 / Stopwatch.Frequency;
        Array.Sort(frameTimes);
        double p99 = frameTimes[Math.Min(frameTimes.Length - 1, (int)(frameTimes.Length * 0.99))];
        return (lastSecond, p99 > 0 ? 1000 / p99 : 0);
    }

    /// <summary>The process presenting the most frames right now (used when Game Mode hasn't named a game).</summary>
    public int? Busiest()
    {
        lock (_lock)
        {
            long cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            var best = _frames.Where(kv => kv.Key != Environment.ProcessId)
                .Select(kv => (Pid: kv.Key, N: kv.Value.Count(t => t >= cutoff)))
                .OrderByDescending(x => x.N).FirstOrDefault();
            return best.N > 5 ? best.Pid : null;
        }
    }

    void StopSession()
    {
        var props = NewProperties();
        ControlTraceW(_session, null, props, EVENT_TRACE_CONTROL_STOP);
        Marshal.FreeHGlobal(props);
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        StopSession();
        if (_trace != INVALID_HANDLE) CloseTrace(_trace);
        _trace = INVALID_HANDLE;
        _thread?.Join(2000);
        lock (_lock) _frames.Clear();
    }

    public void Dispose() => Stop();
}
