using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// The phone's mouse, wheel, keys and text, from the control stream's input messages (moonlight-common-c's Input.h:
/// a big-endian size, a little-endian type, then the packet), made into Windows input with SendInput, as Apollo's
/// platform/windows/input.cpp does (GPLv3). Positions land in the streamed display's area of the desktop. Windows run as
/// administrator do not take input from a program that is not.
/// </summary>
internal static class HostInput
{
    private const uint KeyDown = 0x03, KeyUp = 0x04, AbsMove = 0x05, RelMove = 0x07, ButtonDown = 0x08, ButtonUp = 0x09,
        Scroll = 0x0A, Text = 0x17, HScroll = 0x55000001;

    /// <summary>Where the streamed display sits on the desktop, in pixels.</summary>
    public static volatile Rect Area = new(0, 0, 1920, 1080);

    public sealed record Rect(int X, int Y, int W, int H);

    private static int logged;

    /// <summary>One control message's input payload: one or more input packets one after another.</summary>
    public static void Handle(ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data); // excluding this field
            if (size < 4 || 4 + size > data.Length) return;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            var p = data.Slice(8, size - 4);
            try { One(type, p); }
            catch (Exception e) { if (logged++ < 5) Log.P($"host: input 0x{type:x}: {e.Message}"); }
            data = data[(4 + size)..];
        }
    }

    private static void One(uint type, ReadOnlySpan<byte> p)
    {
        switch (type)
        {
            case AbsMove when p.Length >= 10:
            {
                int x = BinaryPrimitives.ReadInt16BigEndian(p);
                int y = BinaryPrimitives.ReadInt16BigEndian(p[2..]);
                int w = BinaryPrimitives.ReadInt16BigEndian(p[6..]) + 1;
                int h = BinaryPrimitives.ReadInt16BigEndian(p[8..]) + 1;
                MoveTo(x, y, Math.Max(1, w), Math.Max(1, h));
                break;
            }
            case RelMove when p.Length >= 4:
                MoveBy(BinaryPrimitives.ReadInt16BigEndian(p), BinaryPrimitives.ReadInt16BigEndian(p[2..]));
                break;
            case ButtonDown when p.Length >= 1:
            case ButtonUp when p.Length >= 1:
                Button(p[0], type == ButtonDown);
                break;
            case Scroll when p.Length >= 2:
                Mouse(0, 0, BinaryPrimitives.ReadInt16BigEndian(p), MouseWheel);
                break;
            case HScroll when p.Length >= 2:
                Mouse(0, 0, BinaryPrimitives.ReadInt16BigEndian(p), MouseHWheel);
                break;
            case KeyDown when p.Length >= 4:
            case KeyUp when p.Length >= 4:
                Key(BinaryPrimitives.ReadInt16LittleEndian(p[1..]) & 0xFF, type == KeyDown);
                break;
            case Text when p.Length > 0:
                Type(Encoding.UTF8.GetString(p));
                break;
        }
    }

    /// <summary>
    /// A relative move kept inside the streamed display: from where the pointer is (or the display's middle, if it is
    /// elsewhere), by the move, held to the display's edges, sent as a position. A plain relative move went wherever
    /// the pointer was, on this PC's own monitors (the phone sends relative moves, 20 Nov 2026).
    /// </summary>
    private static void MoveBy(int dx, int dy)
    {
        var a = Area;
        GetCursorPos(out POINT now);
        bool inside = now.X >= a.X && now.X < a.X + a.W && now.Y >= a.Y && now.Y < a.Y + a.H;
        double x = inside ? now.X - a.X : a.W / 2.0, y = inside ? now.Y - a.Y : a.H / 2.0;
        x = Math.Clamp(x + dx, 0, a.W - 1);
        y = Math.Clamp(y + dy, 0, a.H - 1);
        AreaPosition(a.X + x, a.Y + y);
    }

    /// <summary>The pointer to the middle of the streamed display (the stream starting).</summary>
    public static void Centre()
    {
        var a = Area;
        AreaPosition(a.X + a.W / 2.0, a.Y + a.H / 2.0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private static void AreaPosition(double px, double py)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1));
        Mouse(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    private static void MoveTo(int x, int y, int w, int h)
    {
        var a = Area;
        double px = a.X + (double)x * (a.W - 1) / Math.Max(1, w - 1);
        double py = a.Y + (double)y * (a.H - 1) / Math.Max(1, h - 1);
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1));
        Mouse(nx, ny, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    private static void Button(int button, bool down)
    {
        uint flags; uint data = 0;
        switch (button)
        {
            case 1: flags = down ? 0x0002u : 0x0004u; break; // left
            case 2: flags = down ? 0x0020u : 0x0040u; break; // middle
            case 3: flags = down ? 0x0008u : 0x0010u; break; // right
            case 4: flags = down ? 0x0080u : 0x0100u; data = 1; break; // X1
            case 5: flags = down ? 0x0080u : 0x0100u; data = 2; break; // X2
            default: return;
        }
        Mouse(0, 0, (int)data, flags);
    }

    /// <summary>A Windows virtual key (the phone sends US-layout key codes), by its scan code where it has one, so games
    /// see it too.</summary>
    private static void Key(int vk, bool down)
    {
        uint scan = MapVirtualKeyW((uint)vk, 4); // MAPVK_VK_TO_VSC_EX: 0xE0xx for extended keys
        uint flags = down ? 0u : KeyUpFlag;
        var input = new INPUT { type = 1 };
        if (scan != 0)
        {
            input.u.ki.wScan = (ushort)(scan & 0xFF);
            flags |= KeyScanCode;
            if ((scan & 0xFF00) == 0xE000 || IsExtended(vk)) flags |= KeyExtended;
        }
        else
        {
            input.u.ki.wVk = (ushort)vk;
        }
        input.u.ki.dwFlags = flags;
        Send(input);
    }

    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E
        or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5 or 0x90;

    private static void Type(string text)
    {
        foreach (char c in text)
        {
            var down = new INPUT { type = 1 };
            down.u.ki.wScan = c;
            down.u.ki.dwFlags = KeyUnicode;
            Send(down);
            down.u.ki.dwFlags = KeyUnicode | KeyUpFlag;
            Send(down);
        }
    }

    private const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseVirtualDesk = 0x4000, MouseWheel = 0x0800, MouseHWheel = 0x1000;
    private const uint KeyExtended = 0x0001, KeyUpFlag = 0x0002, KeyUnicode = 0x0004, KeyScanCode = 0x0008;

    private static void Mouse(int dx, int dy, int data, uint flags)
    {
        var input = new INPUT { type = 0 };
        input.u.mi.dx = dx;
        input.u.mi.dy = dy;
        input.u.mi.mouseData = data;
        input.u.mi.dwFlags = flags;
        Send(input);
    }

    /// <summary>Tests only (PXR_HOST_INPUT=log): input counted and logged, not given to Windows.</summary>
    private static readonly bool logOnly = Environment.GetEnvironmentVariable("PXR_HOST_INPUT") == "log";
    public static long Sent;

    private static unsafe void Send(INPUT input)
    {
        Sent++;
        if (logOnly)
        {
            if (Sent <= 3 || Sent % 500 == 0)
                Log.P($"host: input {Sent}: {(input.type == 0 ? $"mouse {input.u.mi.dx},{input.u.mi.dy} flags 0x{input.u.mi.dwFlags:x}" : $"key scan 0x{input.u.ki.wScan:x} vk 0x{input.u.ki.wVk:x} flags 0x{input.u.ki.dwFlags:x}")}");
            return;
        }
        if (SendInput(1, &input, sizeof(INPUT)) != 1 && logged++ < 5)
            Log.P($"host: input refused by Windows ({Marshal.GetLastWin32Error()}): an administrator's window in front?");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern unsafe uint SendInput(uint n, INPUT* inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
