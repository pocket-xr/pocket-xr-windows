using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PocketXR.Core;

/// <summary>
/// The XREAL control link, 169.254.2.1:52999, ported from Pocket XR for Steam Deck's control.py (itself rewritten
/// from Pocket XR's GlassesWire).
///
/// Frame: u16 type, u32 body length (both big endian), body. An RPC body is a u32 big endian id (top bit set on
/// requests) and a protobuf with the request in field 3 and the answer in field 4 (answer field 1 = result, 0 for done;
/// field 2 = the value; field 3 = a second value).
///
/// Only commands Pocket XR has proven on the Aura are here, and `Sendable` refuses anything else.
/// Never 10079 (EDID), never 10325 to 10328 (firmware update), never a guessed id.
/// </summary>
public static class ControlWire
{
    public const string Host = "169.254.2.1";
    public const ushort Port = 52999;

    public const int Heartbeat = 10000;
    public const int StereoGet = 10273, StereoSet = 10274; // 2D (0) / 3D side by side (1)
    public const int UltrawideGet = 10277, UltrawideSet = 10278;
    public const int SizeRange = 10396, SizeGet = 10397, SizeSet = 10398;
    public const int AnchorGet = 10404, AnchorSet = 10405; // Spatial Anchor
    public const int SerialGet = 10025;
    public const int SpaceModeGet = 10421; // 1 follow, 0 (left out) hover
    public const int SpaceMode = 10284; // set { 1: mode }: 1 follow
    public const int SpaceFollow = 1;
    public const int Recenter = 10279; // empty request: in Ultrawide the glasses bring their canvas in front

    public static readonly IReadOnlySet<int> Sendable = new HashSet<int>
    {
        Heartbeat, StereoGet, StereoSet, UltrawideGet, UltrawideSet, SizeRange, SizeGet, SizeSet,
        AnchorGet, AnchorSet, SerialGet, Recenter, SpaceModeGet, SpaceMode,
    };
    public static readonly IReadOnlySet<int> Forbidden = new HashSet<int> { 10079, 10325, 10326, 10327, 10328 };
    public static readonly IReadOnlySet<int> Sets = new HashSet<int> { StereoSet, UltrawideSet, SizeSet, AnchorSet, Recenter, SpaceMode };

    /// <summary>Swift's WireError: its one case is notSendable(type).</summary>
    public sealed class WireError : Exception
    {
        /// <summary>The message type that was refused.</summary>
        public int Type { get; }

        private WireError(int type) : base($"message {type} may not be sent to the glasses")
        {
            Type = type;
        }

        public static WireError NotSendable(int type) => new(type);
    }

    // MARK: protobuf, just enough

    internal static byte[] Varint(ulong value)
    {
        ulong v = value;
        var @out = new List<byte>();
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            @out.Add(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
        return @out.ToArray();
    }

    /// <summary>Zero is left out, as proto3 and Pocket XR's ProtoWriter do: "off" is an empty request.</summary>
    public static byte[] PbVarint(int field, int v)
    {
        if (v == 0) return Array.Empty<byte>();
        return Join(Varint(checked((ulong)(field << 3))), Varint(checked((ulong)v)));
    }

    public static byte[] PbFloat(int field, float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        if (bits == 0) return Array.Empty<byte>();
        var le = new byte[4];
        for (int k = 0; k < 4; k++) le[k] = unchecked((byte)(bits >> (8 * k)));
        return Join(Varint(checked((ulong)(field << 3 | 5))), le);
    }

    public static byte[] PbMessage(int field, byte[] body)
    {
        return Join(Varint(checked((ulong)(field << 3 | 2))), Varint((ulong)body.Length), body);
    }

    /// <summary>A parsed protobuf value: a varint, a 32 bit float, a 64 bit double, or bytes.</summary>
    public readonly struct PbValue : IEquatable<PbValue>
    {
        public enum ValueKind
        {
            Int,
            F32,
            F64,
            Bytes,
        }

        public ValueKind Kind { get; }
        private readonly long intValue;
        private readonly float f32Value;
        private readonly double f64Value;
        private readonly byte[]? bytesValue;

        private PbValue(ValueKind kind, long i, float f, double d, byte[]? b)
        {
            Kind = kind;
            intValue = i;
            f32Value = f;
            f64Value = d;
            bytesValue = b;
        }

        public static PbValue Int(long v) => new(ValueKind.Int, v, 0, 0, null);
        public static PbValue F32(float v) => new(ValueKind.F32, 0, v, 0, null);
        public static PbValue F64(double v) => new(ValueKind.F64, 0, 0, v, null);
        public static PbValue Bytes(byte[] v) => new(ValueKind.Bytes, 0, 0, 0, v);

        /// <summary>The varint, when this is one.</summary>
        public long? AsInt => Kind == ValueKind.Int ? intValue : null;

        /// <summary>The bytes, when this is bytes.</summary>
        public byte[]? AsBytes => Kind == ValueKind.Bytes ? bytesValue : null;

        public double? Number => Kind switch
        {
            ValueKind.Int => (double)intValue,
            ValueKind.F32 => (double)f32Value,
            ValueKind.F64 => f64Value,
            _ => null,
        };

        public static bool operator ==(PbValue a, PbValue b)
        {
            if (a.Kind != b.Kind) return false;
            return a.Kind switch
            {
                ValueKind.Int => a.intValue == b.intValue,
                ValueKind.F32 => a.f32Value == b.f32Value,
                ValueKind.F64 => a.f64Value == b.f64Value,
                _ => (a.bytesValue ?? Array.Empty<byte>()).AsSpan().SequenceEqual(b.bytesValue ?? Array.Empty<byte>()),
            };
        }

        public static bool operator !=(PbValue a, PbValue b) => !(a == b);
        public bool Equals(PbValue other) => this == other;
        public override bool Equals(object? obj) => obj is PbValue v && this == v;
        public override int GetHashCode() => HashCode.Combine((int)Kind, intValue, f32Value, f64Value, bytesValue?.Length ?? 0);
    }

    /// <summary>
    /// field -> values, or null if malformed. (A field number and a varint come off the wire and can pass 2^31, so both
    /// are long here.)
    /// </summary>
    public static Dictionary<long, List<PbValue>>? PbParse(byte[] b)
    {
        var @out = new Dictionary<long, List<PbValue>>();
        int i = 0;
        ulong? ReadVarint()
        {
            ulong v = 0;
            int shift = 0;
            while (i < b.Length)
            {
                byte c = b[i];
                i += 1;
                if (shift < 64) v |= (ulong)(c & 0x7F) << shift;
                if ((c & 0x80) == 0) return v;
                shift += 7;
            }
            return null;
        }
        while (i < b.Length)
        {
            if (ReadVarint() is not ulong key) return null;
            long field = (long)(key >> 3);
            int wire = (int)(key & 7);
            PbValue value;
            switch (wire)
            {
                case 0:
                    if (ReadVarint() is not ulong v) return null;
                    value = PbValue.Int(unchecked((long)v));
                    break;
                case 5:
                    if (!(i + 4 <= b.Length)) return null;
                    value = PbValue.F32(BitConverter.UInt32BitsToSingle(ImuFramer.U32le(b, i)));
                    i += 4;
                    break;
                case 1:
                    if (!(i + 8 <= b.Length)) return null;
                    value = PbValue.F64(BitConverter.UInt64BitsToDouble(ImuFramer.U64le(b, i)));
                    i += 8;
                    break;
                case 2:
                    if (ReadVarint() is not ulong n || !(n <= (ulong)(b.Length - i))) return null;
                    value = PbValue.Bytes(b.AsSpan(i, (int)n).ToArray());
                    i += (int)n;
                    break;
                default:
                    return null;
            }
            if (!@out.TryGetValue(field, out List<PbValue>? list))
            {
                list = new List<PbValue>();
                @out[field] = list;
            }
            list.Add(value);
        }
        return @out;
    }

    // MARK: frames

    public static byte[] Header(int type, int length)
    {
        return new[]
        {
            (byte)(type >> 8 & 0xFF), (byte)(type & 0xFF),
            (byte)(length >> 24 & 0xFF), (byte)(length >> 16 & 0xFF), (byte)(length >> 8 & 0xFF), (byte)(length & 0xFF),
        };
    }

    /// <summary>The heartbeat request: kind 1, then the host's time in ns; 33 bytes little endian.</summary>
    public static byte[] HeartbeatFrame(ulong t1Ns)
    {
        var body = new byte[33];
        body[0] = 1;
        for (int k = 0; k < 8; k++) body[1 + k] = unchecked((byte)(t1Ns >> (8 * k)));
        return Join(Header(Heartbeat, body.Length), body);
    }

    /// <summary>The RPC id of request number [counter]: the top bit set for a request.</summary>
    public static uint RpcId(int counter) => (uint)((counter + 1) & 0x7FFF_FFFF) | 0x8000_0000;

    /// <summary>
    /// One RPC frame. Throws WireError when [type] is not on the allow list. (Swift's `request` defaults to an empty
    /// array; here null stands for it, as C# cannot have an array as a default value.)
    /// </summary>
    public static byte[] Rpc(int type, uint id, byte[]? request = null)
    {
        if (!(Sendable.Contains(type) && !Forbidden.Contains(type))) throw WireError.NotSendable(type);
        byte[] pb = PbMessage(3, request ?? Array.Empty<byte>());
        byte[] idBytes = { (byte)(id >> 24 & 0xFF), (byte)(id >> 16 & 0xFF), (byte)(id >> 8 & 0xFF), (byte)(id & 0xFF) };
        return Join(Header(type, 4 + pb.Length), idBytes, pb);
    }

    public static byte[] StereoSetFrame(uint id, bool sbs) => Rpc(StereoSet, id, PbVarint(1, sbs ? 1 : 0));
    public static byte[] UltrawideSetFrame(uint id, bool on) => Rpc(UltrawideSet, id, PbVarint(1, on ? 1 : 0));
    public static byte[] AnchorSetFrame(uint id, bool on) => Rpc(AnchorSet, id, PbVarint(1, on ? 1 : 0));
    public static byte[] SizeSetFrame(uint id, float size) => Rpc(SizeSet, id, PbFloat(1, size));
    public static byte[] FollowFrame(uint id) => Rpc(SpaceMode, id, PbVarint(1, SpaceFollow));
    /// <summary>Recentre in Ultrawide (10279, an empty request): the glasses bring their own canvas in front of the head.</summary>
    public static byte[] RecenterFrame(uint id) => Rpc(Recenter, id);

    /// <summary>
    /// Any screen size outside this is not believed, whatever range the glasses answer (Pocket XR's
    /// SCREEN_SIZE_LOWEST and SCREEN_SIZE_HIGHEST).
    /// </summary>
    public const double ScreenSizeLowest = 0.5;
    public const double ScreenSizeHighest = 1.5;

    /// <summary>
    /// A screen size the glasses answered (10397's value, or 10396's largest in its second value), if it can be
    /// believed: a number inside [ScreenSizeLowest, ScreenSizeHighest].
    /// </summary>
    public static double? BelievableSize(double? v)
    {
        if (v is double x && double.IsFinite(x) && x >= ScreenSizeLowest && x <= ScreenSizeHighest) return x;
        return null;
    }

    /// <summary>An answer from the glasses.</summary>
    public record struct Answer
    {
        public int Type;
        public uint Id;
        /// <summary>0 for done. A varint off the wire, so a long.</summary>
        public long Result;
        public double? Value;
        public double? Value2;
        public string? Text;

        public Answer(int type, uint id, long result, double? value, double? value2, string? text)
        {
            Type = type;
            Id = id;
            Result = result;
            Value = value;
            Value2 = value2;
            Text = text;
        }

        /// <summary>The value as a whole flag: an empty answer is 0 (proto3 leaves zero out).</summary>
        public readonly int Flag => Value is double v ? (double.IsFinite(v) && Math.Abs(v) < 1e9 ? (int)v : 0) : 0;
    }

    /// <summary>Splits the control stream into answers, skipping heartbeats and the glasses' own requests.</summary>
    public sealed class Framer
    {
        private readonly List<byte> buf = new();

        public Framer() { }

        public Answer[] Feed(byte[] chunk)
        {
            buf.AddRange(chunk);
            var @out = new List<Answer>();
            int pos = 0;
            while (buf.Count - pos >= 6)
            {
                int type = buf[pos] << 8 | buf[pos + 1];
                long n = (uint)buf[pos + 2] << 24 | (uint)buf[pos + 3] << 16 | (uint)buf[pos + 4] << 8 | (uint)buf[pos + 5];
                if (n > 1 << 20) { pos += 1; continue; }
                if (buf.Count - pos < 6 + n) break;
                byte[] body = buf.GetRange(pos + 6, (int)n).ToArray();
                pos += 6 + (int)n;
                if (!(type != Heartbeat && n >= 4)) continue;
                uint id = ImuFramer.U32be(body, 0);
                if (!((id & 0x8000_0000) == 0)) continue; // answers have the top bit clear
                Dictionary<long, List<PbValue>> fields = PbParse(body.AsSpan(4).ToArray()) ?? new Dictionary<long, List<PbValue>>();
                var resp = new Dictionary<long, List<PbValue>>();
                if (Last(fields, 4)?.AsBytes is byte[] r) resp = PbParse(r) ?? new Dictionary<long, List<PbValue>>();
                long result = 0;
                if (Last(resp, 1)?.AsInt is long code) result = code;
                string? text = null;
                if (Last(resp, 2)?.AsBytes is byte[] t) text = Encoding.UTF8.GetString(t);
                @out.Add(new Answer(type: type, id: id, result: result, value: Last(resp, 2)?.Number,
                                    value2: Last(resp, 3)?.Number, text: text));
            }
            buf.RemoveRange(0, pos);
            return @out.ToArray();
        }

        /// <summary>Swift's fields[n]?.last.</summary>
        private static PbValue? Last(Dictionary<long, List<PbValue>> fields, long field)
        {
            if (fields.TryGetValue(field, out List<PbValue>? list) && list.Count > 0) return list[^1];
            return null;
        }
    }

    private static byte[] Join(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (byte[] p in parts)
        {
            Array.Copy(p, 0, all, at, p.Length);
            at += p.Length;
        }
        return all;
    }
}
