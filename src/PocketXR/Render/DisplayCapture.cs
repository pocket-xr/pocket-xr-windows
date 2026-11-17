using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PocketXR.Render;

/// <summary>
/// Pictures of one display, with DXGI Desktop Duplication (in Windows since 8: no prompt, no border): each new
/// picture is copied on the graphics card into a texture the renderer samples, and the renderer takes the newest.
/// The pointer is drawn into the picture while it is on that display (Desktop Duplication leaves it out).
///
/// It runs on its own thread with a Direct3D device of its own on the display's graphics card, and the picture is a
/// texture shared between that device and the renderer's: nothing passes through system memory, and the drawing loop
/// never waits for a picture. (Duplicating on the renderer's own device was tried first, 16 Nov 2026: waiting for the
/// next picture holds that device's context, and the drawing loop fell from 177 to 81 frames a second.)
/// A display on another graphics card than the renderer's (a laptop with two) cannot share a texture with it: then
/// each picture is read back and uploaded (<see cref="CopiesThroughMemory"/>).
/// </summary>
public sealed class DisplayCapture : IDisposable
{
    public DisplayInfo Display { get; }
    private readonly GpuDevice gpu;
    private Thread? thread;
    private volatile bool stop;
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly object gate = new();

    /// <summary>The capture's own device, kept from one duplication to the next.</summary>
    private GpuDevice? work;
    /// <summary>The picture the renderer samples. Kept across a lost screen, so the last picture stays up.</summary>
    private Picture? final;
    private volatile ID3D11ShaderResourceView? latest;
    /// <summary>Pictures of an older size: the renderer may still hold one for a frame, so they go a little later.</summary>
    private readonly List<(Picture Picture, long At)> retired = new();

    private long frames;
    private double fps;
    private int width, height;
    private volatile string? error;
    private volatile bool copies;
    private int pointerShapes;
    private int pointerType;
    private int pointerWidth, pointerHeight;

    /// <summary>For the checks: read back and upload even on one graphics card, as with two.</summary>
    internal bool ForceCopyThroughMemory;

    /// <summary>[gpu] is the renderer's device: the one <see cref="Texture"/> is for.</summary>
    public DisplayCapture(GpuDevice gpu, DisplayInfo display)
    {
        this.gpu = gpu;
        Display = display;
    }

    /// <summary>
    /// The newest picture as a texture on the renderer's device, null until the first one. Ask for it each frame:
    /// it is another object after the display changes size.
    /// </summary>
    public ID3D11ShaderResourceView? Texture => latest;

    /// <summary>How the screen's panel is turned against the desktop (DXGI_MODE_ROTATION: 1 upright, 2 a quarter
    /// turn, 3 half, 4 three quarters). The picture is as the panel is wired; the renderer turns it back.</summary>
    public int Rotation => rotation;
    private volatile int rotation = 1;

    /// <summary>The picture's size in pixels, (0, 0) until the first one.</summary>
    public (int Width, int Height) Size { get { lock (gate) return (width, height); } }

    /// <summary>New pictures in the last second (a still desktop gives none: Windows only sends what changed).</summary>
    public double Fps { get { lock (gate) return fps; } }

    /// <summary>New pictures since the start.</summary>
    public long Frames => Interlocked.Read(ref frames);

    /// <summary>True when the display is on another graphics card than the renderer, so pictures pass through memory.</summary>
    public bool CopiesThroughMemory => copies;

    /// <summary>Why there is no picture now (the display is gone, the lock screen is up), null while capturing.</summary>
    public string? Error => error;

    /// <summary>Pointer shapes Windows has sent, and the last one's kind (1 monochrome, 2 colour, 4 masked colour) and size.</summary>
    public (int Count, int Type, int Width, int Height) PointerShapes
    {
        get { lock (gate) return (pointerShapes, pointerType, pointerWidth, pointerHeight); }
    }

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = $"Pocket XR capture {Display.GdiName}", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Stops capturing. The renderer must no longer be drawing with <see cref="Texture"/>.</summary>
    public void Dispose()
    {
        stop = true;
        stopped.Set();
        thread?.Join(3000);
        thread = null;
        latest = null;
        final?.Dispose();
        final = null;
        foreach (var r in retired) r.Picture.Dispose();
        retired.Clear();
        work?.Dispose();
        work = null;
    }

    private void Run()
    {
        while (!stop)
        {
            bool lost = false;
            try
            {
                Session();
                lost = true;
            }
            catch (Exception e)
            {
                string why = e is SharpGenException s ? $"{s.Message.Trim()} (0x{s.HResult:X8})" : e.Message;
                if (why != error)
                {
                    error = why;
                    Log.P($"capture {Display.GdiName}: {why}");
                }
            }
            lock (gate) fps = 0;
            // The screen was lost (a mode change, the lock screen, a UAC prompt): open it again. One that will not
            // open (the lock screen is up, the display is gone) is tried once a second.
            stopped.Wait(lost ? 50 : 1000);
        }
    }

    private static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    /// <summary>One duplication, from opening it to losing it. Returns when Windows takes it away; throws when it will not open.</summary>
    private void Session()
    {
        if (!Outputs.Find(Display.GdiName, out IDXGIAdapter1? adapter, out IDXGIOutput? output) || adapter == null || output == null)
        {
            throw new InvalidOperationException("that display is not there");
        }
        IDXGIOutputDuplication? duplication = null;
        PointerPainter? painter = null;
        ID3D11Texture2D? clean = null;
        try
        {
            long luid = Outputs.Luid(adapter);
            if (work == null || work.AdapterLuid != luid || !work.Alive)
            {
                // The picture belongs to the device it was made on.
                RetireFinal();
                work?.Dispose();
                work = null;
                work = GpuDevice.Create(luid);
            }
            GpuDevice device = work;
            ID3D11DeviceContext ctx = device.Context;
            bool through = luid != gpu.AdapterLuid || ForceCopyThroughMemory;
            copies = through;
            using (IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>())
            {
                duplication = output1.DuplicateOutput(device.Device);
            }
            OutduplDescription about = duplication.Description;
            rotation = (int)about.Rotation is >= 1 and <= 4 ? (int)about.Rotation : 1;
            painter = new PointerPainter(device.Device);
            error = null;
            Log.P($"capture {Display.GdiName}: open, {about.ModeDescription.Width}x{about.ModeDescription.Height}"
                + (about.Rotation is ModeRotation.Identity or ModeRotation.Unspecified ? "" : $", turned ({about.Rotation}): shown as the panel is wired, not as it stands")
                + (through ? $", on {device.AdapterName}: copied through memory" : ""));

            int w = 0, h = 0;
            bool shownValid = false;      // the picture holds a whole desktop picture
            bool pointerVisible = false;
            int pointerX = 0, pointerY = 0;
            (int X, int Y, int W, int H)? drawn = null; // where the pointer is drawn in the picture now
            bool pending = false;         // the pointer moved and is not drawn at its new place yet
            double lastCompose = 0;
            double pointerInterval = 1.0 / Math.Max(Display.RefreshHz, 60);
            int count = 0;
            double countStart = Now();
            byte[] shapeBuffer = Array.Empty<byte>();

            while (!stop)
            {
                uint wait = 100;
                if (pending) wait = (uint)Math.Clamp((int)Math.Ceiling((lastCompose + pointerInterval - Now()) * 1000), 1, 100);
                Result result = duplication.AcquireNextFrame(wait, out OutduplFrameInfo info, out IDXGIResource? resource);
                double now = Now();
                if (now - countStart >= 1)
                {
                    lock (gate) fps = count / (now - countStart);
                    count = 0;
                    countStart = now;
                }
                bool image = false;
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                {
                    if (!pending) continue;
                }
                else if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                {
                    Log.P($"capture {Display.GdiName}: the screen changed, opening it again");
                    return;
                }
                else if (result.Failure || resource == null)
                {
                    result.CheckError();
                    continue;
                }
                else
                {
                    try
                    {
                        if (info.LastPresentTime != 0)
                        {
                            using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();
                            Texture2DDescription d = frame.Description;
                            if (clean == null || d.Width != w || d.Height != h)
                            {
                                clean?.Dispose();
                                w = (int)d.Width;
                                h = (int)d.Height;
                                clean = device.Device.CreateTexture2D(new Texture2DDescription(
                                    d.Format, d.Width, d.Height, 1, 1, BindFlags.ShaderResource));
                                shownValid = false;
                                drawn = null;
                            }
                            ctx.CopyResource(clean, frame);
                            image = true;
                            count++;
                            Interlocked.Increment(ref frames);
                        }
                        if (info.PointerShapeBufferSize > 0)
                        {
                            if (shapeBuffer.Length < info.PointerShapeBufferSize) shapeBuffer = new byte[info.PointerShapeBufferSize];
                            OutduplPointerShapeInfo shape;
                            Result got;
                            unsafe
                            {
                                fixed (byte* p = shapeBuffer)
                                {
                                    got = duplication.GetFramePointerShape((uint)shapeBuffer.Length, (IntPtr)p, out _, out shape);
                                }
                            }
                            if (got.Success)
                            {
                                PointerImage? picture = PointerImage.From((int)shape.Type, (int)shape.Width, (int)shape.Height,
                                    (int)shape.Pitch, shapeBuffer.AsSpan(0, (int)info.PointerShapeBufferSize));
                                painter.SetShape(picture?.Turned(rotation));
                                lock (gate)
                                {
                                    pointerShapes++;
                                    pointerType = (int)shape.Type;
                                    pointerWidth = picture?.Width ?? 0;
                                    pointerHeight = picture?.Height ?? 0;
                                }
                                pending = true;
                            }
                        }
                        if (info.LastMouseUpdateTime != 0)
                        {
                            // The position is the shape's top left (the hot spot is already taken off).
                            pointerVisible = info.PointerPosition.Visible;
                            pointerX = info.PointerPosition.Position.X;
                            pointerY = info.PointerPosition.Position.Y;
                            pending = true;
                        }
                    }
                    finally
                    {
                        resource.Dispose();
                        duplication.ReleaseFrame();
                    }
                }

                if (clean == null)
                {
                    // No picture yet: the pointer is drawn with the first one.
                    pending = false;
                    continue;
                }
                // A new picture is shown at once. The pointer alone is redrawn at most once a refresh of that display.
                if (!image && now - lastCompose < pointerInterval) continue;
                if (final == null || final.Width != w || final.Height != h || final.Through != through)
                {
                    RetireFinal();
                    final = new Picture(device, gpu, w, h, through);
                    shownValid = false;
                }
                Picture target = final;
                if (image || !shownValid)
                {
                    ctx.CopyResource(target.WorkTexture, clean);
                }
                else if (drawn is (int ox, int oy, int ow, int oh))
                {
                    // Only the pointer moved: put back the picture under its old place.
                    ctx.CopySubresourceRegion(target.WorkTexture, 0, (uint)ox, (uint)oy, 0, clean, 0,
                        new Box(ox, oy, 0, ox + ow, oy + oh, 1));
                }
                drawn = null;
                if (pointerVisible && painter.HasShape)
                {
                    // The pointer comes in the desktop's own place; the picture is as the panel is wired, so on a turned
                    // screen the place, and the arrow itself (see PointerImage.Turned), are turned to match.
                    // The shape's size on the desktop: a quarter-turned shape has its width and height swapped.
                    bool across = rotation is 2 or 4;
                    int sw = across ? painter.Height : painter.Width, sh = across ? painter.Width : painter.Height;
                    (int px, int py) = rotation switch
                    {
                        2 => (pointerY, h - pointerX - sw),
                        3 => (w - pointerX - sw, h - pointerY - sh),
                        4 => (w - pointerY - sh, pointerX),
                        _ => (pointerX, pointerY),
                    };
                    painter.Draw(ctx, target.Target, w, h, px, py);
                    int x0 = Math.Max(px, 0), y0 = Math.Max(py, 0);
                    int x1 = Math.Min(px + painter.Width, w), y1 = Math.Min(py + painter.Height, h);
                    if (x1 > x0 && y1 > y0) drawn = (x0, y0, x1 - x0, y1 - y0);
                }
                shownValid = true;
                if (target.Staging != null)
                {
                    // Two graphics cards: read the finished picture back and hand it to the renderer's card.
                    ctx.CopyResource(target.Staging, target.WorkTexture);
                    MappedSubresource map = ctx.Map(target.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        using (gpu.Hold()) gpu.Context.UpdateSubresource(target.RenderTexture, 0, null, map.DataPointer, map.RowPitch, 0);
                    }
                    finally
                    {
                        ctx.Unmap(target.Staging, 0);
                    }
                }
                else
                {
                    // Sent to the graphics card now, so the renderer's next frame has it.
                    ctx.Flush();
                }
                pending = false;
                lastCompose = now;
                lock (gate) { width = w; height = h; }
                latest = target.View;
            }
        }
        finally
        {
            painter?.Dispose();
            clean?.Dispose();
            duplication?.Dispose();
            output.Dispose();
            adapter.Dispose();
        }
    }

    /// <summary>Puts the picture aside; it is let go a few seconds later, since the renderer may be drawing from it.</summary>
    private void RetireFinal()
    {
        long now = Stopwatch.GetTimestamp();
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            if (now - retired[i].At > 3 * Stopwatch.Frequency)
            {
                retired[i].Picture.Dispose();
                retired.RemoveAt(i);
            }
        }
        if (final != null) retired.Add((final, now));
        final = null;
    }

    /// <summary>
    /// One BGRA picture the capture draws into on its device and the renderer samples on its own: the same texture
    /// shared between the two, or with [through] two textures and a read-back between them.
    /// </summary>
    private sealed class Picture : IDisposable
    {
        public readonly ID3D11Texture2D WorkTexture;
        public readonly ID3D11RenderTargetView Target;
        public readonly ID3D11Texture2D? Staging;
        public readonly ID3D11Texture2D RenderTexture;
        public readonly ID3D11ShaderResourceView View;
        public readonly int Width, Height;
        public readonly bool Through;

        public Picture(GpuDevice work, GpuDevice render, int width, int height, bool through)
        {
            Width = width;
            Height = height;
            Through = through;
            WorkTexture = work.Device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default, CpuAccessFlags.None, 1, 0, through ? ResourceOptionFlags.None : ResourceOptionFlags.Shared));
            Target = work.Device.CreateRenderTargetView(WorkTexture);
            if (through)
            {
                Staging = work.Device.CreateTexture2D(new Texture2DDescription(
                    Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                RenderTexture = render.Device.CreateTexture2D(new Texture2DDescription(
                    Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
            }
            else
            {
                using IDXGIResource shared = WorkTexture.QueryInterface<IDXGIResource>();
                RenderTexture = render.Device.OpenSharedResource<ID3D11Texture2D>(shared.SharedHandle);
            }
            View = render.Device.CreateShaderResourceView(RenderTexture);
        }

        public void Dispose()
        {
            View.Dispose();
            RenderTexture.Dispose();
            Staging?.Dispose();
            Target.Dispose();
            WorkTexture.Dispose();
        }
    }
}

/// <summary>
/// A pointer shape from Desktop Duplication as two BGRA pictures: one laid over the screen by its alpha, and one
/// that turns the screen's colours over (the text beam and the cross of some themes), null when no pixel does.
/// </summary>
internal sealed class PointerImage
{
    public int Width, Height;
    /// <summary>Blue, green, red, alpha per pixel, alpha not yet multiplied in.</summary>
    public byte[] Colour = Array.Empty<byte>();
    /// <summary>Blue, green, red per pixel: white turns the screen's colour over, black leaves it.</summary>
    public byte[]? Invert;

    public const int Monochrome = 1, Color = 2, MaskedColor = 4;

    /// <summary>
    /// The shape turned as a screen's panel is wired against its desktop (DXGI_MODE_ROTATION: 2 a quarter turn, 3
    /// half, 4 three quarters), so it can be drawn into a picture taken as the panel is wired. The same turn the
    /// renderer undoes, so the arrow ends up upright in the glasses. 1 or anything else: unchanged.
    /// </summary>
    public PointerImage Turned(int rotation)
    {
        if (rotation is not (2 or 3 or 4)) return this;
        int w = Width, h = Height;
        bool across = rotation != 3;
        int nw = across ? h : w, nh = across ? w : h;
        byte[] Turn(byte[] src)
        {
            var dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    (int xn, int yn) = rotation switch
                    {
                        2 => (y, w - 1 - x),
                        3 => (w - 1 - x, h - 1 - y),
                        _ => (h - 1 - y, x),
                    };
                    Buffer.BlockCopy(src, (y * w + x) * 4, dst, (yn * nw + xn) * 4, 4);
                }
            }
            return dst;
        }
        return new PointerImage { Width = nw, Height = nh, Colour = Turn(Colour), Invert = Invert == null ? null : Turn(Invert) };
    }

    /// <summary>
    /// [type] 1: a 1 bit AND mask above a 1 bit XOR mask ([height] is both). AND 0 XOR 0 black, AND 0 XOR 1 white,
    /// AND 1 XOR 0 the screen, AND 1 XOR 1 the screen turned over.
    /// [type] 2: colour with alpha.
    /// [type] 4: colour whose alpha byte is a mask: 0 the colour replaces the screen, 0xFF the colour is XORed with
    /// the screen (black leaves it, white turns it over).
    /// </summary>
    public static PointerImage? From(int type, int width, int height, int pitch, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || pitch <= 0) return null;
        if (type == Monochrome)
        {
            int h = height / 2;
            if (h <= 0 || data.Length < pitch * h * 2 || pitch * 8 < width) return null;
            var p = new PointerImage { Width = width, Height = h, Colour = new byte[width * h * 4] };
            byte[] invert = new byte[width * h * 4];
            bool any = false;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int bit = 0x80 >> (x & 7);
                    bool and = (data[y * pitch + (x >> 3)] & bit) != 0;
                    bool xor = (data[(y + h) * pitch + (x >> 3)] & bit) != 0;
                    int o = (y * width + x) * 4;
                    if (!and)
                    {
                        byte c = xor ? (byte)255 : (byte)0;
                        p.Colour[o] = c; p.Colour[o + 1] = c; p.Colour[o + 2] = c; p.Colour[o + 3] = 255;
                    }
                    else if (xor)
                    {
                        invert[o] = 255; invert[o + 1] = 255; invert[o + 2] = 255; invert[o + 3] = 255;
                        any = true;
                    }
                }
            }
            if (any) p.Invert = invert;
            return p;
        }
        if (type == Color || type == MaskedColor)
        {
            if (pitch < width * 4 || data.Length < pitch * height) return null;
            var p = new PointerImage { Width = width, Height = height, Colour = new byte[width * height * 4] };
            byte[]? invert = type == MaskedColor ? new byte[width * height * 4] : null;
            bool any = false;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * pitch + x * 4;
                    int o = (y * width + x) * 4;
                    if (type == Color)
                    {
                        p.Colour[o] = data[i]; p.Colour[o + 1] = data[i + 1]; p.Colour[o + 2] = data[i + 2]; p.Colour[o + 3] = data[i + 3];
                    }
                    else if (data[i + 3] == 0)
                    {
                        p.Colour[o] = data[i]; p.Colour[o + 1] = data[i + 1]; p.Colour[o + 2] = data[i + 2]; p.Colour[o + 3] = 255;
                    }
                    else if ((data[i] | data[i + 1] | data[i + 2]) != 0)
                    {
                        invert![o] = data[i]; invert[o + 1] = data[i + 1]; invert[o + 2] = data[i + 2]; invert[o + 3] = 255;
                        any = true;
                    }
                }
            }
            if (any) p.Invert = invert;
            return p;
        }
        return null;
    }
}

/// <summary>
/// Draws a pointer shape into a picture on the graphics card: the colour picture by its alpha, then the turning-over
/// picture with a blend that gives colour x (1 - screen) + screen x (1 - colour), which is the screen for black, the
/// screen turned over for white (exact for those two, the only ones Windows' own pointers use), and near XOR between.
/// </summary>
internal sealed class PointerPainter : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader fragment;
    private readonly ID3D11Buffer uniforms;
    private readonly ID3D11BlendState over;
    private readonly ID3D11BlendState turn;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState raster;
    private ID3D11Texture2D? colourTexture, invertTexture;
    private ID3D11ShaderResourceView? colour, invert;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasShape => colour != null;

    public PointerPainter(ID3D11Device device)
    {
        this.device = device;
        using (Blob vs = GpuDevice.Compile(ShaderSource, "pointerVertex", "vs_4_0", "pointer"))
        using (Blob ps = GpuDevice.Compile(ShaderSource, "pointerFragment", "ps_4_0", "pointer"))
        {
            vertex = device.CreateVertexShader(vs);
            fragment = device.CreatePixelShader(ps);
        }
        uniforms = device.CreateBuffer(16, BindFlags.ConstantBuffer);
        over = device.CreateBlendState(Blending(Blend.SourceAlpha, Blend.InverseSourceAlpha));
        turn = device.CreateBlendState(Blending(Blend.InverseDestinationColor, Blend.InverseSourceColor));
        sampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, 0f, float.MaxValue));
        raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    private static BlendDescription Blending(Blend source, Blend destination)
    {
        var b = new BlendDescription();
        b.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            // The picture's alpha is left as Windows gave it.
            RenderTargetWriteMask = ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue,
        };
        return b;
    }

    public void SetShape(PointerImage? shape)
    {
        colour?.Dispose(); colourTexture?.Dispose(); invert?.Dispose(); invertTexture?.Dispose();
        colour = null; colourTexture = null; invert = null; invertTexture = null;
        Width = 0;
        Height = 0;
        if (shape == null) return;
        Width = shape.Width;
        Height = shape.Height;
        (colourTexture, colour) = Make(shape.Colour, shape.Width, shape.Height);
        if (shape.Invert != null) (invertTexture, invert) = Make(shape.Invert, shape.Width, shape.Height);
    }

    private unsafe (ID3D11Texture2D, ID3D11ShaderResourceView) Make(byte[] bgra, int width, int height)
    {
        fixed (byte* p = bgra)
        {
            ID3D11Texture2D t = device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable),
                new SubresourceData((IntPtr)p, (uint)(width * 4)));
            return (t, device.CreateShaderResourceView(t));
        }
    }

    /// <summary>
    /// The shape with its top left at ([x], [y]) pixels of [target] ([width] by [height]). The caller holds the
    /// context; nothing stays set on it.
    /// </summary>
    public void Draw(ID3D11DeviceContext ctx, ID3D11RenderTargetView target, int width, int height, int x, int y)
    {
        if (colour == null || width <= 0 || height <= 0) return;
        // Left, top, right, bottom in clip space (y up).
        var rect = new Vec4f(2f * x / width - 1, 1 - 2f * y / height, 2f * (x + Width) / width - 1, 1 - 2f * (y + Height) / height);
        ctx.UpdateSubresource(in rect, uniforms);
        ctx.OMSetRenderTargets(target);
        ctx.RSSetViewport(0, 0, width, height, 0f, 1f);
        ctx.RSSetState(raster);
        ctx.IASetInputLayout(null!);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(vertex);
        ctx.VSSetConstantBuffer(0, uniforms);
        ctx.PSSetShader(fragment);
        ctx.PSSetSampler(0, sampler);
        ctx.OMSetBlendState(over);
        ctx.PSSetShaderResource(0, colour);
        ctx.Draw(4, 0);
        if (invert != null)
        {
            ctx.OMSetBlendState(turn);
            ctx.PSSetShaderResource(0, invert);
            ctx.Draw(4, 0);
        }
        ctx.ClearState();
    }

    public void Dispose()
    {
        SetShape(null);
        raster.Dispose();
        sampler.Dispose();
        turn.Dispose();
        over.Dispose();
        uniforms.Dispose();
        fragment.Dispose();
        vertex.Dispose();
    }

    private const string ShaderSource = @"
cbuffer PointerUniforms : register(b0) { float4 rect; };
struct PointerOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Texture2D tex : register(t0);
SamplerState s : register(s0);

PointerOut pointerVertex(uint vid : SV_VertexID) {
    float2 c = float2(vid & 1, vid >> 1);
    PointerOut o;
    o.position = float4(lerp(rect.x, rect.z, c.x), lerp(rect.y, rect.w, c.y), 0.0, 1.0);
    o.uv = c;
    return o;
}

float4 pointerFragment(PointerOut i) : SV_Target {
    return tex.Sample(s, i.uv);
}
";
}
