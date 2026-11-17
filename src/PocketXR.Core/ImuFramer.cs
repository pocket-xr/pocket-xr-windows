using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PocketXR.Core;

/// <summary>One motion sample from the glasses: time in nanoseconds, gyro in rad/s, accelerometer in m/s².</summary>
public record struct ImuSample
{
    public ulong TimestampNs;
    public Vec3 Gyro;
    public Vec3 Accel;
    public double Temperature;

    public ImuSample(ulong timestampNs, Vec3 gyro, Vec3 accel, double temperature = 0)
    {
        TimestampNs = timestampNs;
        Gyro = gyro;
        Accel = accel;
        Temperature = temperature;
    }
}

/// <summary>
/// The XREAL One family's and Aura's sensor stream, TCP 169.254.2.1:52998, read only.
///
/// Ported from Pocket XR for Steam Deck's imu.py, itself written from Pocket XR's notes on the public protocol (MIT
/// projects, credited in Pocket XR's docs/LICENSES.md). Frame: magic 0x28 0x36 (some firmware 0x27 0x36), u32 big
/// endian body length, body. Sensor reports have a 128 byte little endian body: 0x08 timestamp ns u64, 0x18 type u32
/// (0x0B IMU, 0x04 magnetometer), 0x1C gyro xyz f32 rad/s, 0x28 accel xyz f32, 0x40 temperature f32.
/// </summary>
public sealed class ImuFramer
{
    public const string Host = "169.254.2.1";
    public const string EcmHost = "169.254.1.1";
    public const ushort Port = 52998;
    public const int Header = 6;
    public const int Body = 128;
    public const uint TypeImu = 0x0B;

    private readonly List<byte> buf = new();
    public int Reports { get; private set; }
    public int Imu { get; private set; }
    public int Dropped { get; private set; }
    public int BadLength { get; private set; }

    public ImuFramer() { }

    /// <summary>Takes the next bytes read from the socket and returns the IMU samples completed by them.</summary>
    public ImuSample[] Feed(ReadOnlySpan<byte> chunk)
    {
        buf.AddRange(chunk);
        var result = new List<ImuSample>();
        int pos = 0;
        int n = buf.Count;
        ReadOnlySpan<byte> b = CollectionsMarshal.AsSpan(buf);
        while (n - pos >= Header)
        {
            byte m0 = b[pos];
            if (!((m0 == 0x28 || m0 == 0x27) && b[pos + 1] == 0x36))
            {
                pos += 1;
                Dropped += 1;
                continue;
            }
            long length = U32be(b, pos + 2);
            if (length != Body)
            {
                pos += 1;
                BadLength += 1;
                continue;
            }
            if (n - pos < Header + Body) break;
            int at = pos + Header;
            if (U32le(b, at + 0x18) == TypeImu)
            {
                result.Add(new ImuSample(
                    timestampNs: U64le(b, at + 0x08),
                    gyro: new Vec3(F32le(b, at + 0x1C), F32le(b, at + 0x20), F32le(b, at + 0x24)),
                    accel: new Vec3(F32le(b, at + 0x28), F32le(b, at + 0x2C), F32le(b, at + 0x30)),
                    temperature: F32le(b, at + 0x40)));
                Imu += 1;
            }
            Reports += 1;
            pos += Header + Body;
        }
        buf.RemoveRange(0, pos);
        if (buf.Count > 65536)
        {
            Dropped += buf.Count - 65536;
            buf.RemoveRange(0, buf.Count - 65536);
        }
        return result.ToArray();
    }

    /// <summary>One frame, for tests and the fake source.</summary>
    public static byte[] Encode(ImuSample s, uint type = TypeImu, byte magic0 = 0x28)
    {
        var b = new byte[Body];
        PutU64le(b, 0, 1);
        PutU64le(b, 0x08, s.TimestampNs);
        PutU32le(b, 0x18, type);
        for (int i = 0; i < 3; i++)
        {
            PutU32le(b, 0x1C + 4 * i, BitConverter.SingleToUInt32Bits((float)s.Gyro[i]));
            PutU32le(b, 0x28 + 4 * i, BitConverter.SingleToUInt32Bits((float)s.Accel[i]));
        }
        PutU32le(b, 0x40, BitConverter.SingleToUInt32Bits((float)s.Temperature));
        var frame = new byte[Header + Body];
        frame[0] = magic0;
        frame[1] = 0x36;
        frame[5] = (byte)Body;
        Array.Copy(b, 0, frame, Header, Body);
        return frame;
    }

    // MARK: bytes

    internal static uint U32be(ReadOnlySpan<byte> b, int i) =>
        (uint)b[i] << 24 | (uint)b[i + 1] << 16 | (uint)b[i + 2] << 8 | (uint)b[i + 3];

    internal static uint U32le(ReadOnlySpan<byte> b, int i) =>
        (uint)b[i] | (uint)b[i + 1] << 8 | (uint)b[i + 2] << 16 | (uint)b[i + 3] << 24;

    internal static ulong U64le(ReadOnlySpan<byte> b, int i) => (ulong)U32le(b, i) | (ulong)U32le(b, i + 4) << 32;

    internal static double F32le(ReadOnlySpan<byte> b, int i) => (double)BitConverter.UInt32BitsToSingle(U32le(b, i));

    internal static void PutU32le(Span<byte> b, int i, uint v)
    {
        for (int k = 0; k < 4; k++) b[i + k] = unchecked((byte)(v >> (8 * k)));
    }

    internal static void PutU64le(Span<byte> b, int i, ulong v)
    {
        for (int k = 0; k < 8; k++) b[i + k] = unchecked((byte)(v >> (8 * k)));
    }
}
