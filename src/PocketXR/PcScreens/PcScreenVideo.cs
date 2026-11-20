using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PocketXR.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: the picture. Each frame from the stream is decoded by Windows' own video decoder (Media Foundation,
/// on the graphics card through DXVA) into a Direct3D texture, and the graphics card's video processor scales it and
/// turns it to RGB straight into the window's swap chain, at once: no pacing hold, the newest picture shown as soon as
/// it is decoded, as the phone does.
///
/// Called on moonlight-common-c's decoder thread; <see cref="Resize"/> from the window's thread, so the swap chain is
/// guarded by a lock.
/// </summary>
public sealed unsafe class PcScreenVideo : IDisposable
{
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid D3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Hevc = new("43564548-0000-0010-8000-00aa00389b71");
    private static readonly Guid Av1 = new("31305641-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid DecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);

    private const uint EnumSyncMft = 0x1, EnumAsyncMft = 0x2, EnumHardware = 0x4, EnumLocal = 0x80, EnumSortAndFilter = 0x40;

    private readonly IntPtr hwnd;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Multithread multithread;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly IMFDXGIDeviceManager manager;
    private IMFTransform? decoder;
    private IDXGISwapChain1? swapChain;
    private ID3D11VideoProcessorEnumerator? vpEnum;
    private ID3D11VideoProcessor? processor;
    private ID3D11VideoProcessorOutputView? outputView;
    private readonly Dictionary<(nint, uint), ID3D11VideoProcessorInputView> inputViews = new();
    private readonly object gate = new();
    private int streamW, streamH, fps;
    private int windowW, windowH;
    private bool sizeChanged;
    private long frameIndex;
    private bool providesSamples;

    /// <summary>What was measured, for the log and the stats line.</summary>
    public readonly PcScreenStats Stats = new();

    /// <summary>Which of H.264, HEVC and AV1 this PC decodes: Moonlight's video format bits.</summary>
    public static int DecodableFormats()
    {
        MediaFactory.MFStartup(true).CheckError();
        int formats = 0;
        if (CountDecoders(H264) > 0) formats |= Moonlight.VideoH264;
        if (CountDecoders(Hevc) > 0) formats |= Moonlight.VideoH265;
        if (CountDecoders(Av1) > 0) formats |= Moonlight.VideoAv1;
        return formats;
    }

    private static int CountDecoders(Guid subtype)
    {
        var input = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = subtype };
        MediaFactory.MFTEnumEx(DecoderCategory, EnumSyncMft | EnumLocal | EnumSortAndFilter, input, null, out IntPtr list, out uint count);
        ReleaseActivates(list, count);
        return (int)count;
    }

    private static void ReleaseActivates(IntPtr list, uint count)
    {
        if (list == IntPtr.Zero) return;
        for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(list, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(list);
    }

    public PcScreenVideo(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        MediaFactory.MFStartup(true).CheckError();
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            levels, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        device = dev;
        context = ctx;
        multithread = context.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context.QueryInterface<ID3D11VideoContext>();
        manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
    }

    /// <summary>The decoder for this stream's codec and size. 0 on success.</summary>
    public int Setup(int format, int width, int height, int frameRate)
    {
        lock (gate)
        {
            streamW = width;
            streamH = height;
            fps = frameRate;
            Guid sub = (format & 0x0F00) != 0 ? Hevc : (format & 0xF000) != 0 ? Av1 : H264;
            var input = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = sub };
            MediaFactory.MFTEnumEx(DecoderCategory, EnumSyncMft | EnumLocal | EnumSortAndFilter, input, null, out IntPtr list, out uint count);
            try
            {
                for (int i = 0; i < count && decoder == null; i++)
                {
                    var activate = new IMFActivate(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                    try
                    {
                        string name = "";
                        try { name = activate.GetString(new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3")); } catch (SharpGenException) { }
                        var t = activate.ActivateObject<IMFTransform>();
                        if (TryConfigure(t, sub, width, height, frameRate))
                        {
                            decoder = t;
                            Log.P($"pc screen: decoder {name} for {CodecName(sub)} {width}x{height}@{frameRate}, " +
                                $"{(providesSamples ? "its own Direct3D textures" : "no textures of its own")}");
                        }
                        else
                        {
                            t.Dispose();
                        }
                    }
                    catch (SharpGenException e)
                    {
                        Log.P($"pc screen: a {CodecName(sub)} decoder refused: {e.Message}");
                    }
                    finally
                    {
                        activate.Release();
                    }
                }
            }
            finally
            {
                if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list);
            }
            if (decoder == null)
            {
                Log.P($"pc screen: no {CodecName(sub)} decoder took {width}x{height}");
                return -1;
            }
            Stats.Reset(width, height, frameRate, CodecName(sub));
            return 0;
        }
    }

    private static string CodecName(Guid sub) => sub == Hevc ? "HEVC" : sub == Av1 ? "AV1" : "H.264";

    private bool TryConfigure(IMFTransform t, Guid sub, int w, int h, int frameRate)
    {
        var attrs = t.Attributes;
        if (attrs.GetUInt32(D3D11Aware) == 0)
        {
            Log.P("pc screen: decoder is not Direct3D 11 aware");
            return false;
        }
        attrs.Set(LowLatency, 1u);
        t.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)manager.NativePointer);
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MajorType, VideoMajor);
        inType.Set(SubType, sub);
        inType.Set(FrameSize, ((ulong)w << 32) | (uint)h);
        inType.Set(FrameRate, ((ulong)frameRate << 32) | 1u);
        inType.Set(InterlaceMode, 2u);
        t.SetInputType(0, inType, 0);
        if (!SetNv12Output(t)) return false;
        providesSamples = (t.GetOutputStreamInfo(0).Flags & 0x100) != 0;
        t.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        t.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        return providesSamples;
    }

    private static bool SetNv12Output(IMFTransform t)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = t.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { return false; }
            using (type)
            {
                if (type.GetGUID(SubType) == Nv12)
                {
                    t.SetOutputType(0, type, 0);
                    return true;
                }
            }
        }
    }

    /// <summary>The window's client area, in pixels.</summary>
    public void Resize(int w, int h)
    {
        lock (gate)
        {
            if (w == windowW && h == windowH) return;
            windowW = Math.Max(1, w);
            windowH = Math.Max(1, h);
            sizeChanged = true;
        }
    }

    /// <summary>
    /// One whole frame from the stream: decoded and shown. Returns Moonlight's DR_OK, or DR_NEED_IDR when the decoder
    /// could not take it (the host then sends a key frame).
    /// </summary>
    public int Frame(ReadOnlySpan<byte> data, bool idr, ulong receiveUs, ulong enqueueUs)
    {
        lock (gate)
        {
            if (decoder == null) return Moonlight.DrNeedIdr;
            long t0 = Stopwatch.GetTimestamp();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
            buffer.Lock(out IntPtr p, out _, out _);
            data.CopyTo(new Span<byte>((void*)p, data.Length));
            buffer.Unlock();
            buffer.CurrentLength = data.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            long ticks = 10_000_000L / Math.Max(1, fps);
            sample.SampleTime = frameIndex * ticks;
            sample.SampleDuration = ticks;
            frameIndex++;
            try
            {
                decoder.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException e) when (e.ResultCode.Code == NotAccepting)
            {
                // A frame still inside: take it out first, then give this one again.
                Drain();
                try { decoder.ProcessInput(0, sample, 0); }
                catch (SharpGenException) { Stats.Dropped++; return Moonlight.DrNeedIdr; }
            }
            catch (SharpGenException e)
            {
                Log.P($"pc screen: decoder took no frame: {e.Message}");
                Stats.Dropped++;
                return Moonlight.DrNeedIdr;
            }
            bool shown = Drain();
            long t1 = Stopwatch.GetTimestamp();
            ulong now = Moonlight.PxrNowUs();
            Stats.Frame(shown, idr, data.Length, (t1 - t0) * 1000.0 / Stopwatch.Frequency,
                (now - enqueueUs) / 1000.0, (now - receiveUs) / 1000.0);
            return Moonlight.DrOk;
        }
    }

    /// <summary>Takes every decoded picture out of the decoder; the newest is shown. True if one was shown.</summary>
    private bool Drain()
    {
        bool shown = false;
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0 };
            Result r = decoder!.ProcessOutput(0, 1, ref output, out _);
            if (r.Code == StreamChange)
            {
                output.Sample?.Dispose();
                if (!SetNv12Output(decoder)) return shown;
                continue;
            }
            if (r.Code == NeedMoreInput || r.Failure)
            {
                output.Sample?.Dispose();
                output.Events?.Dispose();
                if (r.Failure && r.Code != NeedMoreInput) Log.P($"pc screen: decoder output: {r}");
                return shown;
            }
            using (var s = output.Sample)
            {
                output.Events?.Dispose();
                if (s != null) shown |= Present(s);
            }
        }
    }

    private bool Present(IMFSample sample)
    {
        using var mb = sample.GetBufferByIndex(0);
        using var dxgi = mb.QueryInterface<IMFDXGIBuffer>();
        IntPtr res = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
        using var texture = new ID3D11Texture2D(res);
        uint slice = dxgi.SubresourceIndex;
        multithread.Enter();
        try
        {
            if (!EnsureOutput()) return false;
            var key = (texture.NativePointer, slice);
            if (!inputViews.TryGetValue(key, out var view))
            {
                var desc = new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = slice },
                };
                view = videoDevice.CreateVideoProcessorInputView(texture, vpEnum!, desc);
                texture.AddRef(); // the key's pointer stays valid while its view is kept
                inputViews[key] = view;
            }
            var fit = PcScreenLogic.Fit(windowW, windowH, streamW, streamH);
            videoContext.VideoProcessorSetStreamSourceRect(processor!, 0, true, new Vortice.RawRect(0, 0, streamW, streamH));
            videoContext.VideoProcessorSetStreamDestRect(processor!, 0, true, new Vortice.RawRect(fit.X, fit.Y, fit.X + fit.W, fit.Y + fit.H));
            var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = view } };
            videoContext.VideoProcessorBlt(processor!, outputView!, 0, 1, streams);
            swapChain!.Present(0, PresentFlags.None);
            return true;
        }
        finally
        {
            multithread.Leave();
        }
    }

    /// <summary>The swap chain, video processor and output view at the window's size, made again after a resize.</summary>
    private bool EnsureOutput()
    {
        if (windowW <= 0 || windowH <= 0) return false;
        if (swapChain == null)
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)windowW,
                Height = (uint)windowH,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
            };
            swapChain = factory.CreateSwapChainForHwnd(device, hwnd, desc);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            sizeChanged = true;
        }
        if (sizeChanged)
        {
            outputView?.Dispose();
            outputView = null;
            swapChain.ResizeBuffers(2, (uint)windowW, (uint)windowH, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            processor?.Dispose();
            vpEnum?.Dispose();
            foreach (var v in inputViews) { v.Value.Dispose(); Marshal.Release(v.Key.Item1); }
            inputViews.Clear();
            var content = new VideoProcessorContentDescription
            {
                InputWidth = (uint)streamW,
                InputHeight = (uint)streamH,
                OutputWidth = (uint)windowW,
                OutputHeight = (uint)windowH,
                InputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                OutputFrameRate = new Rational((uint)Math.Max(1, fps), 1),
                Usage = VideoUsage.OptimalSpeed,
            };
            vpEnum = videoDevice.CreateVideoProcessorEnumerator(content);
            processor = videoDevice.CreateVideoProcessor(vpEnum, 0);
            // Moonlight's colours (Rec. 709, limited range, as asked of the host) to full-range RGB.
            var input = new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 };
            var output = new VideoProcessorColorSpace { RGB_Range = 0 };
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0, input);
            videoContext.VideoProcessorSetOutputColorSpace(processor, output);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamOutputRate(processor, 0, VideoProcessorOutputRate.Normal, false, null);
            using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
            outputView = videoDevice.CreateVideoProcessorOutputView(back, vpEnum,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            sizeChanged = false;
        }
        return true;
    }

    /// <summary>The swap chain let go (the stream ended), so the window can draw its own words again.</summary>
    public void ReleaseOutput()
    {
        lock (gate)
        {
            multithread.Enter();
            try
            {
                outputView?.Dispose();
                outputView = null;
                processor?.Dispose();
                processor = null;
                vpEnum?.Dispose();
                vpEnum = null;
                foreach (var v in inputViews) { v.Value.Dispose(); Marshal.Release(v.Key.Item1); }
                inputViews.Clear();
                swapChain?.Dispose();
                swapChain = null;
                context.ClearState();
                context.Flush();
            }
            finally
            {
                multithread.Leave();
            }
        }
    }

    /// <summary>The decoder let go; a new stream sets up a new one.</summary>
    public void StopDecoder()
    {
        lock (gate)
        {
            if (decoder == null) return;
            try { decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
            try { decoder.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch (SharpGenException) { }
            decoder.Dispose();
            decoder = null;
            frameIndex = 0;
        }
    }

    public void Dispose()
    {
        StopDecoder();
        ReleaseOutput();
        manager.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        multithread.Dispose();
        context.Dispose();
        device.Dispose();
    }
}

/// <summary>PC screens: frames, timings and drops of one stream, logged every 10 s.</summary>
public sealed class PcScreenStats
{
    /// <summary>Which PC screen, for the log ("pc screen 2").</summary>
    public static string Tag = "pc screen";

    public int Width, Height, Fps;
    public string Codec = "";
    public int Frames, Shown, Idr, Dropped;
    public long Bytes;
    private double decodeSum, decodeMax, queueSum, totalSum, totalMax;
    private long since = Stopwatch.GetTimestamp();

    public void Reset(int w, int h, int fps, string codec)
    {
        Width = w; Height = h; Fps = fps; Codec = codec;
        Frames = Shown = Idr = Dropped = 0;
        Bytes = 0;
        decodeSum = decodeMax = queueSum = totalSum = totalMax = 0;
        since = Stopwatch.GetTimestamp();
    }

    /// <summary>One frame: <paramref name="decodeMs"/> to decode and show, <paramref name="queueMs"/> from fully
    /// received to shown, <paramref name="totalMs"/> from its first packet to shown.</summary>
    public void Frame(bool shown, bool idr, int bytes, double decodeMs, double queueMs, double totalMs)
    {
        Frames++;
        if (shown) Shown++;
        if (idr) Idr++;
        Bytes += bytes;
        decodeSum += decodeMs;
        decodeMax = Math.Max(decodeMax, decodeMs);
        queueSum += queueMs;
        totalSum += totalMs;
        totalMax = Math.Max(totalMax, totalMs);
        double secs = (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
        if (secs >= 10) Flush(secs);
    }

    public string Line { get; private set; } = "";

    private void Flush(double secs)
    {
        int n = Math.Max(1, Frames);
        Line = $"{Codec} {Width}x{Height}@{Fps}: {Shown / secs:F1} shown/s ({Frames} frames, {Idr} key, {Dropped} dropped), " +
            $"{Bytes * 8 / secs / 1e6:F1} Mbps, decode+show {decodeSum / n:F1} ms (max {decodeMax:F1}), " +
            $"received-to-shown {queueSum / n:F1} ms, first-packet-to-shown {totalSum / n:F1} ms (max {totalMax:F1})";
        Log.P(Tag + ": " + Line);
        Frames = Shown = Idr = Dropped = 0;
        Bytes = 0;
        decodeSum = decodeMax = queueSum = totalSum = totalMax = 0;
        since = Stopwatch.GetTimestamp();
    }
}
