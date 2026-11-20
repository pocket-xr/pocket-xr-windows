using System;
using System.Collections.Generic;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// The XREAL Air family's sensor stream, ported from Pocket XR's AirImuProtocol (written there from the publicly
/// documented protocol: ar-drivers-rs, MIT, and nrealAirLinuxDriver; none of their code). The Air, Air 2, Air 2 Pro
/// and Air 2 Ultra have no USB network: their IMU streams on a USB HID interface. Not yet run on Air glasses by the
/// project, on any system.
///
/// A command (64 bytes, to the IMU interface): 0xAA, a CRC32 (little endian) of everything from the length on, a
/// 2 byte little endian length (the payload's plus 3), the command id, the payload. 0x19 with payload 1 starts the
/// stream, with 0 stops it. That is the only command Pocket XR ever sends to these glasses.
///
/// A sensor report (64 bytes, from the IMU interface), little endian:
///
///   0x00  0x01 0x02
///   0x02  temperature, i16
///   0x04  timestamp, u64 (ns; AirTimestamp checks)
///   0x0C  gyro multiplier u16, divisor i32, then x, y, z as i24 (deg/s once multiplied and divided)
///   0x1B  accelerometer multiplier u16, divisor i32, then x, y, z as i24 (g; Parse gives m/s², as the fusion expects)
///   0x2A  magnetometer (not used)
/// </summary>
public static class AirImuProtocol
{
    public const int PacketBytes = 64;
    public const byte CommandHead = 0xAA;
    public const int CmdImuStream = 0x19;
    /// <summary>The Air 2 Ultra, whose IMU is on HID interface 2.</summary>
    public const int Air2Ultra = 0x0426;
    /// <summary>
    /// The XBX A01 and A01+: the Air's stream on HID interface 1, the MCU on 0, three 64 byte HID interfaces in all
    /// (wheaney's nrealAirLinuxDriver hid_ids.c, MIT, and a tester's descriptor of the A01+). Untested: not yet run on
    /// either, on any system.
    /// </summary>
    public const int XbxA01 = 0x0440, XbxA01Plus = 0x0442;
    /// <summary>m/s² in one g: the report's accelerometer unit to the fusion's.</summary>
    public const double G = 9.81;

    /// <summary>The model's name for the log, or null when [product] is not an Air family product with a known stream.</summary>
    public static string? ModelName(int product) => Glasses.AirImu.TryGetValue(product, out string? name) ? name : null;

    public static bool IsAir(int vendor, int product) => vendor == Glasses.Xreal && ModelName(product) != null;

    /// <summary>The HID interface the IMU streams on: 3, 2 on the Air 2 Ultra, and 1 on the XBX A01 and A01+.</summary>
    public static int ImuInterface(int product) => product switch
    {
        Air2Ultra => 2,
        XbxA01 or XbxA01Plus => 1,
        _ => 3,
    };

    /// <summary>
    /// CRC32 as zlib and java.util.zip.CRC32 compute it (polynomial 0xEDB88320, reflected), of [count] bytes from
    /// [from].
    /// </summary>
    public static uint Crc32(byte[] bytes, int from = 0, int? count = null)
    {
        int n = count ?? (bytes.Length - from);
        uint crc = 0xFFFF_FFFF;
        int i = from;
        int end = from + n;
        while (i < end)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++)
            {
                uint lowBit = crc & 1;
                crc >>= 1;
                if (lowBit != 0) crc ^= 0xEDB8_8320;
            }
            i += 1;
        }
        return crc ^ 0xFFFF_FFFF;
    }

    /// <summary>A 64 byte command [id] with [data]. Kept internal: the app only ever sends [StreamCommand].</summary>
    internal static byte[] Command(int id, byte[]? data = null)
    {
        data ??= Array.Empty<byte>();
        if (!(data.Length <= PacketBytes - 8)) throw new ArgumentException("command payload too long");
        var p = new byte[PacketBytes];
        p[0] = CommandHead;
        int length = data.Length + 3;
        p[5] = (byte)(length & 0xFF);
        p[6] = (byte)((length >> 8) & 0xFF);
        p[7] = (byte)(id & 0xFF);
        for (int k = 0; k < data.Length; k++) p[8 + k] = data[k];
        uint crc = Crc32(p, from: 5, count: length);
        for (int k = 0; k < 4; k++) p[1 + k] = unchecked((byte)(crc >> (8 * k)));
        return p;
    }

    /// <summary>The command that starts (or with [on] false, stops) the IMU stream.</summary>
    public static byte[] StreamCommand(bool on = true) => Command(CmdImuStream, new[] { on ? (byte)1 : (byte)0 });

    // MARK: the XBX's MCU

    // With Pocket XR for Steam Deck 0.6.0, a tester's XBX A01+ (Pierre Zammit, November 2026) sent nothing at all on
    // interface 1 after the stream command. wheaney's nrealAirLinuxDriver (hid_ids.c and mcu_hid.c, MIT) marks the XBX
    // A01 and A01+ as needing an MCU "heartbeat" on interface 0 before the IMU streams: commands 0x26, 0x57, 0x12 (1),
    // 0x02 (1), 0x34, 0x35, then 0x31 with the SDK version "3.1.1", then 0x1A with the time every 100 ms. The MCU has
    // frames of its own (0xFD), not the IMU's 0xAA ones. Ported from Pocket XR for Steam Deck 0.6.1. Untested.

    public static bool IsXbx(int product) => product is XbxA01 or XbxA01Plus;
    public const int McuInterface = 0;
    public const byte McuHead = 0xFD;
    public const int McuHeartbeatId = 0x1A;
    public static readonly byte[] McuSdkVersion = "3.1.1"u8.ToArray();
    /// <summary>The MCU's start-up handshake, in order: (message id, data).</summary>
    public static readonly IReadOnlyList<(int Id, byte[] Data)> McuBootstrap = new (int, byte[])[]
    {
        (0x26, Array.Empty<byte>()), (0x57, Array.Empty<byte>()), (0x12, new byte[] { 1, 0, 0, 0 }),
        (0x02, new byte[] { 1, 0, 0, 0 }), (0x34, Array.Empty<byte>()), (0x35, Array.Empty<byte>()), (0x31, McuSdkVersion),
    };

    /// <summary>
    /// An XREAL MCU frame: 0xFD, the CRC32 (little endian) of everything after it, then a little endian length (17 +
    /// data), timestamp (u64), message id (u16), 5 reserved bytes, the data. Not padded here.
    /// </summary>
    public static byte[] McuCommand(int id, byte[]? data = null, ulong timestamp = 0)
    {
        data ??= Array.Empty<byte>();
        int length = 17 + data.Length;
        var f = new byte[5 + length];
        f[0] = McuHead;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(5), (ushort)length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(7), timestamp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(15), (ushort)id);
        data.CopyTo(f, 22);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1), Crc32(f, from: 5));
        return f;
    }

    /// <summary>The heartbeat: 0x1A with the time (ns, any steady clock).</summary>
    public static byte[] McuHeartbeat(ulong nowNs)
    {
        var t = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(t, nowNs);
        return McuCommand(McuHeartbeatId, t);
    }

    /// <summary>
    /// The XBX's tries, in order, as (interface, with the MCU heartbeat): interface 1 alone, then interface 1 with the
    /// MCU woken on 0, then any other interface the glasses have (2 on the A01+), still with the heartbeat. Other Air
    /// models: their IMU interface only, never another one.
    /// </summary>
    public static List<(int Interface, bool Heartbeat)> Tries(int product, IEnumerable<int> interfaces)
    {
        var have = interfaces.Distinct().OrderBy(i => i).ToList();
        int imu = ImuInterface(product);
        var tries = new List<(int, bool)>();
        if (have.Contains(imu)) tries.Add((imu, false));
        if (!IsXbx(product)) return tries;
        bool mcu = have.Contains(McuInterface);
        if (mcu && have.Contains(imu)) tries.Add((imu, true));
        foreach (int i in have)
            if (i != McuInterface && i != imu) tries.Add((i, mcu));
        return tries;
    }

    /// <summary>The first bytes of a report in hex, for the log: "01 02 1a ... (64 bytes)".</summary>
    public static string HexStart(byte[] data, int n = 16) =>
        string.Join(" ", data.Take(n).Select(b => b.ToString("x2"))) + (data.Length > n ? " ..." : "") + $" ({data.Length} bytes)";

    /// <summary>Whether [packet] is a sensor report (not a command's answer, which starts 0xAA).</summary>
    public static bool IsReport(byte[] packet) => packet.Length >= 0x2A && packet[0] == 0x01 && packet[1] == 0x02;

    /// <summary>
    /// The report in [packet] as a sample in head axes (x right, y up, z back: what HeadFusion's Axes.Aura takes as it
    /// is), gyro in rad/s and accelerometer in m/s², or null when it is not a report or a divisor is 0. The sensor's own
    /// axes are x left, y back, z up, as ar-drivers-rs maps them (head x = -x, y = z, z = y); unconfirmed on glasses.
    /// The timestamp is the report's own, raw: AirTimestamp turns it into ns.
    /// </summary>
    public static ImuSample? Parse(byte[] packet)
    {
        if (!IsReport(packet)) return null;
        double gMul = U16(packet, 0x0C);
        int gDiv = I32(packet, 0x0E);
        double aMul = U16(packet, 0x1B);
        int aDiv = I32(packet, 0x1D);
        if (gDiv == 0 || aDiv == 0) return null;
        double gScale = gMul / gDiv * Math.PI / 180;
        // The report is in g, about 1 at rest, but the fusion works in m/s² (Pocket XR 1.2.3, issue 49).
        double aScale = aMul / aDiv * G;
        var gyro = new Vec3(I24(packet, 0x12) * gScale, I24(packet, 0x15) * gScale, I24(packet, 0x18) * gScale);
        var accel = new Vec3(I24(packet, 0x21) * aScale, I24(packet, 0x24) * aScale, I24(packet, 0x27) * aScale);
        return new ImuSample(timestampNs: ImuFramer.U64le(packet, 0x04), gyro: ToHead(gyro), accel: ToHead(accel),
                             temperature: I16(packet, 0x02));
    }

    /// <summary>The sensor's axes (x left, y back, z up) in head axes (x right, y up, z back).</summary>
    public static Vec3 ToHead(Vec3 v) => new(-v.X, v.Z, v.Y);

    /// <summary>Head axes back to the sensor's: the inverse of [ToHead].</summary>
    public static Vec3 ToSensor(Vec3 v) => new(-v.X, v.Z, v.Y);

    /// <summary>Builds a sensor report, for the checks and a fake source. Values are in the sensor's own axes, deg/s and g.</summary>
    public static byte[] EncodeReport(ulong timestamp, Vec3 gyroDegS, Vec3 accelG, int gyroDiv = 8192, int accelDiv = 8192)
    {
        var p = new byte[PacketBytes];
        p[0] = 0x01;
        p[1] = 0x02;
        for (int k = 0; k < 8; k++) p[0x04 + k] = unchecked((byte)(timestamp >> (8 * k)));
        Put16(p, 0x0C, 1);
        Put32(p, 0x0E, gyroDiv);
        Put16(p, 0x1B, 1);
        Put32(p, 0x1D, accelDiv);
        for (int k = 0; k < 3; k++)
        {
            Put24(p, 0x12 + 3 * k, checked((int)Math.Round(gyroDegS[k] * gyroDiv, MidpointRounding.AwayFromZero)));
            Put24(p, 0x21 + 3 * k, checked((int)Math.Round(accelG[k] * accelDiv, MidpointRounding.AwayFromZero)));
        }
        return p;
    }

    // MARK: bytes

    internal static int U16(byte[] p, int i) => p[i] | p[i + 1] << 8;

    internal static int I16(byte[] p, int i) => unchecked((short)(ushort)(p[i] | p[i + 1] << 8));

    /// <summary>Three bytes, sign extended.</summary>
    internal static int I24(byte[] p, int i)
    {
        uint raw = (uint)p[i] | (uint)p[i + 1] << 8 | (uint)p[i + 2] << 16;
        return unchecked((int)(raw << 8)) >> 8;
    }

    internal static int I32(byte[] p, int i) => unchecked((int)ImuFramer.U32le(p, i));

    private static void Put16(byte[] p, int at, int v)
    {
        p[at] = (byte)(v & 0xFF);
        p[at + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void Put24(byte[] p, int at, int v)
    {
        uint u = unchecked((uint)v);
        for (int k = 0; k < 3; k++) p[at + k] = unchecked((byte)(u >> (8 * k)));
    }

    private static void Put32(byte[] p, int at, int v)
    {
        uint u = unchecked((uint)v);
        for (int k = 0; k < 4; k++) p[at + k] = unchecked((byte)(u >> (8 * k)));
    }
}

/// <summary>
/// The Air report's timestamp unit, from its first steps (Pocket XR's AirTimestamp). The public drivers read it as
/// ns; should a firmware send µs instead, every step would be 1000 times too short and the fusion would turn the view
/// by a thousandth of the head's turn. At about 1 kHz a step of under 50 000 can only be µs.
///
/// A Swift struct with a mutating method; a class here, so the one reader's state cannot be changed on a copy by
/// mistake (a C# struct held in a readonly field would be).
/// </summary>
public sealed class AirTimestamp
{
    public const ulong MicrosBelow = 50_000;

    private ulong? last;
    /// <summary>Ns per unit: 1 until decided, then 1 (ns) or 1000 (µs).</summary>
    public ulong Scale { get; private set; } = 1;
    public bool Decided { get; private set; }

    public AirTimestamp() { }

    public ulong ToNs(ulong raw)
    {
        if (!Decided && last is ulong l && raw > l)
        {
            Scale = (raw - l) < MicrosBelow ? 1000UL : 1UL;
            Decided = true;
        }
        last = raw;
        return unchecked(raw * Scale);
    }

    public string Line
    {
        get
        {
            if (!Decided) return "timestamps: unit not decided yet";
            return Scale == 1 ? "timestamps: ns" : "timestamps: µs (taken as ns x 1000)";
        }
    }
}
