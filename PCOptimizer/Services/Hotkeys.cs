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
    public bool Register(Hotkey hotkey, Action action) => Register(hotkey.NativeModifiers, hotkey.Key, action);

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

/// <summary>A key plus modifiers, saved in settings as text like "Ctrl+Shift+F9".</summary>
public readonly record struct Hotkey(System.Windows.Input.ModifierKeys Modifiers, System.Windows.Input.Key Key)
{
    public uint NativeModifiers =>
        (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? HotkeyHost.MOD_ALT : 0) |
        (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? HotkeyHost.MOD_CONTROL : 0) |
        (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? HotkeyHost.MOD_SHIFT : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(Key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)Key - (int)System.Windows.Input.Key.D0).ToString(),
            System.Windows.Input.Key.Snapshot => "PrintScreen",
            _ => Key.ToString(),
        });
        return string.Join("+", parts);
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = System.Windows.Input.ModifierKeys.None;
        System.Windows.Input.Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= System.Windows.Input.ModifierKeys.Control; break;
                case "shift": mods |= System.Windows.Input.ModifierKeys.Shift; break;
                case "alt": mods |= System.Windows.Input.ModifierKeys.Alt; break;
                case "printscreen": key = System.Windows.Input.Key.Snapshot; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) key = System.Windows.Input.Key.D0 + (part[0] - '0');
                    else if (Enum.TryParse<System.Windows.Input.Key>(part, true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        return key is { } kk ? new Hotkey(mods, kk) : null;
    }

    /// <summary>F-keys and Print Screen can stand alone; anything else needs Ctrl or Alt so typing isn't hijacked.</summary>
    public bool IsUsable =>
        Key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24 or System.Windows.Input.Key.Snapshot ||
        Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) || Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
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
        uint flags = (up ? 0x2u : 0) | (key is System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin or System.Windows.Input.Key.PrintScreen
            or System.Windows.Input.Key.MediaPlayPause or System.Windows.Input.Key.MediaNextTrack or System.Windows.Input.Key.MediaPreviousTrack ? 0x1u : 0);
        return new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
    }
}
