using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PocketXR.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;

namespace PocketXR.PcHost;

/// <summary>
/// The host's picture: one of this PC's displays, captured with Desktop Duplication, scaled on the graphics card to the
/// stream's size and turned to NV12 (Rec. 709, limited range, as the phone asks), and encoded to H.264 by the
/// graphics card's own encoder through Media Foundation (low latency, constant bitrate, no B-frames, a key frame only
/// when asked). Frames go out at the stream's rate; a still screen repeats its last picture, so the phone never
/// waits. All on one Direct3D device made for video.
/// </summary>
internal sealed unsafe class HostEncoder : IDisposable
{
    // Media Foundation's ids.
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Hevc = new("43564548-0000-0010-8000-00aa00389b71");
    private static readonly Guid Av1 = new("31305641-0000-0010-8000-00aa00389b71");
    private static readonly Guid ApiMaxBitrate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");

    /// <summary>Moonlight's bitstream formats, as the client names its choice in ANNOUNCE (bitStreamFormat).</summary>
    public const int CodecH264 = 0, CodecHevc = 1, CodecAv1 = 2;
    public static string CodecName(int c) => c == CodecHevc ? "HEVC" : c == CodecAv1 ? "AV1" : "H.264";
    private static Guid Subtype(int c) => c == CodecHevc ? Hevc : c == CodecAv1 ? Av1 : H264;

    /// <summary>Whether this PC's graphics card has a hardware encoder for the codec (NV12 in).</summary>
    public static bool Available(int codec)
    {
        MediaFactory.MFStartup(true).CheckError();
        var input = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = Nv12 };
        var output = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = Subtype(codec) };
        MediaFactory.MFTEnumEx(EncoderCategory, 0x4 | 0x40, input, output, out IntPtr list, out uint count);
        if (list != IntPtr.Zero)
        {
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(list, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(list);
        }
        return count > 0;
    }

    public readonly int Codec;
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid EncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    // ICodecAPI's settings.
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid ApiLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid ApiRateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid ApiMeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid ApiGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid ApiBFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ApiForceKey = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid ApiQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    private const int NeedInputEvent = 601, HaveOutputEvent = 602;
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    public readonly int Width, Height, Fps, BitrateKbps;
    private readonly string displayName;
    private readonly Action<byte[], bool, long> onFrame;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Multithread multithread;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly IMFDXGIDeviceManager manager;
    private IMFTransform? encoder;
    private IMFMediaEventGenerator? events;
    private IntPtr codecApi;
    private ID3D11Texture2D? latest, clean;
    private ID3D11RenderTargetView? latestTarget;
    private Render.PointerPainter? painter;
    private byte[] shapeBuffer = new byte[64 * 64 * 4];
    private bool pointerVisible;
    private int pointerX, pointerY;
    public long pointerShapes;
    private readonly ID3D11Texture2D[] nv12 = new ID3D11Texture2D[6];
    private int nv12Next;
    private ID3D11VideoProcessorEnumerator? vpEnum;
    private ID3D11VideoProcessor? processor;
    private ID3D11VideoProcessorInputView? inputView;
    private readonly ID3D11VideoProcessorOutputView?[] outputViews = new ID3D11VideoProcessorOutputView?[6];
    private int srcW, srcH;
    private Thread? captureThread, encodeThread;
    private volatile bool stopping;
    private volatile bool idrWanted = true;
    private long frameIndex;
    private bool providesSamples;
    private int outputBufferSize;
    public string EncoderName { get; private set; } = "";
    public string Error { get; private set; } = "";
    private long captured;
    /// <summary>For the log: NeedInput events, frames out, key frames, bytes, and submit-to-output time (ms, summed).</summary>
    public long NeedInputs, Outputs, Keys, OutBytes;
    public double EncodeMsSum, EncodeMsMax;
    private readonly long[] submitTicks = new long[64];

    /// <param name="displayName">Windows' name of the display to stream (\\.\DISPLAY2), or null for the main one.</param>
    /// <param name="onFrame">Each encoded frame: Annex-B bytes, whether it is a key frame, and when its picture was taken
    /// (Stopwatch ticks), on the encoder's thread.</param>
    public HostEncoder(string? displayName, int width, int height, int fps, int bitrateKbps, Action<byte[], bool, long> onFrame, int codec = CodecH264)
    {
        Codec = codec;
        this.displayName = displayName ?? "";
        Width = width & ~1;
        Height = height & ~1;
        Fps = Math.Clamp(fps, 10, 240);
        BitrateKbps = bitrateKbps;
        this.onFrame = onFrame;
        MediaFactory.MFStartup(true).CheckError();
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        device = dev;
        context = ctx;
        multithread = context.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context.QueryInterface<ID3D11VideoContext>();
        manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        for (int i = 0; i < nv12.Length; i++)
        {
            nv12[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                // The video encoder flag lets the encoder read it where it is, with no copy (D3D11_BIND_VIDEO_ENCODER).
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | (BindFlags)0x400,
            });
        }
    }

    public void Start()
    {
        MakeEncoder();
        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Host-capture", Priority = ThreadPriority.AboveNormal };
        encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "Host-encode", Priority = ThreadPriority.Highest };
        captureThread.Start();
        encodeThread.Start();
    }

    /// <summary>The next frame is a key frame (the phone asked, or it lost one).</summary>
    public void RequestIdr() => idrWanted = true;

    // ---- the encoder ----

    private void MakeEncoder()
    {
        var input = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = Nv12 };
        var output = new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = Subtype(Codec) };
        MediaFactory.MFTEnumEx(EncoderCategory, 0x4 | 0x40, input, output, out IntPtr list, out uint count);
        try
        {
            for (int i = 0; i < count && encoder == null; i++)
            {
                var activate = new IMFActivate(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                string name = "";
                try { name = activate.GetString(FriendlyName); } catch (SharpGenException) { }
                try
                {
                    var t = activate.ActivateObject<IMFTransform>();
                    if (Configure(t, name)) { encoder = t; EncoderName = name; }
                    else t.Dispose();
                }
                catch (SharpGenException e)
                {
                    Log.P($"host: encoder {name} refused: {e.Message}");
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
        if (encoder == null) throw new InvalidOperationException($"this PC's graphics card has no {CodecName(Codec)} encoder that took the stream");
        events = encoder.QueryInterface<IMFMediaEventGenerator>();
        encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Log.P($"host: encoder {EncoderName}, {CodecName(Codec)} {Width}x{Height}@{Fps}, up to {BitrateKbps / 1000} Mbps, " +
            $"{(providesSamples ? "its own output samples" : $"output buffers of {outputBufferSize} bytes")}");
    }

    private bool Configure(IMFTransform t, string name)
    {
        t.Attributes.Set(AsyncUnlock, 1u);
        t.Attributes.Set(ApiLowLatency, 1u); // MF_LOW_LATENCY, the same id
        t.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)manager.NativePointer);
        // The encoder's settings first: some encoders take rate control and low latency only before the media types.
        codecApi = QueryInterface(t.NativePointer, IidCodecApi);
        // PXR_ENC (tests only): words that switch settings off: lowlat, rc, gop, bframes, speed.
        string skip = Environment.GetEnvironmentVariable("PXR_ENC") ?? "";
        if (codecApi != IntPtr.Zero)
        {
            if (!skip.Contains("lowlat")) SetBool(ApiLowLatency, true);
            // Peak-limited variable bitrate: a still or quiet screen sends small frames. Constant bitrate filled every
            // frame to the full rate (65 KB a frame at 64 Mbps even with nothing moving), so the Wi-Fi carried ~80 Mbps all
            // the time and the phone's round trip and receive times grew (Mike's phone, 20 Nov 2026).
            if (!skip.Contains("rc"))
            {
                SetUInt(ApiRateControl, 1); // eAVEncCommonRateControlMode_PeakConstrainedVBR
                SetUInt(ApiMeanBitrate, (uint)(BitrateKbps * 1000 * 3 / 4));
                SetUInt(ApiMaxBitrate, (uint)(BitrateKbps * 1000));
            }
            if (!skip.Contains("bframes")) SetUInt(ApiBFrames, 0);
            if (!skip.Contains("gop")) SetUInt(ApiGopSize, 0xFFFFFFFF); // infinite: key frames only when asked
            if (!skip.Contains("speed")) SetUInt(ApiQualityVsSpeed, 0); // speed
        }
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MajorType, VideoMajor);
            outType.Set(SubType, Subtype(Codec));
            outType.Set(AvgBitrate, (uint)(BitrateKbps * 1000));
            outType.Set(FrameSize, ((ulong)Width << 32) | (uint)Height);
            outType.Set(FrameRate, ((ulong)Fps << 32) | 1u);
            outType.Set(PixelAspect, (1UL << 32) | 1u);
            outType.Set(InterlaceMode, 2u);
            // H.264 High (Moonlight's VIDEO_FORMAT_H264), HEVC Main; AV1 left to the encoder (Main).
            if (Codec == CodecH264) outType.Set(Profile, 100u);
            else if (Codec == CodecHevc) outType.Set(Profile, 1u);
            outType.Set(YuvMatrix, 1u); // BT.709
            outType.Set(NominalRange, 2u); // 16-235
            t.SetOutputType(0, outType, 0);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MajorType, VideoMajor);
            inType.Set(SubType, Nv12);
            inType.Set(FrameSize, ((ulong)Width << 32) | (uint)Height);
            inType.Set(FrameRate, ((ulong)Fps << 32) | 1u);
            inType.Set(PixelAspect, (1UL << 32) | 1u);
            inType.Set(InterlaceMode, 2u);
            t.SetInputType(0, inType, 0);
        }
        var info = t.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & 0x100) != 0;
        outputBufferSize = Math.Max((int)info.Size, Width * Height);
        return true;
    }

    private void EncodeLoop()
    {
        timeBeginPeriod(1);
        long period = Stopwatch.Frequency / Fps;
        long next = Stopwatch.GetTimestamp();
        try
        {
            while (!stopping)
            {
                IMFMediaEvent ev;
                try { ev = events!.GetEvent(0); }
                catch (SharpGenException) { if (stopping) return; throw; }
                using (ev)
                {
                    int type = (int)ev.EventType;
                    if (type == NeedInputEvent)
                    {
                        NeedInputs++;
                        // Frames at the stream's rate: wait for this frame's time, then send the newest picture.
                        long now = Stopwatch.GetTimestamp();
                        if (next > now)
                        {
                            int ms = (int)((next - now) * 1000 / Stopwatch.Frequency);
                            if (ms > 0) Thread.Sleep(ms);
                            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
                        }
                        next = Math.Max(next + period, Stopwatch.GetTimestamp() - period);
                        Submit();
                    }
                    else if (type == HaveOutputEvent)
                    {
                        TakeOutput();
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!stopping)
            {
                Error = e.Message;
                Log.P($"host: encoder stopped: {e}");
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void Submit()
    {
        ID3D11Texture2D target = nv12[nv12Next];
        int slot = nv12Next;
        nv12Next = (nv12Next + 1) % nv12.Length;
        multithread.Enter();
        try
        {
            if (latest != null && EnsureProcessor(slot))
            {
                var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
                videoContext.VideoProcessorBlt(processor!, outputViews[slot]!, 0, 1, streams);
                // Sent to the graphics card now: the encoder reads the picture from its own queue and otherwise waited
                // about 100 ms for Direct3D to flush by itself (5 frames a second, 20 Nov 2026).
                context.Flush();
            }
        }
        finally
        {
            multithread.Leave();
        }
        if (idrWanted && codecApi != IntPtr.Zero)
        {
            idrWanted = false;
            SetUInt(ApiForceKey, 1);
        }
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, target, 0, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        long ticks = 10_000_000L / Fps;
        sample.SampleTime = frameIndex * ticks;
        sample.SampleDuration = ticks;
        submitTicks[frameIndex % submitTicks.Length] = Stopwatch.GetTimestamp();
        frameIndex++;
        encoder!.ProcessInput(0, sample, 0);
    }

    private void TakeOutput()
    {
        IMFSample? own = null;
        if (!providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(outputBufferSize));
        }
        var output = new OutputDataBuffer { StreamID = 0, Sample = own! };
        Result r = encoder!.ProcessOutput(0, 1, ref output, out _);
        if (r.Code == StreamChange)
        {
            using var t = encoder.GetOutputAvailableType(0, 0);
            encoder.SetOutputType(0, t, 0);
            output.Sample?.Dispose();
            return;
        }
        if (r.Failure)
        {
            output.Sample?.Dispose();
            if (r.Code != NeedMoreInput && !stopping) Log.P($"host: encoder output: {r}");
            return;
        }
        using var s = output.Sample;
        output.Events?.Dispose();
        if (s == null) return;
        bool key = false;
        try { key = s.GetUInt32(CleanPoint) != 0; } catch (SharpGenException) { }
        long taken = submitTicks[(int)((s.SampleTime / (10_000_000L / Fps)) % submitTicks.Length)];
        using var mb = s.ConvertToContiguousBuffer();
        mb.Lock(out IntPtr p, out _, out int len);
        byte[] bytes = new byte[len];
        Marshal.Copy(p, bytes, 0, len);
        mb.Unlock();
        if (!key) key = Codec == CodecAv1 ? Av1HasSequenceHeader(bytes) : HasIdr(bytes, Codec == CodecHevc);
        double ms = (Stopwatch.GetTimestamp() - taken) * 1000.0 / Stopwatch.Frequency;
        Outputs++;
        if (key) Keys++;
        OutBytes += bytes.Length;
        EncodeMsSum += ms;
        EncodeMsMax = Math.Max(EncodeMsMax, ms);
        onFrame(bytes, key, taken);
    }

    /// <summary>Whether an Annex-B H.264 frame holds an IDR slice (NAL type 5).</summary>
    /// <summary>Whether an AV1 frame (low-overhead OBUs) starts with a sequence header (OBU type 1): a key frame.</summary>
    public static bool Av1HasSequenceHeader(ReadOnlySpan<byte> b)
    {
        int i = 0;
        for (int n = 0; n < 8 && i < b.Length; n++)
        {
            byte h = b[i];
            int type = (h >> 3) & 0xF;
            if (type == 1) return true;
            bool ext = (h & 0x4) != 0, hasSize = (h & 0x2) != 0;
            i += 1 + (ext ? 1 : 0);
            if (!hasSize) return false;
            long size = 0;
            for (int s = 0; s < 8 && i < b.Length; s++)
            {
                byte v = b[i++];
                size |= (long)(v & 0x7F) << (7 * s);
                if ((v & 0x80) == 0) break;
            }
            i += (int)size;
        }
        return false;
    }

    public static bool HasIdr(ReadOnlySpan<byte> b, bool hevc = false)
    {
        for (int i = 0; i + 3 < b.Length; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1) continue;
            if (!hevc && (b[i + 3] & 0x1F) == 5) return true;
            if (hevc && ((b[i + 3] >> 1) & 0x3F) is 19 or 20 or 21) return true;
        }
        return false;
    }

    private bool EnsureProcessor(int slot)
    {
        if (processor == null)
        {
            var content = new VideoProcessorContentDescription
            {
                InputWidth = (uint)srcW,
                InputHeight = (uint)srcH,
                OutputWidth = (uint)Width,
                OutputHeight = (uint)Height,
                InputFrameRate = new Rational((uint)Fps, 1),
                OutputFrameRate = new Rational((uint)Fps, 1),
                Usage = VideoUsage.OptimalSpeed,
            };
            vpEnum = videoDevice.CreateVideoProcessorEnumerator(content);
            processor = videoDevice.CreateVideoProcessor(vpEnum, 0);
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
            videoContext.VideoProcessorSetOutputColorSpace(processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamOutputRate(processor, 0, VideoProcessorOutputRate.Normal, false, null);
            inputView = videoDevice.CreateVideoProcessorInputView(latest!, vpEnum, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        }
        outputViews[slot] ??= videoDevice.CreateVideoProcessorOutputView(nv12[slot], vpEnum!,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        return true;
    }

    private void DropProcessor()
    {
        for (int i = 0; i < outputViews.Length; i++) { outputViews[i]?.Dispose(); outputViews[i] = null; }
        inputView?.Dispose();
        inputView = null;
        processor?.Dispose();
        processor = null;
        vpEnum?.Dispose();
        vpEnum = null;
    }

    // ---- capture ----

    private void CaptureLoop()
    {
        timeBeginPeriod(1);
        while (!stopping)
        {
            IDXGIOutputDuplication? dup = null;
            try
            {
                dup = Duplicate(out int w, out int h);
                multithread.Enter();
                try
                {
                    if (latest == null || w != srcW || h != srcH)
                    {
                        DropProcessor();
                        latestTarget?.Dispose();
                        latest?.Dispose();
                        clean?.Dispose();
                        srcW = w;
                        srcH = h;
                        var desc = new Texture2DDescription
                        {
                            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        };
                        latest = device.CreateTexture2D(desc);
                        clean = device.CreateTexture2D(desc);
                        latestTarget = device.CreateRenderTargetView(latest);
                        painter ??= new Render.PointerPainter(device);
                        Log.P($"host: capturing {(displayName.Length > 0 ? displayName : "the main display")} at {w}x{h}");
                    }
                }
                finally
                {
                    multithread.Leave();
                }
                while (!stopping)
                {
                    // No waiting inside Desktop Duplication: its wait holds the Direct3D device, and the encoder, on the
                    // same device, waited behind it (5 frames a second, 20 Nov 2026). Asked every 2 ms instead.
                    Result r = dup.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource? res);
                    if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                    if (r.Failure) { Log.P($"host: capture: {r}"); break; }
                    try
                    {
                        // Desktop Duplication leaves the pointer out of the picture (its shape and place come apart),
                        // and the phone shows the PC's pointer only from the picture: it is drawn in here, as Apollo
                        // and Sunshine do. The picture without it is kept ("clean") so a moving pointer is redrawn.
                        bool changed = false;
                        if ((info.LastPresentTime != 0 || captured == 0) && res != null) // the first picture always
                        {
                            using var tex = res.QueryInterface<ID3D11Texture2D>();
                            multithread.Enter();
                            try { context.CopyResource(clean!, tex); }
                            finally { multithread.Leave(); }
                            captured++;
                            changed = true;
                        }
                        if (info.PointerShapeBufferSize > 0)
                        {
                            if (shapeBuffer.Length < info.PointerShapeBufferSize) shapeBuffer = new byte[info.PointerShapeBufferSize];
                            OutduplPointerShapeInfo shape;
                            Result got;
                            fixed (byte* p = shapeBuffer)
                                got = dup.GetFramePointerShape((uint)shapeBuffer.Length, (IntPtr)p, out _, out shape);
                            if (got.Success)
                            {
                                var picture = Render.PointerImage.From((int)shape.Type, (int)shape.Width, (int)shape.Height,
                                    (int)shape.Pitch, shapeBuffer.AsSpan(0, (int)info.PointerShapeBufferSize));
                                multithread.Enter();
                                try { painter!.SetShape(picture); }
                                finally { multithread.Leave(); }
                                pointerShapes++;
                                changed = true;
                            }
                        }
                        if (info.LastMouseUpdateTime != 0)
                        {
                            // The position is the shape's top left (the hot spot already taken off).
                            pointerVisible = info.PointerPosition.Visible;
                            pointerX = info.PointerPosition.Position.X;
                            pointerY = info.PointerPosition.Position.Y;
                            changed = true;
                        }
                        if (changed)
                        {
                            multithread.Enter();
                            try
                            {
                                context.CopyResource(latest!, clean!);
                                if (pointerVisible && painter!.HasShape) painter.Draw(context, latestTarget!, srcW, srcH, pointerX, pointerY);
                            }
                            finally { multithread.Leave(); }
                        }
                    }
                    finally
                    {
                        res?.Dispose();
                        dup.ReleaseFrame();
                    }
                }
            }
            catch (Exception e) when (!stopping)
            {
                Log.P($"host: capture: {e.Message}");
                Thread.Sleep(500);
            }
            finally
            {
                dup?.Dispose();
            }
        }
    }

    private IDXGIOutputDuplication Duplicate(out int w, out int h)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        IDXGIOutput? chosen = null;
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? o).Success && o != null; i++)
        {
            var d = o.Description;
            bool want = displayName.Length > 0 ? string.Equals(d.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)
                : d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0;
            if (want && chosen == null) chosen = o; else o.Dispose();
        }
        if (chosen == null) throw new InvalidOperationException($"no display {(displayName.Length > 0 ? displayName : "at 0,0")} on this graphics card");
        using (chosen)
        {
            var rect = chosen.Description.DesktopCoordinates;
            w = rect.Right - rect.Left;
            h = rect.Bottom - rect.Top;
            // The phone's mouse lands in this display's area of the desktop.
            HostInput.Area = new HostInput.Rect(rect.Left, rect.Top, w, h);
            using var o1 = chosen.QueryInterface<IDXGIOutput1>();
            return o1.DuplicateOutput(device);
        }
    }

    public long Captured => captured;

    // ---- ICodecAPI by hand (Vortice has no wrapper) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant { public ushort Vt; public ushort R1, R2, R3; public ulong Value; public ulong Pad; }

    private static IntPtr QueryInterface(IntPtr unk, Guid iid)
    {
        return Marshal.QueryInterface(unk, in iid, out IntPtr p) == 0 ? p : IntPtr.Zero;
    }

    private int SetValue(Guid key, Variant v)
    {
        var vtbl = *(IntPtr**)codecApi;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        return fn(codecApi, &key, &v);
    }

    private void SetUInt(Guid key, uint value)
    {
        int hr = SetValue(key, new Variant { Vt = 19, Value = value });
        if (hr < 0) Log.P($"host: encoder setting {key} = {value} refused (0x{hr:x8})");
    }

    private void SetBool(Guid key, bool value)
    {
        int hr = SetValue(key, new Variant { Vt = 11, Value = value ? 0xFFFFu : 0u });
        if (hr < 0) Log.P($"host: encoder setting {key} = {value} refused (0x{hr:x8})");
    }

    public void Dispose()
    {
        stopping = true;
        try { if (encoder != null) MediaFactory.MFShutdownObject(encoder); } catch (Exception) { }
        captureThread?.Join(1000);
        encodeThread?.Join(1000);
        if (codecApi != IntPtr.Zero) { Marshal.Release(codecApi); codecApi = IntPtr.Zero; }
        events?.Dispose();
        encoder?.Dispose();
        DropProcessor();
        latestTarget?.Dispose();
        painter?.Dispose();
        latest?.Dispose();
        clean?.Dispose();
        foreach (var t in nv12) t?.Dispose();
        manager.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        multithread.Dispose();
        context.Dispose();
        device.Dispose();
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
