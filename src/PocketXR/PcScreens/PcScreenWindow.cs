using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: the window a PC screen streams into, a plain Win32 window (its swap chain needs one of its own). Before
/// and between streams it says in words what is happening; while streaming the picture fills it, kept to its shape,
/// and the mouse, wheel and keyboard over it go to the other computer. Its own pointer is hidden over it: the other
/// computer's pointer is in the picture.
/// </summary>
internal sealed unsafe class PcScreenWindow
{
    public IntPtr Hwnd { get; private set; }
    public string Words { get; private set; } = "";
    public bool Streaming;

    /// <summary>The stream's size, for mouse positions: set when the stream starts.</summary>
    public int StreamW = PcScreenLogic.StreamW, StreamH = PcScreenLogic.StreamH;

    /// <summary>Relative moves instead of positions (a Linux host, which maps positions against its own screen).</summary>
    public bool RelativeMouse;

    public Action<int, int>? Resized;
    public Action? Closing;

    private static PcScreenWindow? only;
    private int lastX = int.MinValue, lastY;
    private bool tracking;

    private const string ClassName = "PocketXR.PcScreen";
    private const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014,
        WM_SETCURSOR = 0x0020, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C,
        WM_MOUSEHWHEEL = 0x020E, WM_MOUSELEAVE = 0x02A3, WM_KILLFOCUS = 0x0008, WM_APP_WORDS = 0x8001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;

    public static PcScreenWindow Create(string title, int x, int y, int w, int h, bool borderless)
    {
        var win = new PcScreenWindow();
        only = win;
        IntPtr instance = GetModuleHandleW(null);
        fixed (char* cls = ClassName)
        fixed (char* t = title)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x0003, // redraw on resize
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, 32512),
                hbrBackground = GetStockObject(4), // black
                lpszClassName = cls,
                hIcon = ExtractIconW(instance, Environment.ProcessPath ?? "", 0),
            };
            RegisterClassExW(&wc);
            uint style = borderless ? WS_POPUP | WS_VISIBLE : WS_OVERLAPPEDWINDOW | WS_VISIBLE;
            if (!borderless)
            {
                // The size asked for is the picture's: the frame goes around it.
                var r = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
                AdjustWindowRectEx(&r, style, false, 0);
                w = r.Right - r.Left;
                h = r.Bottom - r.Top;
            }
            win.Hwnd = CreateWindowExW(0, cls, t, style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }
        if (win.Hwnd == IntPtr.Zero) throw new InvalidOperationException($"the PC screen's window was not made ({Marshal.GetLastWin32Error()})");
        SetForegroundWindow(win.Hwnd);
        return win;
    }

    public (int W, int H) ClientSize
    {
        get
        {
            RECT r;
            GetClientRect(Hwnd, &r);
            return (r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Words shown in the window while there is no picture; also its title. Any thread.</summary>
    public void Say(string words)
    {
        Words = words;
        Log.P("pc screen: " + words);
        PostMessageW(Hwnd, WM_APP_WORDS, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetTitle(string title)
    {
        fixed (char* t = title) SetWindowTextW(Hwnd, t);
    }

    /// <summary>Runs the window's messages until it closes.</summary>
    public static void Run()
    {
        MSG m;
        while (GetMessageW(&m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }
    }

    public void Close() => PostMessageW(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        var w = only;
        try
        {
            if (w != null && w.Handle(hwnd, msg, wp, lp, out IntPtr result)) return result;
        }
        catch (Exception e)
        {
            Log.P($"pc screen: window: {e}");
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    private bool Handle(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_APP_WORDS:
                InvalidateRect(hwnd, null, true);
                return true;
            case WM_ERASEBKGND:
                if (Streaming) { result = (IntPtr)1; return true; }
                return false;
            case WM_PAINT:
                if (Streaming) return false;
                Paint(hwnd);
                return true;
            case WM_SIZE:
                Resized?.Invoke((int)(lp & 0xFFFF), (int)((lp >> 16) & 0xFFFF));
                InvalidateRect(hwnd, null, true);
                return false;
            case WM_SETCURSOR:
                if (Streaming && (lp & 0xFFFF) == 1) // over the client area
                {
                    SetCursor(IntPtr.Zero);
                    result = (IntPtr)1;
                    return true;
                }
                return false;
            case WM_MOUSEMOVE:
                if (!Streaming) return false;
                if (!tracking)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 0x2, hwndTrack = hwnd };
                    TrackMouseEvent(&tme);
                    tracking = true;
                }
                Move((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                return true;
            case WM_MOUSELEAVE:
                tracking = false;
                lastX = int.MinValue;
                return true;
            case WM_LBUTTONDOWN: return Button(hwnd, true, Moonlight.ButtonLeft, lp);
            case WM_LBUTTONUP: return Button(hwnd, false, Moonlight.ButtonLeft, lp);
            case WM_RBUTTONDOWN: return Button(hwnd, true, Moonlight.ButtonRight, lp);
            case WM_RBUTTONUP: return Button(hwnd, false, Moonlight.ButtonRight, lp);
            case WM_MBUTTONDOWN: return Button(hwnd, true, Moonlight.ButtonMiddle, lp);
            case WM_MBUTTONUP: return Button(hwnd, false, Moonlight.ButtonMiddle, lp);
            case WM_XBUTTONDOWN: return Button(hwnd, true, ((wp >> 16) & 0xFFFF) == 1 ? Moonlight.ButtonX1 : Moonlight.ButtonX2, lp);
            case WM_XBUTTONUP: return Button(hwnd, false, ((wp >> 16) & 0xFFFF) == 1 ? Moonlight.ButtonX1 : Moonlight.ButtonX2, lp);
            case WM_MOUSEWHEEL:
                if (Streaming) Moonlight.PxrScroll((short)((wp >> 16) & 0xFFFF));
                return Streaming;
            case WM_MOUSEHWHEEL:
                if (Streaming) Moonlight.PxrHScroll((short)((wp >> 16) & 0xFFFF));
                return Streaming;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (!Streaming) return false;
                Key((int)wp, msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN, lp);
                return true;
            case WM_CHAR:
                return Streaming;
            case WM_KILLFOCUS:
                // Keys held when the window lost focus would stay down on the other computer.
                if (Streaming) foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) Moonlight.PxrKey((short)(0x8000 | vk), 0, 0);
                return false;
            case WM_CLOSE:
                Closing?.Invoke();
                DestroyWindow(hwnd);
                return true;
            case WM_DESTROY:
                PostQuitMessage(0);
                return true;
        }
        return false;
    }

    private void Move(int x, int y)
    {
        var (cw, ch) = ClientSize;
        var fit = PcScreenLogic.Fit(cw, ch, StreamW, StreamH);
        int px = Math.Clamp(x - fit.X, 0, Math.Max(0, fit.W - 1));
        int py = Math.Clamp(y - fit.Y, 0, Math.Max(0, fit.H - 1));
        if (RelativeMouse)
        {
            if (lastX != int.MinValue)
            {
                int dx = (int)Math.Round((px - lastX) * (double)StreamW / Math.Max(1, fit.W));
                int dy = (int)Math.Round((py - lastY) * (double)StreamH / Math.Max(1, fit.H));
                if (dx != 0 || dy != 0) Moonlight.PxrMouseMove((short)dx, (short)dy);
            }
            lastX = px;
            lastY = py;
            return;
        }
        Moonlight.PxrMousePosition((short)px, (short)py, (short)Math.Max(1, fit.W), (short)Math.Max(1, fit.H));
    }

    private bool Button(IntPtr hwnd, bool down, int button, IntPtr lp)
    {
        if (!Streaming) return false;
        if (down) SetCapture(hwnd); else ReleaseCapture();
        Move((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        Moonlight.PxrMouseButton(down ? 1 : 0, button);
        return true;
    }

    private static void Key(int vk, bool down, IntPtr lp)
    {
        bool extended = ((lp >> 24) & 1) != 0;
        // Windows gives the generic Shift, Ctrl and Alt; GameStream wants the left or right one.
        if (vk == 0x10) vk = ((lp >> 16) & 0xFF) == 0x36 ? 0xA1 : 0xA0;
        else if (vk == 0x11) vk = extended ? 0xA3 : 0xA2;
        else if (vk == 0x12) vk = extended ? 0xA5 : 0xA4;
        int mods = 0;
        if (GetKeyState(0x10) < 0) mods |= Moonlight.ModShift;
        if (GetKeyState(0x11) < 0) mods |= Moonlight.ModCtrl;
        if (GetKeyState(0x12) < 0) mods |= Moonlight.ModAlt;
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) mods |= Moonlight.ModMeta;
        Moonlight.PxrKey((short)(0x8000 | (vk & 0xFF)), down ? 1 : 0, mods);
    }

    private void Paint(IntPtr hwnd)
    {
        PAINTSTRUCT ps;
        IntPtr dc = BeginPaint(hwnd, &ps);
        RECT r;
        GetClientRect(hwnd, &r);
        FillRect(dc, &r, GetStockObject(4));
        SetBkMode(dc, 1);
        SetTextColor(dc, 0x00E0E0E0);
        int size = Math.Clamp((r.Bottom - r.Top) / 22, 16, 40);
        IntPtr font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        IntPtr old = SelectObject(dc, font);
        var box = new RECT { Left = r.Left + size * 2, Top = r.Top, Right = r.Right - size * 2, Bottom = r.Bottom };
        string text = Words;
        fixed (char* t = text)
        {
            // Centred: measured first, then drawn in the middle.
            var measure = box;
            DrawTextW(dc, t, text.Length, &measure, 0x0010 | 0x0001 | 0x0400); // WORDBREAK | CENTER | CALCRECT
            int hgt = measure.Bottom - measure.Top;
            box.Top = r.Top + Math.Max(0, (r.Bottom - r.Top - hgt) / 2);
            box.Bottom = box.Top + hgt;
            DrawTextW(dc, t, text.Length, &box, 0x0010 | 0x0001);
        }
        SelectObject(dc, old);
        DeleteObject(font);
        EndPaint(hwnd, &ps);
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* m, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern int TranslateMessage(MSG* m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(MSG* m);
    [DllImport("user32.dll")] private static extern int PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern int DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetClientRect(IntPtr hwnd, RECT* r);
    [DllImport("user32.dll")] private static extern int AdjustWindowRectEx(RECT* r, uint style, bool menu, uint ex);
    [DllImport("user32.dll")] private static extern int InvalidateRect(IntPtr hwnd, RECT* r, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] private static extern int EndPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, RECT* r, IntPtr brush);
    [DllImport("user32.dll")] private static extern int DrawTextW(IntPtr dc, char* text, int len, RECT* r, uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, int id);
    [DllImport("user32.dll")] private static extern int TrackMouseEvent(TRACKMOUSEEVENT* t);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseCapture();
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowTextW(IntPtr hwnd, char* text);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIconW(IntPtr inst, string file, uint index);
}
