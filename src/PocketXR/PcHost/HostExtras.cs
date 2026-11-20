using System;
using System.Runtime.InteropServices;
using System.Threading;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>This PC's clipboard as text, for Apollo's clipboard requests (the phone shares its clipboard both ways).</summary>
internal static class HostClipboard
{
    private const uint UnicodeText = 13;

    public static string? Get()
    {
        if (!Open()) return null;
        try
        {
            IntPtr h = GetClipboardData(UnicodeText);
            if (h == IntPtr.Zero) return "";
            IntPtr p = GlobalLock(h);
            try { return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static bool Set(string text)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr h = GlobalAlloc(0x0002, (UIntPtr)bytes); // GMEM_MOVEABLE
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(UnicodeText, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Another program may hold the clipboard for a moment: tries for a second (in the app, 200 ms once fell short, 20 Nov 2026).</summary>
    private static bool Open()
    {
        for (int i = 0; i < 25; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(40);
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
}

/// <summary>
/// The host on the network as Moonlight finds hosts: an mDNS service "_nvstream._tcp" on the HTTP port, through Windows'
/// own DNS-SD (DnsServiceRegister, Windows 10 1809 and later), so the phone lists it among the PCs it finds.
/// </summary>
internal sealed unsafe class HostMdns : IDisposable
{
    private IntPtr instance;
    private readonly ManualResetEventSlim registered = new(false);
    private static HostMdns? current;
    private bool done;

    public void Register(string name, int port)
    {
        current = this;
        string host = Environment.MachineName + ".local";
        instance = DnsServiceConstructInstance($"{name}._nvstream._tcp.local", host, IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0, 0, null, null);
        if (instance == IntPtr.Zero)
        {
            Log.P("host: mDNS: could not describe the service");
            return;
        }
        var req = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = 1, // DNS_QUERY_REQUEST_VERSION1
            InterfaceIndex = 0,
            pServiceInstance = instance,
            pRegisterCompletionCallback = &Completed,
            pQueryContext = IntPtr.Zero,
            hCredentials = IntPtr.Zero,
            unicastEnabled = 0,
        };
        uint r = DnsServiceRegister(&req, IntPtr.Zero);
        if (r != 9506) // DNS_REQUEST_PENDING
        {
            Log.P($"host: mDNS: not announced ({r})");
            return;
        }
        if (registered.Wait(3000)) Log.P($"host: mDNS: announced as \"{name}\" (_nvstream._tcp, port {port})");
        else Log.P("host: mDNS: no answer from Windows within 3 s");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void Completed(uint status, IntPtr context, IntPtr inst)
    {
        if (status != 0) Log.P($"host: mDNS: registration status {status}");
        // The instance given back is the one registered, freed once in Dispose.
        if (current != null) current.registered.Set();
    }

    public void Dispose()
    {
        if (done || instance == IntPtr.Zero) return;
        done = true;
        var req = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = 1,
            pServiceInstance = instance,
            pRegisterCompletionCallback = &Completed,
        };
        registered.Reset();
        if (DnsServiceDeRegister(&req, IntPtr.Zero) == 9506) registered.Wait(1000);
        DnsServiceFreeInstance(instance);
        instance = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public delegate* unmanaged[Stdcall]<uint, IntPtr, IntPtr, void> pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6, ushort port,
        ushort priority, ushort weight, uint propertiesCount, string[]? keys, string[]? values);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(DNS_SERVICE_REGISTER_REQUEST* request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(DNS_SERVICE_REGISTER_REQUEST* request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
}
