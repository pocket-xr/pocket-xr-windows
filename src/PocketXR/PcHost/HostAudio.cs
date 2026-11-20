using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// The host's sound (Sunshine's audioBroadcastThread, GPLv3): what this PC plays, captured by Windows' loopback
/// (WASAPI, through NAudio), made 48 kHz stereo, encoded to Opus at a constant bitrate in packets of the client's
/// duration (5 ms), each sent as RTP payload type 97, AES-CBC encrypted when the client asks (key: the launch's key;
/// IV: the big-endian launch key id plus the sequence number). After every four packets, two Reed-Solomon parity
/// packets (payload type 127) with GameStream's own parity matrix, so a lost packet is rebuilt.
///
/// Pacing, as Sunshine does it: captured sound goes into a ring, and a thread of its own sends one packet every packet
/// duration, by the clock, from the ring; silence only when the ring is empty. Sending each capture straight away sent
/// NAudio's ~50 ms chunks as ten packets back to back, and nothing at all while the PC was silent: the phone dropped
/// "too much pending audio" about 27 times a second and its player underran (Mike's glasses, 20 Nov 2026). The ring is
/// held to a few packets, so the capture's clock running a little fast against this one never builds up delay.
/// </summary>
internal sealed unsafe class HostAudio : IDisposable
{
    private const int Rate = 48000, Channels = 2, Bitrate = 192_000;
    private readonly Socket socket;
    private readonly int frameSamples, durationMs;
    private readonly bool encrypt;
    private readonly byte[] key;
    private readonly uint keyId;
    private readonly IOpusEncoder opus;
    private readonly Aes? aes;
    private WasapiCapture? capture;
    /// <summary>Captured 48 kHz stereo samples, interleaved, waiting to be sent; [ringCount] of them from [ringStart].</summary>
    private readonly float[] ring;
    private int ringStart, ringCount;
    private readonly object ringGate = new();
    public long SilentPackets, Trimmed;
    private volatile IPEndPoint? client;
    private readonly Thread pings;
    private volatile bool stopping;
    private readonly float[] pending;
    private int pendingCount;
    private double resamplePos;
    private ushort seq;
    private uint timestamp;
    private readonly byte[][] shards = new byte[4][];
    private int shardLen;
    private ushort baseSeq;
    private uint baseTimestamp;
    public long PacketsSent, FecSent;
    private float lastL, lastR;

    public HostAudio(int port, int durationMs, bool encrypt, byte[] key, int keyId)
    {
        this.durationMs = durationMs is 5 or 10 or 20 ? durationMs : 5;
        frameSamples = Rate * this.durationMs / 1000;
        this.encrypt = encrypt;
        this.key = key;
        this.keyId = (uint)keyId;
        if (encrypt)
        {
            aes = Aes.Create();
            aes.Key = key;
        }
        opus = OpusCodecFactory.CreateEncoder(Rate, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        opus.Bitrate = Bitrate;
        opus.UseVBR = false;
        opus.Complexity = 5;
        pending = new float[frameSamples * Channels * 4];
        ring = new float[Rate * Channels / 2]; // half a second at most
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null);
        pings = new Thread(Pings) { IsBackground = true, Name = "Host-audio-ping" };
        pings.Start();
    }

    public void Start()
    {
        try
        {
            // A 20 ms buffer, so the capture is read every ~10 ms (NAudio's own loopback capture uses 100 ms).
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            capture = new Loopback(device, 20);
            var f = capture.WaveFormat;
            Log.P($"host: sound from {f.SampleRate} Hz, {f.Channels} channels, {f.BitsPerSample} bits ({f.Encoding}); " +
                $"Opus {Bitrate / 1000} kbps in {durationMs} ms packets, {(encrypt ? "encrypted" : "not encrypted")}");
            capture.DataAvailable += (_, e) => Captured(e.Buffer, e.BytesRecorded, f);
            capture.StartRecording();
            new Thread(Pace) { IsBackground = true, Name = "Host-audio-pace", Priority = ThreadPriority.AboveNormal }.Start();
        }
        catch (Exception e)
        {
            Log.P($"host: no sound: {e.Message}");
        }
    }

    private void Pings()
    {
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!stopping)
        {
            try
            {
                int n = socket.ReceiveFrom(buf, ref from);
                if (n >= 4 && client == null) Log.P($"host: sound client at {from}");
                client = (IPEndPoint)from;
            }
            catch (SocketException) { if (stopping) return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>Captured sound to 48 kHz stereo float, in frames of the packet duration.</summary>
    private void Captured(byte[] buffer, int bytes, WaveFormat f)
    {
        if (client == null || stopping) return;
        int ch = Math.Max(1, f.Channels);
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
        int bytesPerSample = f.BitsPerSample / 8;
        int frames = bytes / (bytesPerSample * ch);
        double step = (double)f.SampleRate / Rate;
        for (int i = 0; i < frames; i++)
        {
            float l = Sample(buffer, (i * ch) * bytesPerSample, isFloat, bytesPerSample);
            float r = ch > 1 ? Sample(buffer, (i * ch + 1) * bytesPerSample, isFloat, bytesPerSample) : l;
            if (f.SampleRate == Rate)
            {
                Push(l, r);
                continue;
            }
            // Another rate (44.1 kHz): linear steps between this sample and the last.
            while (resamplePos < 1.0)
            {
                Push(lastL + (l - lastL) * (float)resamplePos, lastR + (r - lastR) * (float)resamplePos);
                resamplePos += step;
            }
            resamplePos -= 1.0;
            lastL = l;
            lastR = r;
        }
    }

    private static float Sample(byte[] b, int at, bool isFloat, int size) =>
        isFloat ? BitConverter.ToSingle(b, at) : size == 2 ? BitConverter.ToInt16(b, at) / 32768f : BitConverter.ToInt32(b, at) / 2147483648f;

    private void Push(float l, float r)
    {
        pending[pendingCount++] = l;
        pending[pendingCount++] = r;
        if (pendingCount >= frameSamples * Channels)
        {
            Store(pending.AsSpan(0, frameSamples * Channels));
            pendingCount = 0;
        }
    }

    /// <summary>A packet's worth of samples into the ring. More than 8 packets waiting: the oldest go, down to 2.</summary>
    private void Store(ReadOnlySpan<float> pcm)
    {
        lock (ringGate)
        {
            int packet = frameSamples * Channels;
            if (ringCount + pcm.Length > 8 * packet)
            {
                int drop = ringCount + pcm.Length - 2 * packet;
                drop = Math.Min(drop, ringCount);
                ringStart = (ringStart + drop) % ring.Length;
                ringCount -= drop;
                Trimmed += drop / packet;
            }
            for (int i = 0; i < pcm.Length; i++) ring[(ringStart + ringCount + i) % ring.Length] = pcm[i];
            ringCount += pcm.Length;
        }
    }

    /// <summary>One packet every packet duration, by the clock: from the ring, or silence when it is empty.</summary>
    private void Pace()
    {
        timeBeginPeriod(1);
        try
        {
            int packet = frameSamples * Channels;
            var frame = new float[packet];
            long tick = System.Diagnostics.Stopwatch.Frequency * durationMs / 1000;
            long next = System.Diagnostics.Stopwatch.GetTimestamp();
            bool primed = false;
            while (!stopping)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < next)
                {
                    double ms = (next - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > 1.5) Thread.Sleep(1);
                    else Thread.SpinWait(50);
                    continue;
                }
                // Far behind (the PC was busy): start again from now rather than send a burst.
                if (now - next > 10 * tick) next = now;
                next += tick;
                if (client == null) continue;
                bool fromRing;
                lock (ringGate)
                {
                    // After silence, wait for two packets in hand before playing from the ring, so one late capture
                    // does not cut in with silence.
                    if (!primed && ringCount >= 2 * packet) primed = true;
                    fromRing = primed && ringCount >= packet;
                    if (fromRing)
                    {
                        for (int i = 0; i < packet; i++) frame[i] = ring[(ringStart + i) % ring.Length];
                        ringStart = (ringStart + packet) % ring.Length;
                        ringCount -= packet;
                    }
                    else primed = false;
                }
                if (!fromRing)
                {
                    Array.Clear(frame);
                    SilentPackets++;
                }
                SendFrame(frame);
            }
        }
        catch (Exception e) { Log.P($"host: sound pacing stopped: {e.Message}"); }
        finally { timeEndPeriod(1); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>NAudio's loopback capture with a buffer of our choosing (its own is 100 ms).</summary>
    private sealed class Loopback : WasapiCapture
    {
        public Loopback(MMDevice device, int bufferMs) : base(device, false, bufferMs) { }
        protected override NAudio.CoreAudioApi.AudioClientStreamFlags GetAudioClientStreamFlags() =>
            NAudio.CoreAudioApi.AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    private void SendFrame(ReadOnlySpan<float> pcm)
    {
        var to = client;
        if (to == null) return;
        Span<byte> encoded = stackalloc byte[1400];
        int n = opus.Encode(pcm, frameSamples, encoded, encoded.Length);
        if (n <= 0) return;
        byte[] payload;
        if (encrypt)
        {
            byte[] iv = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(iv, keyId + seq);
            payload = aes!.EncryptCbc(encoded[..n], iv, PaddingMode.PKCS7);
        }
        else
        {
            payload = encoded[..n].ToArray();
        }
        byte[] packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = 97;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        payload.CopyTo(packet, 12);
        Send(packet, to);
        PacketsSent++;

        int slot = seq % 4;
        if (slot == 0)
        {
            baseSeq = seq;
            baseTimestamp = timestamp;
            shardLen = payload.Length;
        }
        shards[slot] = payload;
        if (payload.Length != shardLen) shardLen = -1; // sizes differ: no parity for this group
        if (slot == 3 && shardLen > 0) SendParity(to);
        seq++;
        timestamp += (uint)durationMs;
    }

    private void SendParity(IPEndPoint to)
    {
        byte[] all = new byte[6 * shardLen];
        for (int i = 0; i < 4; i++) shards[i].CopyTo(all, i * shardLen);
        fixed (byte* p = all)
            if (HostNative.PxrHostAudioFecEncode(p, shardLen) != 0) return;
        for (int x = 0; x < 2; x++)
        {
            byte[] packet = new byte[12 + 12 + shardLen];
            packet[0] = 0x80;
            packet[1] = 127;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(seq + x + 1));
            packet[12] = (byte)x;
            packet[13] = 97;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), baseSeq);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), baseTimestamp);
            Array.Copy(all, (4 + x) * shardLen, packet, 24, shardLen);
            Send(packet, to);
            FecSent++;
        }
    }

    private void Send(byte[] packet, IPEndPoint to)
    {
        try { socket.SendTo(packet, to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        stopping = true;
        try { capture?.StopRecording(); } catch (Exception) { }
        capture?.Dispose();
        try { socket.Dispose(); } catch (Exception) { }
        aes?.Dispose();
    }
}
