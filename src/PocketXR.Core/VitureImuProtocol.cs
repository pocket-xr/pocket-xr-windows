using System;
using System.Globalization;
using System.Runtime.CompilerServices;

// The checks build VitureImuProtocol's frames with its internal Frame(msgId, payload), as the Mac's checks do.
[assembly: InternalsVisibleTo("PocketXR.Checks")]

namespace PocketXR.Core;

/// <summary>
/// A VITURE frame read from a report: its message id and payload. (VitureImuProtocol.Frame on the Mac, a struct inside
/// VitureImuProtocol; C# cannot have a type and a method of one name in a class, so it stands beside it.)
/// </summary>
public readonly struct VitureImuProtocolFrame : IEquatable<VitureImuProtocolFrame>
{
    public int MsgId { get; }
    public byte[] Payload { get; }

    public VitureImuProtocolFrame(int msgId, byte[] payload)
    {
        MsgId = msgId;
        Payload = payload;
    }

    public static bool operator ==(VitureImuProtocolFrame a, VitureImuProtocolFrame b) =>
        a.MsgId == b.MsgId && (a.Payload ?? Array.Empty<byte>()).AsSpan().SequenceEqual(b.Payload ?? Array.Empty<byte>());

    public static bool operator !=(VitureImuProtocolFrame a, VitureImuProtocolFrame b) => !(a == b);
    public bool Equals(VitureImuProtocolFrame other) => this == other;
    public override bool Equals(object? obj) => obj is VitureImuProtocolFrame f && this == f;
    public override int GetHashCode() => HashCode.Combine(MsgId, Payload?.Length ?? 0);
}

/// <summary>
/// VITURE's glasses carry their IMU over USB HID on VITURE's own vendor id, 0x35CA. Ported from Pocket XR's
/// VitureImuProtocol, which was written from one public, MIT licensed protocol write-up (elasticjava/viture-v2's
/// PROTOCOL.md, measured on a VITURE Pro 2). No VITURE SDK code and no closed library is used. Experimental: the
/// project has run it on no VITURE glasses on a Mac.
///
/// A frame (one 64 byte HID report, zero padded, all little endian):
///
///   0  preamble 0x0010, u16
///   2  message id, u16
///   4  payload length, u16
///   6  checksum, u16: the plain sum of the payload's bytes, mod 65536
///   8  payload
///
/// 0x0301 (IMU control), payload 2 bytes: which streams (0x01 fused pose, 0x02 raw gyro and accelerometer, 0 off) and
/// a rate index, 0 the slowest (60 Hz) to 5 the fastest; index 2 is 120 Hz. Starting and stopping the raw stream is
/// the only thing Pocket XR ever sends to these glasses.
///
/// 0x7309 (a raw sample), payload: 4 u32 sample counter, 10 gyro x, y, z as f32 in rad/s, 22 accelerometer x, y, z as
/// f32 in g.
///
/// Axes: the write-up does not say which way the sensor's axes point, so the sensor's x, y, z are passed on as they
/// are. Pocket XR takes the Beast as x right, y down, z front (HeadFusion's Axes.One, which Glasses.Axes gives for it).
/// </summary>
public static class VitureImuProtocol
{
    public const int Vendor = 0x35CA;
    public const int PacketBytes = 64;
    public const int HeaderBytes = 8;
    public const int Preamble = 0x0010;

    public const int MsgImuControl = 0x0301;
    public const int MsgPose = 0x7308;
    public const int MsgRaw = 0x7309;

    public const byte StreamOff = 0x00;
    public const byte StreamRaw = 0x02;
    /// <summary>The rate index the write-up shows working (120 Hz).</summary>
    public const byte Rate120 = 2;
    /// <summary>The smallest raw payload holding the gyro and the accelerometer.</summary>
    public const int RawMinPayload = 34;
    /// <summary>The Beast's motion sensor is on HID interface 5 (found on the Deck); other models are tried interface by interface.</summary>
    public const int BeastImuInterface = 5;

    /// <summary>One raw sample: the counter, the gyro in rad/s and the accelerometer in g, in the sensor's own axes.</summary>
    public record struct RawSample
    {
        public uint Counter;
        public Vec3 Gyro;
        public Vec3 AccelG;

        public RawSample(uint counter, Vec3 gyro, Vec3 accelG)
        {
            Counter = counter;
            Gyro = gyro;
            AccelG = accelG;
        }
    }

    /// <summary>The plain byte sum of [count] bytes of [bytes] from [from], mod 65536.</summary>
    public static int Checksum(byte[] bytes, int from = 0, int? count = null)
    {
        int n = count ?? (bytes.Length - from);
        int s = 0;
        int i = from;
        while (i < from + n)
        {
            s += bytes[i];
            i += 1;
        }
        return s & 0xFFFF;
    }

    /// <summary>
    /// A frame for [msgId] with [payload], header and payload only ([Padded] makes it a 64 byte report). Kept
    /// internal: the app only ever sends [StartRaw] and [Stop].
    /// </summary>
    internal static byte[] Frame(int msgId, byte[] payload)
    {
        if (!(payload.Length <= PacketBytes - HeaderBytes)) throw new ArgumentException("payload too long");
        var p = new byte[HeaderBytes + payload.Length];
        Put16(p, 0, Preamble);
        Put16(p, 2, msgId);
        Put16(p, 4, payload.Length);
        Put16(p, 6, Checksum(payload));
        Array.Copy(payload, 0, p, HeaderBytes, payload.Length);
        return p;
    }

    /// <summary>[frame] zero padded to one 64 byte HID report.</summary>
    public static byte[] Padded(byte[] frame)
    {
        if (frame.Length >= PacketBytes) return frame;
        var p = new byte[PacketBytes];
        Array.Copy(frame, p, frame.Length);
        return p;
    }

    /// <summary>The command that starts the raw gyro and accelerometer stream at 120 Hz (header and payload only).</summary>
    public static byte[] StartRaw() => Frame(MsgImuControl, new[] { StreamRaw, Rate120 });

    /// <summary>The command that stops every IMU stream.</summary>
    public static byte[] Stop() => Frame(MsgImuControl, new byte[] { StreamOff, 0 });

    /// <summary>The frame in [packet], or null when the preamble, the length or the checksum is wrong.</summary>
    public static VitureImuProtocolFrame? ReadFrame(byte[] packet)
    {
        if (!(packet.Length >= HeaderBytes && U16(packet, 0) == Preamble)) return null;
        int len = U16(packet, 4);
        if (!(HeaderBytes + len <= packet.Length)) return null;
        if (!(Checksum(packet, from: HeaderBytes, count: len) == U16(packet, 6))) return null;
        return new VitureImuProtocolFrame(msgId: U16(packet, 2), payload: packet.AsSpan(HeaderBytes, len).ToArray());
    }

    /// <summary>The raw sample in a 0x7309 [payload], or null when it is too short or holds a value that is not a number.</summary>
    public static RawSample? ParseRaw(byte[] payload)
    {
        if (!(payload.Length >= RawMinPayload)) return null;
        var gyro = new Vec3(ImuFramer.F32le(payload, 10), ImuFramer.F32le(payload, 14), ImuFramer.F32le(payload, 18));
        var accel = new Vec3(ImuFramer.F32le(payload, 22), ImuFramer.F32le(payload, 26), ImuFramer.F32le(payload, 30));
        if (!(Finite(gyro) && Finite(accel))) return null;
        return new RawSample(counter: ImuFramer.U32le(payload, 4), gyro: gyro, accelG: accel);
    }

    /// <summary>
    /// The raw sample of a whole 64 byte report (gyro in rad/s, accelerometer in g, the sensor's own axes; VitureClock
    /// gives its time from the counter). Null when the report is not a raw sample.
    /// </summary>
    public static RawSample? ParseReport(byte[] packet)
    {
        if (!(ReadFrame(packet) is VitureImuProtocolFrame f && f.MsgId == MsgRaw)) return null;
        return ParseRaw(f.Payload);
    }

    /// <summary>[raw] as the fusion takes it: gyro in rad/s, accelerometer in m/s² (g times 9.81), axes as the sensor sends.</summary>
    public static ImuSample Sample(RawSample raw, ulong timestampNs) =>
        new(timestampNs: timestampNs, gyro: raw.Gyro, accel: raw.AccelG * AirImuProtocol.G);

    /// <summary>Builds a 64 byte raw sample report, for the checks and a fake source.</summary>
    public static byte[] EncodeRaw(uint counter, Vec3 gyroRadS, Vec3 accelG)
    {
        var p = new byte[46];
        ImuFramer.PutU32le(p, 4, counter);
        for (int k = 0; k < 3; k++)
        {
            ImuFramer.PutU32le(p, 10 + 4 * k, BitConverter.SingleToUInt32Bits((float)gyroRadS[k]));
            ImuFramer.PutU32le(p, 22 + 4 * k, BitConverter.SingleToUInt32Bits((float)accelG[k]));
        }
        return Padded(Frame(MsgRaw, p));
    }

    private static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    internal static int U16(byte[] p, int i) => p[i] | p[i + 1] << 8;

    private static void Put16(byte[] p, int at, int v)
    {
        p[at] = (byte)(v & 0xFF);
        p[at + 1] = (byte)((v >> 8) & 0xFF);
    }
}

/// <summary>
/// The raw samples' times (Pocket XR's VitureClock). A sample carries only a counter, one a sample, so the time step
/// comes from the rate the glasses really send at, measured against the PC's clock over the first 120 samples; until
/// then the asked for rate's. A counter that goes back (a restart) starts over.
///
/// A Swift struct with a mutating method; a class here: a C# struct's `new VitureClock()` would skip the constructor
/// with its default rate and leave a period of 0.
/// </summary>
public sealed class VitureClock
{
    public const long LearnSamples = 120;

    private readonly double nominalNs;
    private long firstCounter = -1;
    private ulong firstHostNs = 0;
    private long lastCounter = -1;
    /// <summary>Ns a sample.</summary>
    public double PeriodNs { get; private set; }
    public bool Learned { get; private set; }

    public VitureClock(double nominalHz = 120)
    {
        nominalNs = 1e9 / nominalHz;
        PeriodNs = 1e9 / nominalHz;
    }

    /// <summary>The sample's time, ns, from its [counter] and the PC's [hostNs] when it came.</summary>
    public ulong ToNs(uint counter, ulong hostNs)
    {
        long c = counter;
        if (firstCounter < 0 || c < lastCounter)
        {
            firstCounter = c;
            firstHostNs = hostNs;
            Learned = false;
            PeriodNs = nominalNs;
        }
        lastCounter = c;
        long steps = c - firstCounter;
        if (!Learned && steps >= LearnSamples)
        {
            double measured = (double)unchecked(hostNs - firstHostNs) / (double)steps;
            // A rate off by more than a factor of 20 from what was asked is not believed.
            if (measured > PeriodNs / 20 && measured < PeriodNs * 20) PeriodNs = measured;
            Learned = true;
        }
        return unchecked(firstHostNs + checked((ulong)Math.Round((double)steps * PeriodNs, MidpointRounding.AwayFromZero)));
    }

    public string Line =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1:F1} Hz", Learned ? "samples measured at" : "samples taken as", 1e9 / PeriodNs);
}
