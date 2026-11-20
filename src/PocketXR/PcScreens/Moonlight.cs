using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: moonlight-common-c (Moonlight's stream core, GPLv3), built with Pocket XR's glue into moonlight-pxr.dll
/// (native/moonlight). One stream per process: the core keeps its connection in globals, so each PC screen runs in its
/// own PocketXR.exe --pc-screen process, and this class is static.
/// </summary>
public static unsafe class Moonlight
{
    private const string Dll = "moonlight-pxr.dll";

    public const int VideoH264 = 0x0001, VideoH265 = 0x0100, VideoAv1 = 0x1000;
    public const int AudioStereo = (0x3 << 16) | (2 << 8) | 0xCA;
    public const int StreamLocal = 0, StreamRemote = 1, StreamAuto = 2;
    public const int EncryptAll = unchecked((int)0xFFFFFFFF);
    public const int ColourRec709 = 1, RangeLimited = 0;
    public const int DrOk = 0, DrNeedIdr = -1;
    public const int FrameIdr = 1;
    public const int ButtonLeft = 1, ButtonMiddle = 2, ButtonRight = 3, ButtonX1 = 4, ButtonX2 = 5;
    public const int ModShift = 0x01, ModCtrl = 0x02, ModAlt = 0x04, ModMeta = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public delegate* unmanaged[Cdecl]<int, int, int, void> Stage;
        public delegate* unmanaged[Cdecl]<void> Started;
        public delegate* unmanaged[Cdecl]<int, void> Terminated;
        public delegate* unmanaged[Cdecl]<byte*, void> Log;
        public delegate* unmanaged[Cdecl]<int, void> Status;
        public delegate* unmanaged[Cdecl]<int, int, int, int, int> VideoSetup;
        public delegate* unmanaged[Cdecl]<byte*, int, int, int, ulong, ulong, int, int> VideoFrame;
        public delegate* unmanaged[Cdecl]<int, int, int, int, int, byte*, int> AudioInit;
        public delegate* unmanaged[Cdecl]<byte*, int, void> AudioSample;
    }

    /// <summary>What the stream tells the PC screen. Called on the core's own threads.</summary>
    public interface IListener
    {
        void Stage(int kind, string name, int error);
        void Started();
        void Terminated(int error);
        void Status(int status);
        int VideoSetup(int format, int width, int height, int fps);
        int VideoFrame(ReadOnlySpan<byte> data, bool idr, int frameNumber, ulong receiveUs, ulong enqueueUs, int hostLatencyTenthsMs);
        int AudioInit(int sampleRate, int channels, int streams, int coupled, int samplesPerFrame, ReadOnlySpan<byte> mapping);
        void AudioSample(ReadOnlySpan<byte> opus);
    }

    private static IListener? listener;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PxrStart(byte* address, byte* appVersion, byte* gfeVersion, byte* rtspSessionUrl,
        int serverCodecModeSupport, int width, int height, int fps, int bitrateKbps, int packetSize, int streamingRemotely,
        int audioConfiguration, int supportedVideoFormats, int clientRefreshRateX100, byte* riKey16, int riKeyId,
        int encryptionFlags, int colorSpace, int colorRange, int videoCapabilities, Callbacks* callbacks);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void PxrStop();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void PxrInterrupt();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern byte* PxrLaunchQuery();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern byte* PxrStageName(int stage);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong PxrNowUs();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void PxrRequestIdr();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrPendingVideoFrames();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrRtt(out uint rttMs, out uint varianceMs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrMousePosition(short x, short y, short refW, short refH);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrMouseMove(short dx, short dy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrMouseButton(int press, int button);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrScroll(short amount);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrHScroll(short amount);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrKey(short keyCode, int down, int modifiers);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int PxrText(byte* utf8, uint length);

    /// <summary>"&amp;corever=1": the core's own launch query, added to the host's launch URL.</summary>
    public static string LaunchQuery => Marshal.PtrToStringUTF8((nint)PxrLaunchQuery()) ?? "";

    public static string StageName(int stage) => Marshal.PtrToStringUTF8((nint)PxrStageName(stage)) ?? stage.ToString();

    public static void Text(string text)
    {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(text);
        fixed (byte* p = b) PxrText(p, (uint)b.Length);
    }

    /// <summary>Starts the stream; blocks until it is up (0) or failed (the core's error).</summary>
    public static int Start(IListener l, string address, string appVersion, string? gfeVersion, string? rtspSessionUrl,
        int serverCodecModeSupport, int width, int height, int fps, int bitrateKbps, int packetSize, int streamingRemotely,
        int supportedVideoFormats, byte[] riKey, int riKeyId)
    {
        listener = l;
        var cb = new Callbacks
        {
            Stage = &OnStage,
            Started = &OnStarted,
            Terminated = &OnTerminated,
            Log = &OnLog,
            Status = &OnStatus,
            VideoSetup = &OnVideoSetup,
            VideoFrame = &OnVideoFrame,
            AudioInit = &OnAudioInit,
            AudioSample = &OnAudioSample,
        };
        byte[] a = Z(address), v = Z(appVersion), g = Z(gfeVersion), r = Z(rtspSessionUrl);
        fixed (byte* pa = a, pv = v, pg = g, pr = r, pk = riKey)
        {
            return PxrStart(pa, pv, gfeVersion == null ? null : pg, rtspSessionUrl == null ? null : pr, serverCodecModeSupport,
                width, height, fps, bitrateKbps, packetSize, streamingRemotely, AudioStereo, supportedVideoFormats, fps * 100,
                pk, riKeyId, EncryptAll, ColourRec709, RangeLimited, 0, &cb);
        }
    }

    private static byte[] Z(string? s) => System.Text.Encoding.UTF8.GetBytes((s ?? "") + "\0");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStage(int kind, int stage, int error)
    {
        try { listener?.Stage(kind, StageName(stage), error); } catch (Exception e) { Log.P($"pc screen: stage: {e}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStarted()
    {
        try { listener?.Started(); } catch (Exception e) { Log.P($"pc screen: started: {e}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnTerminated(int error)
    {
        try { listener?.Terminated(error); } catch (Exception e) { Log.P($"pc screen: terminated: {e}"); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnLog(byte* message)
    {
        Log.P("moonlight: " + Marshal.PtrToStringUTF8((nint)message));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStatus(int status)
    {
        try { listener?.Status(status); } catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnVideoSetup(int format, int w, int h, int fps)
    {
        try { return listener?.VideoSetup(format, w, h, fps) ?? -1; }
        catch (Exception e) { Log.P($"pc screen: video setup: {e}"); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnVideoFrame(byte* data, int length, int frameType, int frameNumber, ulong receiveUs, ulong enqueueUs, int hostLatency)
    {
        try
        {
            return listener?.VideoFrame(new ReadOnlySpan<byte>(data, length), frameType == FrameIdr, frameNumber, receiveUs, enqueueUs, hostLatency) ?? DrOk;
        }
        catch (Exception e)
        {
            Log.P($"pc screen: video frame: {e}");
            return DrNeedIdr;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnAudioInit(int rate, int channels, int streams, int coupled, int samplesPerFrame, byte* mapping)
    {
        try { return listener?.AudioInit(rate, channels, streams, coupled, samplesPerFrame, new ReadOnlySpan<byte>(mapping, 8)) ?? -1; }
        catch (Exception e) { Log.P($"pc screen: audio init: {e}"); return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAudioSample(byte* data, int length)
    {
        try { listener?.AudioSample(new ReadOnlySpan<byte>(data, length)); } catch (Exception) { }
    }
}
