using System.Runtime.InteropServices;

namespace Rig.Services;

/// <summary>One snapshot of an Xbox-style controller: buttons, sticks (−1..1), triggers (0..1) and battery.</summary>
public readonly record struct PadState(bool Connected, ushort Buttons, double LX, double LY, double RX, double RY, double LT, double RT)
{
    public bool Has(PadButton b) => (Buttons & (ushort)b) != 0;
}

[Flags]
public enum PadButton : ushort
{
    Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LeftStick = 0x40, RightStick = 0x80,
    LB = 0x100, RB = 0x200, Guide = 0x400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>
/// XInput, the API Windows games use for controllers. Covers Xbox controllers and anything that pretends to be one
/// (most third-party pads, PlayStation pads through Steam Input or DS4Windows).
/// </summary>
public static class Controllers
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vibration);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint index, byte devType, out XINPUT_BATTERY_INFORMATION info);

    public static PadState Read(int index)
    {
        try
        {
            if (XInputGetState((uint)index, out var s) != 0) return default;
            var g = s.Gamepad;
            static double Axis(short v) => Math.Max(-1, v / 32767.0);
            return new PadState(true, g.wButtons, Axis(g.sThumbLX), Axis(g.sThumbLY), Axis(g.sThumbRX), Axis(g.sThumbRY),
                g.bLeftTrigger / 255.0, g.bRightTrigger / 255.0);
        }
        catch (DllNotFoundException) { return default; }
    }

    /// <summary>Motor strengths 0..1 for the heavy (left) and light (right) motors.</summary>
    public static void Rumble(int index, double left, double right)
    {
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(Math.Clamp(left, 0, 1) * 65535), wRightMotorSpeed = (ushort)(Math.Clamp(right, 0, 1) * 65535) };
        try { XInputSetState((uint)index, ref v); } catch (DllNotFoundException) { }
    }

    public static string Battery(int index)
    {
        try
        {
            if (XInputGetBatteryInformation((uint)index, 0, out var b) != 0) return "";
            return b.BatteryType switch
            {
                1 => "Wired", // BATTERY_TYPE_WIRED
                0 => "",      // disconnected
                _ => b.BatteryLevel switch { 0 => "Battery empty", 1 => "Battery low", 2 => "Battery medium", _ => "Battery full" },
            };
        }
        catch (DllNotFoundException) { return ""; }
    }
}
