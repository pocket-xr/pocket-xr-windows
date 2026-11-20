using System;
using System.Runtime.InteropServices;

namespace PocketXR.PcHost;

/// <summary>
/// The host's native pieces, in moonlight-pxr.dll (native/moonlight/pxr_host.c): moonlight-common-c's ENet fork for the
/// control stream's server, and nanors' Reed-Solomon for the video stream's error correction: the same code the client
/// speaks, as Sunshine builds them for its server.
/// </summary>
internal static unsafe class HostNative
{
    private const string Dll = "moonlight-pxr.dll";

    public const int EventNone = 0, EventConnect = 1, EventDisconnect = 2, EventReceive = 3;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr PxrHostEnetStart(int port, int maxPeers);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void PxrHostEnetStop(IntPtr host);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PxrHostEnetService(IntPtr host, int timeoutMs, out IntPtr peer, out uint connectData, byte* buf, int cap, out int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrHostEnetSend(IntPtr host, IntPtr peer, byte* data, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void PxrHostEnetDisconnect(IntPtr peer);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrHostAudioFecEncode(byte* shards, int blockSize);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int PxrHostFecEncode(byte* shards, int dataShards, int parityShards, int blockSize);
}
