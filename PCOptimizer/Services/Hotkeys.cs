using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PCOptimizer.Services;

/// <summary>
/// A hidden message-only window that owns the app's global hotkeys and listens for clipboard changes.
/// </summary>
public sealed class HotkeyHost : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x312, WM_CLIPBOARDUPDATE = 0x31D;

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = [];
    int _nextId = 1;

    public event EventHandler? ClipboardChanged;

    public HotkeyHost()
    {
        // HWND_MESSAGE parent: never visible, still receives hotkeys and clipboard notifications.
        _source = new HwndSource(new HwndSourceParameters("PCOptimizerHotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    /// <summary>Registers a system-wide hotkey. False if another app already owns that combination.</summary>
    public bool Register(uint mods, System.Windows.Input.Key key, Action action)
    {
        int id = _nextId++;
        uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action)) { action(); handled = true; }
        else if (msg == WM_CLIPBOARDUPDATE) ClipboardChanged?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}

/// <summary>Sends key presses to whatever window has focus.</summary>
public static class KeySender
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Presses the keys in order, then releases them in reverse (a chord like Win+Alt+G).</summary>
    public static void Chord(params System.Windows.Input.Key[] keys)
    {
        var list = new List<INPUT>();
        foreach (var k in keys) list.Add(Make(k, up: false));
        foreach (var k in keys.Reverse()) list.Add(Make(k, up: true));
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    static INPUT Make(System.Windows.Input.Key key, bool up)
    {
        ushort vk = (ushort)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        // Windows keys and navigation keys are "extended".
        uint flags = (up ? 0x2u : 0) | (key is System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or System.Windows.Input.Key.PrintScreen ? 0x1u : 0);
        return new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
    }
}
