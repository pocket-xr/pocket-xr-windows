using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// The host's video stream (Sunshine's stream.cpp videoBroadcastThread, GPLv3): each encoded frame behind an 8-byte
/// short header, cut into packets of the client's packet size, each with a 12-byte RTP header (and 4 reserved bytes)
/// and a 16-byte NV_VIDEO_PACKET header, in up to four FEC blocks with Reed-Solomon parity (20 %, at least the
/// client's minimum). Sent to the address the client's pings come from. Unencrypted (the host offers no video
/// encryption).
/// </summary>
internal sealed unsafe class HostVideo : IDisposable
{
    private const int RtpHeader = 16; // 12 bytes and 4 reserved
    private const int HeaderSize = RtpHeader + 16;
    private const int MaxFecBlocks = 4;

    private readonly Socket socket;
    private readonly int packetSize, fecPercent, minParity;
    private IPEndPoint? client;
    private readonly Thread pings;
    private volatile bool stopping;
    /// <summary>The packets sent so far: the RTP sequence is its low 16 bits, the stream packet index its low 24. The
    /// index must never go back: a 16-bit count wrapped after about 20 s and the client then called every frame corrupt
    /// (20 Nov 2026).</summary>
    private uint lowSeq;
    private uint frameIndex;
    private readonly long epoch = Stopwatch.GetTimestamp();
    public long FramesSent, PacketsSent, BytesSent;

    public HostVideo(int port, int packetSize, int fecPercent, int minParity)
    {
        this.packetSize = packetSize;
        this.fecPercent = fecPercent;
        this.minParity = minParity;
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SendBufferSize = 4 << 20;
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        // Windows reports an ICMP "port unreachable" as an error on the next receive: switched off.
        socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
        pings = new Thread(Pings) { IsBackground = true, Name = "Host-video-ping" };
        pings.Start();
    }

    public bool HasClient => client != null;

    /// <summary>The client's first ping: the stream's first key frame goes out now, so the phone does not wait for one.</summary>
    public Action? FirstClient;

    private void Pings()
    {
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!stopping)
        {
            try
            {
                int n = socket.ReceiveFrom(buf, ref from);
                bool first = client == null;
                client = (IPEndPoint)from;
                if (n >= 4 && first)
                {
                    Log.P($"host: video client at {from}");
                    FirstClient?.Invoke();
                }
            }
            catch (SocketException)
            {
                // Closing the socket interrupts the wait (WSAEINTR): the end, not an error.
                if (stopping) return;
            }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>One encoded frame out. <paramref name="latencyTenthsMs"/>: from capture to sending, 0.1 ms units.</summary>
    public void Send(ReadOnlySpan<byte> frame, bool idr, int latencyTenthsMs)
    {
        var to = client;
        if (to == null) return;
        frameIndex++;
        int blockSize = packetSize + RtpHeader;
        int payloadPerPacket = blockSize - HeaderSize;

        // The short frame header, then the frame, cut into packets each behind a 32-byte header.
        int dataLen = 8 + frame.Length;
        int packets = (dataLen + payloadPerPacket - 1) / payloadPerPacket;
        int lastLen = dataLen % (packetSize - 16);
        if (lastLen == 0) lastLen = packetSize - 16;
        byte[] payload = new byte[packets * HeaderSize + dataLen];
        Span<byte> head = stackalloc byte[8];
        head[0] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(head[1..], (ushort)Math.Clamp(latencyTenthsMs, 0, 65535));
        head[3] = (byte)(idr ? 2 : 1);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], (ushort)lastLen);
        int src = 0;
        for (int p = 0; p < packets; p++)
        {
            int at = p * (HeaderSize + payloadPerPacket) + HeaderSize;
            int take = Math.Min(payloadPerPacket, dataLen - src);
            for (int i = 0; i < take; i++, src++)
                payload[at + i] = src < 8 ? head[src] : frame[src - 8];
        }

        int fec = fecPercent;
        int maxDataShards = 255 * 100 / (100 + fec);
        int maxPerBlock = maxDataShards * blockSize;
        int blocks = (payload.Length + maxPerBlock - 1) / maxPerBlock;
        if (blocks > MaxFecBlocks)
        {
            fec = 0;
            blocks = MaxFecBlocks;
        }
        int unaligned = payload.Length / blocks;
        int aligned = (unaligned + blockSize - 1) / blockSize * blockSize;
        uint timestamp = (uint)((Stopwatch.GetTimestamp() - epoch) * 90000 / Stopwatch.Frequency);
        for (int b = 0; b < blocks; b++)
        {
            int start = b * aligned;
            int len = b == blocks - 1 ? payload.Length - start : Math.Min(aligned, payload.Length - start);
            if (len <= 0) break;
            SendBlock(to, payload.AsSpan(start, len), b, blocks, fec, timestamp);
        }
        FramesSent++;
    }

    private void SendBlock(IPEndPoint to, ReadOnlySpan<byte> block, int blockIndex, int blocks, int fec, uint timestamp)
    {
        int blockSize = packetSize + RtpHeader;
        int dataShards = (block.Length + blockSize - 1) / blockSize;
        int parity = (dataShards * fec + 99) / 100;
        if (parity < minParity && fec != 0)
        {
            parity = minParity;
            fec = 100 * parity / dataShards;
        }
        if (dataShards + parity > 255) parity = 255 - dataShards;
        int total = dataShards + parity;
        byte[] shards = new byte[total * blockSize];
        block.CopyTo(shards);
        byte multiFecBlocks = (byte)((blockIndex << 4) | ((blocks - 1) << 6));
        for (int x = 0; x < dataShards; x++)
        {
            var h = shards.AsSpan(x * blockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (lowSeq + (uint)x) << 8);
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], frameIndex);
            byte flags = 0x1; // picture data
            if (x == 0) flags |= 0x4; // start of frame
            if (x == dataShards - 1) flags |= 0x2; // end of frame
            h[24] = flags;
            h[26] = 0x10;
            h[27] = multiFecBlocks;
        }
        if (fec != 0 && parity > 0)
        {
            fixed (byte* p = shards)
            {
                if (HostNative.PxrHostFecEncode(p, dataShards, parity, blockSize) != 0) Log.P("host: FEC encode failed");
            }
        }
        for (int x = 0; x < total; x++)
        {
            var h = shards.AsSpan(x * blockSize, blockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(x << 12 | dataShards << 22 | fec << 4));
            h[0] = 0x90; // RTP version 2 with the extension flag
            h[1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(h[2..], (ushort)(lowSeq + x));
            BinaryPrimitives.WriteUInt32BigEndian(h[4..], timestamp);
            h[27] = multiFecBlocks;
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], frameIndex);
            try
            {
                socket.SendTo(h, SocketFlags.None, to);
                PacketsSent++;
                BytesSent += blockSize;
            }
            catch (SocketException e)
            {
                Log.P($"host: video send: {e.SocketErrorCode}");
                break;
            }
        }
        lowSeq += (uint)total;
    }

    public void Dispose()
    {
        stopping = true;
        try { socket.Dispose(); } catch (Exception) { }
    }
}

/// <summary>
/// The host's control stream (Sunshine's control_server_t): an ENet server on the control port. Every message is
/// AES-GCM with the launch's key (CONTROL_V2: a 12-byte nonce of the sequence number, then 'C','C' from the client or
/// 'H','C' from the host). Handled: key frame requests, loss stats (as a key frame request) and input (logged in this
/// milestone). The ENet calls all run on this class's own thread.
/// </summary>
internal sealed unsafe class HostControl : IDisposable
{
    public const ushort TypeIdr = 0x0302, TypeInvalidate = 0x0301, TypeLossStats = 0x0201, TypeInput = 0x0206,
        TypeStartA = 0x0305, TypeStartB = 0x0307, TypeTermination = 0x0109, TypePeriodicPing = 0x0200;

    private readonly IntPtr host;
    private readonly AesGcm gcm;
    private readonly Thread thread;
    private volatile bool stopping;
    private IntPtr peer;
    private uint seq;
    public Action? KeyFrameWanted;
    public Action<ReadOnlyMemory<byte>>? Input;
    public Action? Gone;
    public volatile bool Connected;
    private int inputs;

    public HostControl(int port, byte[] key)
    {
        host = HostNative.PxrHostEnetStart(port, 4);
        if (host == IntPtr.Zero) throw new InvalidOperationException($"the control port {port} could not be opened");
        gcm = new AesGcm(key, 16);
        thread = new Thread(Run) { IsBackground = true, Name = "Host-control" };
        thread.Start();
    }

    private void Run()
    {
        byte[] buf = new byte[65536];
        while (!stopping)
        {
            int kind;
            IntPtr p;
            int len;
            fixed (byte* b = buf) kind = HostNative.PxrHostEnetService(host, 50, out p, out uint data, b, buf.Length, out len);
            if (kind == HostNative.EventConnect)
            {
                peer = p;
                Connected = true;
                Log.P("host: control stream connected");
            }
            else if (kind == HostNative.EventDisconnect)
            {
                Log.P("host: control stream disconnected");
                Connected = false;
                peer = IntPtr.Zero;
                Gone?.Invoke();
            }
            else if (kind == HostNative.EventReceive)
            {
                Received(buf.AsSpan(0, Math.Min(len, buf.Length)));
            }
            if (pendingTermination && peer != IntPtr.Zero)
            {
                pendingTermination = false;
                SendNow(TypeTermination, new byte[] { 0x80, 0x03, 0x00, 0x23 }); // closed by the server, gracefully (big-endian)
            }
        }
    }

    private void Received(ReadOnlySpan<byte> m)
    {
        if (m.Length < 8 + 16 + 4 || BinaryPrimitives.ReadUInt16LittleEndian(m) != 0x0001) return;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(m[2..]);
        uint s = BinaryPrimitives.ReadUInt32LittleEndian(m[4..]);
        int plainLen = length - 4 - 16;
        if (plainLen < 4 || 4 + length > m.Length) return;
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, s);
        nonce[10] = (byte)'C';
        nonce[11] = (byte)'C';
        byte[] plain = new byte[plainLen];
        try { gcm.Decrypt(nonce, m.Slice(8 + 16, plainLen), m.Slice(8, 16), plain); }
        catch (CryptographicException) { Log.P("host: a control message did not decrypt"); return; }
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(plain);
        switch (type)
        {
            case TypeIdr:
            case TypeInvalidate:
                Log.P($"host: the phone asked for a key frame (0x{type:x4})");
                KeyFrameWanted?.Invoke();
                break;
            case TypeInput:
                if (inputs++ == 0) Log.P("host: first input from the phone");
                Input?.Invoke(plain.AsMemory(4));
                break;
        }
    }

    private volatile bool pendingTermination;

    /// <summary>Tells the phone the stream has ended (sent from the control thread).</summary>
    public void Terminate() => pendingTermination = true;

    private void SendNow(ushort type, byte[] payload)
    {
        byte[] plain = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(plain, type);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(plain, 4);
        uint s = seq++;
        byte[] msg = new byte[8 + 16 + plain.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(msg, 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(2), (ushort)(4 + 16 + plain.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4), s);
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, s);
        nonce[10] = (byte)'H';
        nonce[11] = (byte)'C';
        gcm.Encrypt(nonce, plain, msg.AsSpan(8 + 16), msg.AsSpan(8, 16));
        fixed (byte* p = msg) HostNative.PxrHostEnetSend(host, peer, p, msg.Length);
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join(1000);
        HostNative.PxrHostEnetStop(host);
        gcm.Dispose();
    }
}
