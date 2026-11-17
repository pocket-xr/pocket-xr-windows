using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;

namespace PocketXR.Core;

/// <summary>
/// Reads the glasses' motion stream (TCP 52998) on its own thread and hands each sample to [onSample]. Reconnects
/// after a loss, waiting 1 s, then up to 8 s between tries. Nothing is ever sent on this link.
/// </summary>
public sealed class ImuReader
{
    private readonly Action<ImuSample> onSample;
    private readonly ImuFramer framer = new();
    private Thread? thread;
    private readonly object gate = new();
    private volatile bool stopped;
    private string? connectedVia;
    private int samples;

    public int Samples => Volatile.Read(ref samples);

    /// <summary>How the stream is reached now ("UsbNcm Host Device (169.254.2.10 -> 169.254.2.1)"), null while not connected.</summary>
    public string? ConnectedVia { get { lock (gate) return connectedVia; } }

    /// <summary>The framer's own counts, for a report: reports seen, bytes skipped, wrong lengths.</summary>
    public (int Reports, int Imu, int Dropped, int BadLength) Counts => (framer.Reports, framer.Imu, framer.Dropped, framer.BadLength);

    public ImuReader(Action<ImuSample> onSample)
    {
        this.onSample = onSample;
    }

    public void Start()
    {
        var t = new Thread(Run) { Name = "Pocket XR head tracking", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread = t;
        t.Start();
    }

    public void Stop()
    {
        stopped = true;
    }

    private void Run()
    {
        double delay = 1.0;
        var buf = new byte[65536];
        bool quietLogged = false;
        while (!stopped)
        {
            Socket socket;
            string via;
            try
            {
                (socket, via) = GlassesNet.Open(ImuFramer.Port, line =>
                {
                    if (!quietLogged) { Log.P(line); quietLogged = true; }
                });
            }
            catch (Exception)
            {
                // Sleep in short steps, so Stop is noticed.
                for (double waited = 0; waited < delay && !stopped; waited += 0.1) Thread.Sleep(100);
                delay = Math.Min(delay * 2, 8);
                continue;
            }
            quietLogged = false;
            delay = 1;
            lock (gate) connectedVia = via;
            Log.P($"head tracking: reading 52998 via {via}");
            long lastData = Stopwatch.GetTimestamp();
            while (!stopped)
            {
                byte[]? chunk = GlassesNet.Receive(socket, buf);
                if (chunk == null)
                {
                    Log.P("head tracking: the glasses closed the stream");
                    break;
                }
                if (chunk.Length == 0)
                {
                    if (Stopwatch.GetElapsedTime(lastData).TotalSeconds > 3)
                    {
                        Log.P("head tracking: no data for 3 s; reconnecting");
                        break;
                    }
                    continue;
                }
                lastData = Stopwatch.GetTimestamp();
                foreach (ImuSample s in framer.Feed(chunk))
                {
                    Interlocked.Increment(ref samples);
                    onSample(s);
                }
            }
            try { socket.Close(); } catch (Exception) { }
            lock (gate) connectedVia = null;
        }
    }
}

/// <summary>
/// One open control link (TCP 52999): a heartbeat every 50 ms, as Pocket XR does, and Request() waits for its answer.
/// Ported from the Deck's control.Link.
/// </summary>
public sealed class ControlLink
{
    private readonly Socket socket;
    public string Via { get; }
    private readonly object sendGate = new();
    private readonly object stateGate = new();
    private int counter;
    private readonly Dictionary<uint, ControlWire.Answer> answers = new();
    private volatile bool closed;
    private readonly ControlWire.Framer framer = new();

    public bool Closed => closed;

    public ControlLink()
    {
        (socket, Via) = GlassesNet.Open(ControlWire.Port, Log.P);
        new Thread(ReadLoop) { Name = "Pocket XR control link", IsBackground = true }.Start();
        new Thread(BeatLoop) { Name = "Pocket XR heartbeat", IsBackground = true }.Start();
    }

    private void Send(byte[] bytes)
    {
        lock (sendGate) GlassesNet.Send(socket, bytes);
    }

    /// <summary>Nanoseconds since the PC started, as the heartbeat carries.</summary>
    private static ulong UptimeNanoseconds() =>
        (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    private void BeatLoop()
    {
        while (!closed)
        {
            try { Send(ControlWire.HeartbeatFrame(UptimeNanoseconds())); } catch (Exception) { break; }
            Thread.Sleep(50);
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        while (!closed)
        {
            byte[]? chunk = GlassesNet.Receive(socket, buf);
            if (chunk == null) break;
            if (chunk.Length == 0) continue;
            var got = framer.Feed(chunk);
            lock (stateGate)
            {
                foreach (ControlWire.Answer a in got) answers[a.Id & 0x7FFF_FFFF] = a;
            }
        }
        closed = true;
    }

    /// <summary>Sends the frame built for a new request id and waits up to [wait] seconds for its answer.</summary>
    public ControlWire.Answer? Request(string what, Func<uint, byte[]> build, double wait = 3)
    {
        uint id;
        lock (stateGate)
        {
            id = ControlWire.RpcId(counter);
            counter += 1;
        }
        byte[] frame = build(id);
        int type = frame[0] << 8 | frame[1];
        Send(frame);
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < wait && !closed)
        {
            ControlWire.Answer? a = null;
            lock (stateGate)
            {
                if (answers.Remove(id & 0x7FFF_FFFF, out ControlWire.Answer found)) a = found;
            }
            if (a is ControlWire.Answer got)
            {
                // Sets and refusals are logged; plain reads only when they fail, to keep the log readable.
                if (ControlWire.Sets.Contains(type) || got.Result != 0)
                    Log.P($"  {what} ({type}) -> result {got.Result} value {(got.Value is { } v ? v.ToString() : "none")}");
                return got;
            }
            Thread.Sleep(10);
        }
        Log.P($"  {what} ({type}) -> no answer");
        return null;
    }

    /// <summary>An empty read.</summary>
    public ControlWire.Answer? Get(int type, double wait = 3)
    {
        try { return Request("read", id => ControlWire.Rpc(type, id), wait); }
        catch (Exception) { return null; }
    }

    public void Close()
    {
        closed = true;
        try { socket.Close(); } catch (Exception) { }
    }
}
