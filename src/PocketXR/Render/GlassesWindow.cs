using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PocketXR.Render;

/// <summary>What the app gives for one frame, asked for as late as possible before it is drawn.</summary>
public sealed record FrameInput(
    Quat Head,
    IReadOnlyList<(PanelPlacement Placement, ID3D11ShaderResourceView? Texture)> Panels,
    RenderOptions Options);

/// <summary>
/// A borderless window over the whole glasses display, above everything, that the mouse passes through, and the
/// thread that draws it: it waits until the display can take a frame (the swap chain's waitable object, at most one
/// frame queued), asks the app for the head pose, draws, and presents on the display's next refresh. So it runs at
/// whatever rate the glasses run.
/// </summary>
public sealed class GlassesWindow : IDisposable
{
    private readonly Renderer renderer;
    private readonly Func<FrameInput> frame;
    private readonly DisplayInfo? display;
    private readonly bool layered;
    private int x, y, width, height;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stop;
    private volatile bool running;
    private Exception? startError;
    private IntPtr hwnd;
    private volatile bool displayChanged;
    private double fps;
    private long frames;
    private int bufferWidth, bufferHeight;
    private readonly object gate = new();
    private bool lostRaised;

    /// <summary>
    /// The window stopped by itself: the display went away, or the graphics card was reset or removed. Raised once,
    /// on the drawing thread, with the reason; the window is closed when it returns. Dispose it as usual.
    /// </summary>
    public event Action<string>? Lost;

    /// <summary>Frames shown a second, measured over the last second.</summary>
    public double Fps { get { lock (gate) return fps; } }

    /// <summary>Frames shown since the start.</summary>
    public long Frames => Interlocked.Read(ref frames);

    /// <summary>The picture's size in pixels: the display's.</summary>
    public (int Width, int Height) Size { get { lock (gate) return (bufferWidth, bufferHeight); } }

    /// <summary>True while the drawing thread runs.</summary>
    public bool Running => running;

    internal IntPtr Handle => hwnd;

    /// <summary>
    /// Covers [display] exactly (its pixels, whatever the scaling) and starts drawing. [frame] is called on the
    /// drawing thread once a frame; keep it short. Throws when the window or its swap chain cannot be made.
    ///
    /// [layered] adds WS_EX_LAYERED, which is what lets clicks fall through to another program's window under this
    /// one; without it clicks on the glasses' display go nowhere. It is off because a layered window is one more
    /// thing between the picture and the display.
    /// </summary>
    public GlassesWindow(DisplayInfo display, Renderer renderer, Func<FrameInput> frame, bool layered = false)
        : this(display, display.X, display.Y, display.Width, display.Height, renderer, frame, layered)
    {
    }

    private GlassesWindow(DisplayInfo? display, int x, int y, int width, int height, Renderer renderer, Func<FrameInput> frame, bool layered)
    {
        this.display = display;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        this.renderer = renderer;
        this.frame = frame;
        this.layered = layered;
        thread = new Thread(Run) { IsBackground = true, Name = "Pocket XR glasses", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
        if (startError != null)
        {
            thread.Join();
            throw new InvalidOperationException($"The glasses' window could not be made: {startError.Message}", startError);
        }
    }

    /// <summary>
    /// For the checks only: the same window and drawing loop as a small ordinary window (not topmost, not following
    /// a display) at that place in pixels.
    /// </summary>
    internal static GlassesWindow Windowed(int x, int y, int width, int height, Renderer renderer, Func<FrameInput> frame, bool layered = false) =>
        new(null, x, y, width, height, renderer, frame, layered);

    public void Dispose()
    {
        stop = true;
        wake.Set();
        if (Thread.CurrentThread != thread) thread.Join(5000);
    }

    // MARK: - The drawing thread

    private void Run()
    {
        IDXGISwapChain1? chain = null;
        IDXGISwapChain2? chain2 = null;
        ID3D11RenderTargetView? target = null;
        string? lost = null;
        try
        {
            try
            {
                // Pixels are pixels: the window's place and size are the display's own, at any scaling.
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                current = this;
                hwnd = MakeWindow();
                chain = MakeSwapChain(hwnd, out chain2);
                running = true;
            }
            catch (Exception e)
            {
                startError = e;
                Log.P($"glasses window: could not start: {e.Message}");
                return;
            }
            finally
            {
                ready.Set();
            }
            Log.P($"glasses window: {width}x{height} at {x},{y}" + (display == null ? " (a test window)" : $" on {display.GdiName}"));

            IntPtr[] handles = { wake.SafeWaitHandle.DangerousGetHandle(), chain2.FrameLatencyWaitableObject };
            int count = 0;
            double start = Now();
            double lastLook = start;
            while (!stop)
            {
                uint got = MsgWaitForMultipleObjectsEx(2, handles, 250, QsAllInput, MwmoInputAvailable);
                while (PeekMessageW(out Msg m, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref m);
                    DispatchMessageW(ref m);
                }
                if (stop) break;
                double now = Now();
                if (display != null && (displayChanged || now - lastLook >= 1))
                {
                    displayChanged = false;
                    lastLook = now;
                    if (!FollowDisplay(out lost)) break;
                }
                if (now - start >= 1 && got != 1)
                {
                    // No frame for a second (the display is asleep): the count falls to what was shown.
                    lock (gate) fps = count / (now - start);
                    count = 0;
                    start = now;
                }
                if (got != 1) continue; // not the swap chain: a message, the timeout or the stop

                try
                {
                    if (!GetClientRect(hwnd, out Rect client)) { lost = "the window is gone"; break; }
                    int w = client.Right - client.Left, h = client.Bottom - client.Top;
                    if (w <= 0 || h <= 0) continue;
                    if (target == null || w != bufferWidth || h != bufferHeight)
                    {
                        target?.Dispose();
                        target = null;
                        if (bufferWidth != 0)
                        {
                            using (renderer.Gpu.Hold()) renderer.Gpu.Context.ClearState();
                            chain.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
                            Log.P($"glasses window: now {w}x{h}");
                        }
                        using (ID3D11Texture2D back = chain.GetBuffer<ID3D11Texture2D>(0))
                        {
                            target = renderer.Gpu.Device.CreateRenderTargetView(back);
                        }
                        lock (gate) { bufferWidth = w; bufferHeight = h; }
                    }
                    // As late as it can be: the display is ready for this frame now.
                    FrameInput input = frame();
                    renderer.Draw(target, w, h, input.Head, input.Panels, input.Options);
                    Result shown = chain.Present(1, PresentFlags.None);
                    if (shown.Failure)
                    {
                        lost = Gone(shown) ? "the graphics card was reset or removed" : $"the frame could not be shown (0x{shown.Code:X8})";
                        break;
                    }
                }
                catch (SharpGenException e)
                {
                    lost = Gone(e.ResultCode) || !renderer.Gpu.Alive
                        ? "the graphics card was reset or removed"
                        : $"drawing failed: {e.Message.Trim()}";
                    break;
                }
                Interlocked.Increment(ref frames);
                count++;
                now = Now();
                if (now - start >= 1)
                {
                    lock (gate) fps = count / (now - start);
                    count = 0;
                    start = now;
                }
            }
        }
        catch (Exception e)
        {
            lost = $"the drawing thread failed: {e.Message}";
            Log.P($"glasses window: {e}");
        }
        finally
        {
            running = false;
            lock (gate) fps = 0;
            try
            {
                if (renderer.Gpu.Alive)
                {
                    using (renderer.Gpu.Hold()) renderer.Gpu.Context.ClearState();
                }
            }
            catch (Exception) { }
            target?.Dispose();
            chain2?.Dispose();
            chain?.Dispose();
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
            current = null;
            if (lost != null && !stop && !lostRaised)
            {
                lostRaised = true;
                Log.P($"glasses window: stopped: {lost}");
                try { Lost?.Invoke(lost); } catch (Exception e) { Log.P($"glasses window: {e}"); }
            }
            else if (startError == null)
            {
                Log.P("glasses window: closed");
            }
        }
    }

    private static bool Gone(Result r) =>
        r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code
        || r.Code == Vortice.DXGI.ResultCode.DeviceHung.Code || r.Code == Vortice.DXGI.ResultCode.DriverInternalError.Code;

    private static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    /// <summary>
    /// Keeps the window on its display when Windows changes it (the glasses going from 1920x1080 to 3840x1080 for
    /// 3D keep their name and change size). False, with the reason, when the display is no longer there.
    /// </summary>
    private bool FollowDisplay(out string? why)
    {
        why = null;
        DisplayInfo? now = Displays.List().Find(d => string.Equals(d.GdiName, display!.GdiName, StringComparison.OrdinalIgnoreCase));
        if (now == null || now.Width <= 0 || now.Height <= 0)
        {
            why = "the glasses' display is gone";
            return false;
        }
        if (now.X != x || now.Y != y || now.Width != width || now.Height != height)
        {
            x = now.X; y = now.Y; width = now.Width; height = now.Height;
            Log.P($"glasses window: the display is now {width}x{height} at {x},{y}");
            SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate);
        }
        return true;
    }

    private IDXGISwapChain1 MakeSwapChain(IntPtr window, out IDXGISwapChain2 second)
    {
        using IDXGIDevice dxgiDevice = renderer.Gpu.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        var d = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        IDXGISwapChain1 chain = factory.CreateSwapChainForHwnd(renderer.Gpu.Device, window, d, null, null);
        try
        {
            factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAll);
            second = chain.QueryInterface<IDXGISwapChain2>();
            second.MaximumFrameLatency = 1;
            return chain;
        }
        catch (Exception)
        {
            chain.Dispose();
            throw;
        }
    }

    // MARK: - The window

    [ThreadStatic] private static GlassesWindow? current;
    private static readonly WndProc Proc = WindowProc; // kept, so the window class's pointer to it stays good
    private static readonly object ClassGate = new();
    private static bool classMade;
    private const string ClassName = "PocketXR.GlassesWindow";

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40, SwpNoZOrder = 0x4;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint QsAllInput = 0x04FF, MwmoInputAvailable = 0x4;
    private const uint WmClose = 0x10, WmEraseBkgnd = 0x14, WmMouseActivate = 0x21, WmDisplayChange = 0x7E, WmNcHitTest = 0x84;

    private IntPtr MakeWindow()
    {
        IntPtr instance = GetModuleHandleW(null);
        lock (ClassGate)
        {
            if (!classMade)
            {
                var c = new WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<WndClassEx>(),
                    Proc = Marshal.GetFunctionPointerForDelegate(Proc),
                    Instance = instance,
                    Background = GetStockObject(4), // BLACK_BRUSH: black until the first frame
                    ClassName = ClassName,
                };
                if (RegisterClassExW(ref c) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
                classMade = true;
            }
        }
        // Never activated, never in the taskbar or Alt+Tab, and the mouse passes through.
        uint ex = WsExTransparent | WsExNoActivate | WsExToolWindow;
        if (display != null) ex |= WsExTopmost;
        if (layered) ex |= WsExLayered;
        IntPtr made = CreateWindowExW(ex, ClassName, "Pocket XR glasses", WsPopup, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (made == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        if (layered) SetLayeredWindowAttributes(made, 0, 255, 2); // LWA_ALPHA, opaque: a layered window shows nothing until told
        if (display != null)
        {
            SetWindowPos(made, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        }
        else
        {
            SetWindowPos(made, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow | SwpNoZOrder);
        }
        return made;
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmNcHitTest: return new IntPtr(-1); // HTTRANSPARENT
            case WmMouseActivate: return new IntPtr(3); // MA_NOACTIVATE
            case WmEraseBkgnd: return new IntPtr(1);
            case WmClose: return IntPtr.Zero; // only Dispose closes it
            case WmDisplayChange:
                if (current != null) current.displayChanged = true;
                break;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr Proc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg message);
    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
}
