using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

namespace PCOptimizer.Services;

public sealed class InputDay
{
    public long Keys { get; set; }
    public long LeftClicks { get; set; }
    public long RightClicks { get; set; }
    public long MiddleClicks { get; set; }
    public long SideClicks { get; set; }
    public long Scrolls { get; set; }
    /// <summary>Cursor travel in physical pixels.</summary>
    public double Distance { get; set; }
    public long Clicks => LeftClicks + RightClicks + MiddleClicks + SideClicks;
}

public sealed class GameInput
{
    public long Keys { get; set; }
    public long Clicks { get; set; }
    /// <summary>Seconds actively playing (Game Mode on and input in the last minute).</summary>
    public double Seconds { get; set; }
    public Dictionary<int, long> KeyCounts { get; set; } = [];
    public double Apm => Seconds < 30 ? 0 : (Keys + Clicks) / (Seconds / 60);
}

public sealed class InputStatsData
{
    public const int HeatW = 64, HeatH = 36;
    /// <summary>"yyyy-MM-dd" -> totals for that day.</summary>
    public Dictionary<string, InputDay> Days { get; set; } = [];
    /// <summary>All-time presses per virtual-key code.</summary>
    public Dictionary<int, long> KeyCounts { get; set; } = [];
    /// <summary>Where the cursor spends its time and where you click, on a grid over all screens.</summary>
    public int[] MoveHeat { get; set; } = new int[HeatW * HeatH];
    public int[] ClickHeat { get; set; } = new int[HeatW * HeatH];
    public Dictionary<string, GameInput> Games { get; set; } = [];
    public DateTime Since { get; set; } = DateTime.Now;
    /// <summary>Millimetres per pixel of the main screen, to turn cursor travel into a distance.</summary>
    public double MmPerPixel { get; set; } = 0.25;
}

/// <summary>
/// Counts key presses, clicks, scrolling and cursor travel with low-level input hooks on their own thread
/// (so a busy UI can never lag your input). Only counts are kept: never what you type or in which order.
/// Stays on this PC in inputstats.json; 90 days of daily totals are kept.
/// </summary>
public sealed class InputStats
{
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    static string FilePath => Path.Combine(
        Path.GetDirectoryName(Environment.GetEnvironmentVariable("PCOPTIMIZER_SETTINGS") is { Length: > 0 } s ? s
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCOptimizer", "x"))!,
        "inputstats.json");

    readonly object _lock = new();
    readonly bool[] _down = new bool[256];
    readonly HookProc _keyProc, _mouseProc; // kept alive: the hooks call back into them
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    Thread? _thread;
    uint _threadId;
    IntPtr _keyHook, _mouseHook;
    int _lastX = int.MinValue, _lastY;
    int _vx, _vy, _vw = 1, _vh = 1;
    int _ticks;

    public InputStatsData Data { get; }
    public bool Running => _thread is not null;
    /// <summary>The game being played right now (set by Game Mode), so input can be counted per game.</summary>
    public volatile string? Game;

    public InputStats()
    {
        Data = Load();
        _keyProc = KeyHook;
        _mouseProc = MouseHook;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_thread is not null) return;
        RefreshScreen();
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            _keyHook = SetWindowsHookEx(13, _keyProc, module, 0);   // WH_KEYBOARD_LL
            _mouseHook = SetWindowsHookEx(14, _mouseProc, module, 0); // WH_MOUSE_LL
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_keyHook);
            UnhookWindowsHookEx(_mouseHook);
        }) { IsBackground = true, Name = "Input stats hooks" };
        _thread.Start();
        ready.Wait(2000);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_thread is not null)
        {
            PostThreadMessage(_threadId, 0x12, IntPtr.Zero, IntPtr.Zero); // WM_QUIT ends the hook thread's message loop
            _thread.Join(1000);
            _thread = null;
        }
        Save();
    }

    void RefreshScreen()
    {
        _vx = GetSystemMetrics(76); _vy = GetSystemMetrics(77);
        _vw = Math.Max(1, GetSystemMetrics(78)); _vh = Math.Max(1, GetSystemMetrics(79));
        var dc = GetDC(IntPtr.Zero);
        int mm = GetDeviceCaps(dc, 4), px = GetDeviceCaps(dc, 8); // HORZSIZE, HORZRES
        ReleaseDC(IntPtr.Zero, dc);
        if (mm > 0 && px > 0) lock (_lock) Data.MmPerPixel = (double)mm / px;
    }

    InputDay Today()
    {
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        if (!Data.Days.TryGetValue(key, out var day)) Data.Days[key] = day = new InputDay();
        return day;
    }

    GameInput? GameEntry()
    {
        var game = Game;
        if (game is null) return null;
        if (!Data.Games.TryGetValue(game, out var g)) Data.Games[game] = g = new GameInput();
        return g;
    }

    unsafe IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            uint vk = k->vk & 0xFF;
            int msg = (int)wParam;
            if (msg is 0x100 or 0x104) // WM_KEYDOWN, WM_SYSKEYDOWN
            {
                // Held keys auto-repeat; count each press once.
                if (!_down[vk])
                    lock (_lock)
                    {
                        Today().Keys++;
                        Data.KeyCounts[(int)vk] = Data.KeyCounts.GetValueOrDefault((int)vk) + 1;
                        if (GameEntry() is { } g) { g.Keys++; g.KeyCounts[(int)vk] = g.KeyCounts.GetValueOrDefault((int)vk) + 1; }
                    }
                _down[vk] = true;
            }
            else if (msg is 0x101 or 0x105) _down[vk] = false;
        }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    unsafe IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var m = (MSLLHOOKSTRUCT*)lParam;
            int msg = (int)wParam;
            int cx = Math.Clamp((int)((long)(m->x - _vx) * InputStatsData.HeatW / _vw), 0, InputStatsData.HeatW - 1);
            int cy = Math.Clamp((int)((long)(m->y - _vy) * InputStatsData.HeatH / _vh), 0, InputStatsData.HeatH - 1);
            int cell = cy * InputStatsData.HeatW + cx;
            lock (_lock)
            {
                switch (msg)
                {
                    case 0x200: // WM_MOUSEMOVE
                        if (_lastX != int.MinValue)
                        {
                            double d = Math.Sqrt((double)(m->x - _lastX) * (m->x - _lastX) + (double)(m->y - _lastY) * (m->y - _lastY));
                            if (d < 1500) Today().Distance += d; // bigger jumps are games re-centring the cursor
                        }
                        _lastX = m->x; _lastY = m->y;
                        Data.MoveHeat[cell]++;
                        break;
                    case 0x201: Today().LeftClicks++; Click(cell); break;   // WM_LBUTTONDOWN
                    case 0x204: Today().RightClicks++; Click(cell); break;  // WM_RBUTTONDOWN
                    case 0x207: Today().MiddleClicks++; Click(cell); break; // WM_MBUTTONDOWN
                    case 0x20B: Today().SideClicks++; Click(cell); break;   // WM_XBUTTONDOWN
                    case 0x20A or 0x20E: // WM_MOUSEWHEEL, WM_MOUSEHWHEEL: count notches
                        Today().Scrolls += Math.Max(1, Math.Abs((short)(m->mouseData >> 16)) / 120);
                        break;
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    void Click(int cell)
    {
        Data.ClickHeat[cell]++;
        if (GameEntry() is { } g) g.Clicks++;
    }

    void Tick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        bool active = GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount - info.dwTime) < 60_000;
        if (active && Game is not null) lock (_lock) GameEntry()!.Seconds++;
        if (++_ticks % 60 == 0) { RefreshScreen(); Save(); }
    }

    /// <summary>A copy that's safe to read while the hooks keep counting.</summary>
    public InputStatsData Snapshot()
    {
        lock (_lock) return JsonSerializer.Deserialize<InputStatsData>(JsonSerializer.Serialize(Data))!;
    }

    static InputStatsData Load()
    {
        try
        {
            var data = File.Exists(FilePath) ? JsonSerializer.Deserialize<InputStatsData>(File.ReadAllText(FilePath)) ?? new() : new();
            if (data.MoveHeat.Length != InputStatsData.HeatW * InputStatsData.HeatH) data.MoveHeat = new int[InputStatsData.HeatW * InputStatsData.HeatH];
            if (data.ClickHeat.Length != InputStatsData.HeatW * InputStatsData.HeatH) data.ClickHeat = new int[InputStatsData.HeatW * InputStatsData.HeatH];
            return data;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                var cutoff = DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd");
                foreach (var old in Data.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) Data.Days.Remove(old);
                json = JsonSerializer.Serialize(Data);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Data.Days.Clear();
            Data.KeyCounts.Clear();
            Data.Games.Clear();
            Array.Clear(Data.MoveHeat);
            Array.Clear(Data.ClickHeat);
            Data.Since = DateTime.Now;
        }
        Save();
    }
}
