using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PocketXR.Core;

namespace PocketXR.Checks;

// The glasses' side: the Air family's and VITURE's HID packets, Steady mode's steps, the control frames Steady mode
// sends, which model can do what, and the Glasses report. Ported from Pocket XR's AirTracking101Test, Discussion24Test,
// Ultrawide0118Test, SteadySixDofTest, OneEyeSixDofTest, SteadyReplugTest and DisplayCommands0532Test.
public static class GlassesChecks
{
    private static byte[] HexBytes(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => byte.TryParse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b) ? b : (byte)0)
            .ToArray();

    /// <summary>Swift's `try?`: the frame, or null when it was refused.</summary>
    private static byte[]? Try(Func<byte[]> build)
    {
        try { return build(); } catch (Exception) { return null; }
    }

    /// <summary>Swift's == between an optional array and an array.</summary>
    private static bool Same(byte[]? a, byte[] b) => a != null && a.AsSpan().SequenceEqual(b);

    private static bool Near(double a, double b, double tolerance = 1e-6) => Check.Near(a, b, tolerance);

    /// <summary>What the glasses answered, for SteadyPlan: 0 is off and 1 is on.</summary>
    private static SettingsControl.State GlassesState(int? stereo = 0, int? wide = 0, int? anchor = 0, double? size = 1.2, double? max = 1.2)
    {
        var s = new SettingsControl.State();
        s.Stereo = stereo;
        s.Ultrawide = wide;
        s.Anchor = anchor;
        s.SizeFactor = size;
        s.SizeMax = max;
        return s;
    }

    public static void Run()
    {
        AirChecks();
        VitureChecks();
        SteadyChecks();
        ControlWireChecks();
        CapabilityChecks();
        ReportChecks();
    }

    private static void AirChecks()
    {
        double deg = Math.PI / 180;

        // CRC32 as zlib computes it.
        Check.That(AirImuProtocol.Crc32(Encoding.UTF8.GetBytes("123456789")) == 0xCBF4_3926, "CRC32 of 123456789");

        // The start command is framed as the Air expects: 0xAA, the CRC of the length, id and payload, length 4, 0x19, 1.
        byte[] start = AirImuProtocol.StreamCommand();
        Check.That(start.Length == 64, "the Air command is 64 bytes");
        Check.That(start[0] == 0xAA, "it starts 0xAA");
        Check.That((start[5] | start[6] << 8) == 4, "length: one byte of payload plus 3");
        Check.That(start[7] == 0x19 && start[8] == 1, "command 0x19 with 1 starts the stream");
        Check.That(start[1..5].SequenceEqual(new byte[] { 0xC5, 0xD1, 0x21, 0x42 }), "the start command's CRC32, little endian (0x4221D1C5)");
        Check.That(start[9..].All(b => b == 0), "the rest is zero");
        byte[] stop = AirImuProtocol.StreamCommand(on: false);
        Check.That(stop[7] == 0x19 && stop[8] == 0, "command 0x19 with 0 stops it");
        Check.That(stop[1..5].SequenceEqual(new byte[] { 0x53, 0xE1, 0x26, 0x35 }), "the stop command's CRC32 (0x3526E153)");

        // The Air 2 Pro is an Air on HID interface 3; the Aura and the One keep their network stream.
        Check.That(AirImuProtocol.IsAir(vendor: 0x3318, product: 0x0432), "the Air 2 Pro is an Air");
        Check.That(AirImuProtocol.ModelName(0x0432) == "XREAL Air 2 Pro", "its name");
        Check.That(AirImuProtocol.ImuInterface(0x0432) == 3 && AirImuProtocol.ImuInterface(0x0424) == 3, "the IMU is on interface 3");
        Check.That(AirImuProtocol.ImuInterface(0x0426) == 2, "and on 2 on the Air 2 Ultra");
        Check.That(AirImuProtocol.ImuInterface(0x0440) == 1 && AirImuProtocol.ImuInterface(0x0442) == 1, "and on 1 on the XBX A01 and A01+");
        Check.That(AirImuProtocol.IsAir(vendor: 0x3318, product: 0x0440) && AirImuProtocol.IsAir(vendor: 0x3318, product: 0x0442),
                   "the XBX A01 and A01+ stream as an Air");
        Check.That(AirImuProtocol.ModelName(0x0442) == "XREAL XBX A01+" && AirImuProtocol.ModelName(0x0440) == "XREAL XBX A01", "their names");

        // The XBX's MCU frame, as wheaney's library lays it out (the Deck's test, the same bytes).
        byte[] mcu = AirImuProtocol.McuCommand(0x31, AirImuProtocol.McuSdkVersion);
        Check.That(mcu[0] == 0xFD && mcu.Length == 5 + 17 + 5, "an MCU frame: 0xFD, CRC, 17 bytes of head, the data");
        Check.That((mcu[5] | mcu[6] << 8) == 22 && mcu[7..15].All(b => b == 0) && (mcu[15] | mcu[16] << 8) == 0x31,
                   "its length (17 + data), timestamp 0, message id 0x31");
        Check.That(mcu[17..22].All(b => b == 0) && mcu[22..].SequenceEqual("3.1.1"u8.ToArray()), "5 reserved bytes, then the SDK version");
        uint mcuCrc = (uint)(mcu[1] | mcu[2] << 8 | mcu[3] << 16 | mcu[4] << 24);
        Check.That(mcuCrc == AirImuProtocol.Crc32(mcu, from: 5), "its CRC32 covers everything after the CRC");
        byte[] beat = AirImuProtocol.McuHeartbeat(123);
        Check.That(beat.Length == 30 && beat[15] == 0x1A && beat[16] == 0 && beat[22] == 123, "the heartbeat: 0x1A with the time");
        Check.That(AirImuProtocol.McuBootstrap.Select(m => m.Id).SequenceEqual(new[] { 0x26, 0x57, 0x12, 0x02, 0x34, 0x35, 0x31 }),
                   "the MCU's start-up handshake, in the library's order");
        Check.That(AirImuProtocol.HexStart(Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(), 4) == "00 01 02 03 ... (20 bytes)",
                   "the first bytes of a report, in hex");

        // The XBX A01+'s tries: interface 1 alone, with the MCU woken on 0, then interface 2. An Air 2 tries only its own.
        var xbxTries = AirImuProtocol.Tries(0x0442, new[] { 0, 1, 2 });
        Check.That(xbxTries.SequenceEqual(new[] { (1, false), (1, true), (2, true) }), "the XBX: 1, then 1 with the heartbeat, then 2");
        Check.That(AirImuProtocol.Tries(0x0440, new[] { 1 }).SequenceEqual(new[] { (1, false) }), "with no interface 0 listed, no heartbeat");
        Check.That(AirImuProtocol.Tries(0x0428, new[] { 0, 1, 2, 3, 4 }).SequenceEqual(new[] { (3, false) }), "the Air 2: interface 3 only");
        Check.That(AirImuProtocol.Tries(0x0426, new[] { 0, 1, 2, 3 }).SequenceEqual(new[] { (2, false) }), "the Air 2 Ultra: interface 2 only");
        Check.That(AirImuProtocol.Tries(0x0428, new[] { 4 }).Count == 0, "no IMU interface listed: nothing tried");
        Check.That(!AirImuProtocol.IsAir(vendor: 0x3318, product: 0x0439), "the Aura is not an Air");
        Check.That(!AirImuProtocol.IsAir(vendor: 0x3318, product: 0x0437), "the One is not an Air");
        Check.That(!AirImuProtocol.IsAir(vendor: 0x1234, product: 0x0432), "another maker's device is not an Air");

        // A report is read in head axes, radians a second and metres per second squared.
        byte[] p = AirImuProtocol.EncodeReport(timestamp: 123_456_789, gyroDegS: new Vec3(10, -20, 30),
                                               accelG: new Vec3(0.1, -0.2, 0.97));
        ImuSample? r = AirImuProtocol.Parse(p);
        Check.That(r != null, "an Air report is read");
        if (r is ImuSample got)
        {
            Check.That(got.TimestampNs == 123_456_789, "its timestamp");
            // Head x = -sensor x, y = sensor z, z = sensor y.
            Check.That(Near(got.Gyro.X, -10 * deg, 1e-4) && Near(got.Gyro.Y, 30 * deg, 1e-4) && Near(got.Gyro.Z, -20 * deg, 1e-4), "gyro in head axes, rad/s");
            // The report is in g; the fusion works in m/s2.
            Check.That(Near(got.Accel.X, -0.1 * 9.81, 1e-2) && Near(got.Accel.Y, 0.97 * 9.81, 1e-2) && Near(got.Accel.Z, -0.2 * 9.81, 1e-2), "accelerometer in head axes, m/s2");
        }
        // At rest, flat on a desk as worn (sensor z up), gravity reads up in head axes at 9.81.
        ImuSample? rest = AirImuProtocol.Parse(AirImuProtocol.EncodeReport(timestamp: 1, gyroDegS: Vec3.Zero, accelG: new Vec3(0, 0, 1)));
        Check.That(rest is ImuSample atRest && Near(atRest.Accel.Y, 9.81, 1e-2) && Near(atRest.Accel.X, 0, 1e-6), "gravity is up (head y) at 9.81");
        Check.That(AirImuProtocol.ToSensor(AirImuProtocol.ToHead(new Vec3(1, 2, 3))) == new Vec3(1, 2, 3), "to head axes and back");

        // What is not a report is refused.
        Check.That(AirImuProtocol.Parse(new byte[64]) == null, "zeros are not a report");
        Check.That(AirImuProtocol.Parse(start) == null, "a command's answer (0xAA) is not a report");
        byte[] wrong = (byte[])p.Clone();
        wrong[0] = 0x02;
        Check.That(AirImuProtocol.Parse(wrong) == null, "a wrong first byte is refused");
        Check.That(AirImuProtocol.Parse(p[..0x20]) == null, "a short packet is refused");
        byte[] zeroDiv = AirImuProtocol.EncodeReport(timestamp: 1, gyroDegS: Vec3.Zero, accelG: Vec3.Zero, gyroDiv: 0);
        Check.That(AirImuProtocol.Parse(zeroDiv) == null, "a divisor of 0 is not a report");

        // The timestamp's unit comes from its first step.
        var ns = new AirTimestamp();
        Check.That(ns.ToNs(1_000_000) == 1_000_000 && !ns.Decided, "the first timestamp is taken as it is");
        Check.That(ns.ToNs(2_000_000) == 2_000_000 && ns.Decided && ns.Scale == 1, "steps of a million are ns");
        var us = new AirTimestamp();
        _ = us.ToNs(1_000);
        Check.That(us.ToNs(2_000) == 2_000_000 && us.Scale == 1000, "steps of a thousand are microseconds");
    }

    private static void VitureChecks()
    {
        // The write-up's example frame, and the two commands Pocket XR sends.
        byte[] example = VitureImuProtocol.Frame(VitureImuProtocol.MsgImuControl, new byte[] { 1, 2 });
        Check.That(example.SequenceEqual(HexBytes("10 00 01 03 02 00 03 00 01 02")), "the IMU control frame matches the write-up's example");
        Check.That(VitureImuProtocol.StartRaw().SequenceEqual(HexBytes("10 00 01 03 02 00 04 00 02 02")), "the raw stream start command");
        Check.That(VitureImuProtocol.Stop().SequenceEqual(HexBytes("10 00 01 03 02 00 00 00 00 00")), "the stop command");
        byte[] padded = VitureImuProtocol.Padded(VitureImuProtocol.StartRaw());
        Check.That(padded.Length == 64 && padded[10..].All(b => b == 0), "padded with zeros to one 64 byte report");
        VitureImuProtocolFrame? back = VitureImuProtocol.ReadFrame(VitureImuProtocol.Padded(example));
        Check.That(back?.MsgId == VitureImuProtocol.MsgImuControl && back is VitureImuProtocolFrame bf && bf.Payload.SequenceEqual(new byte[] { 1, 2 }), "a frame reads back");

        // A raw sample is read, and a bad frame is not.
        byte[] p = VitureImuProtocol.EncodeRaw(counter: 4242, gyroRadS: new Vec3(0.1, -0.2, 0.3), accelG: new Vec3(0, 1, 0));
        Check.That(p.Length == 64, "a raw sample is one 64 byte report");
        VitureImuProtocolFrame? f = VitureImuProtocol.ReadFrame(p);
        Check.That(f?.MsgId == VitureImuProtocol.MsgRaw, "it is message 0x7309");
        VitureImuProtocol.RawSample? s = VitureImuProtocol.ParseReport(p);
        Check.That(s?.Counter == 4242, "its counter");
        Check.That(s is VitureImuProtocol.RawSample sg && Near(sg.Gyro.X, 0.1, 1e-6) && Near(sg.Gyro.Y, -0.2, 1e-6) && Near(sg.Gyro.Z, 0.3, 1e-6), "gyro in rad/s");
        Check.That(s is VitureImuProtocol.RawSample sa && Near(sa.AccelG.Y, 1, 1e-6), "accelerometer in g");
        if (s is VitureImuProtocol.RawSample raw)
        {
            ImuSample sample = VitureImuProtocol.Sample(raw, timestampNs: 7);
            Check.That(Near(sample.Accel.Y, 9.81, 1e-4) && Near(sample.Gyro.Y, -0.2, 1e-6) && sample.TimestampNs == 7, "the fusion gets m/s2 and rad/s");
        }
        byte[] badSum = (byte[])p.Clone();
        badSum[20] = unchecked((byte)(badSum[20] + 1));
        Check.That(VitureImuProtocol.ReadFrame(badSum) == null, "a wrong checksum is refused");
        byte[] badMagic = (byte[])p.Clone();
        badMagic[0] = 0x11;
        Check.That(VitureImuProtocol.ReadFrame(badMagic) == null, "a wrong preamble is refused");
        Check.That(VitureImuProtocol.ReadFrame(p[..20]) == null, "a length past the packet is refused");
        byte[] air = AirImuProtocol.EncodeReport(timestamp: 1, gyroDegS: Vec3.Zero, accelG: new Vec3(0, 0, 1));
        Check.That(VitureImuProtocol.ReadFrame(air) == null, "XREAL's Air report is not a VITURE frame");
        byte[] pose = VitureImuProtocol.Padded(VitureImuProtocol.Frame(VitureImuProtocol.MsgPose, new byte[24]));
        Check.That(VitureImuProtocol.ReadFrame(pose) != null && VitureImuProtocol.ParseReport(pose) == null, "a pose frame is not a raw sample");

        // The clock learns the real rate from the counter.
        var c = new VitureClock(nominalHz: 120);
        ulong period = 1_000_000_000 / 240; // the glasses really send 240 a second
        ulong t = 0;
        for (int n = 0; n <= 200; n++) t = c.ToNs(counter: (uint)(1000 + n), hostNs: 5_000_000_000 + (ulong)n * period);
        Check.That(c.Learned, "the rate is learnt after 120 samples");
        Check.That(Math.Abs((long)t - (long)(5_000_000_000 + 200 * period)) < 1_000, "times follow the measured rate");
        Check.That(c.Line.Contains("240.0 Hz"), "the clock's line says 240 Hz");
        Check.That(c.ToNs(counter: 3, hostNs: 9_000_000_000) == 9_000_000_000 && !c.Learned, "a counter that goes back starts over");
    }

    private static void SteadyChecks()
    {
        // Plain Steady mode (3DoF) goes step by step (Ultrawide0118Test).
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(stereo: 1, anchor: 1, size: 0.8, max: 1.3)) == SteadyPlan.Step.AnchorOff, "Spatial Anchor off first");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(stereo: 1, size: 0.8, max: 1.3)) == SteadyPlan.Step.Leave3D, "then out of 3D");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(size: 0.8, max: 1.3)) == SteadyPlan.Step.UltrawideOn, "then Ultrawide on");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, size: 0.9, max: 1.2)) == SteadyPlan.Step.ScreenMax, "then the largest screen size");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, size: 1.2, max: 1.2)) == SteadyPlan.Step.Done, "then done");
        Check.That(SteadyPlan.Next(on: true, state: new SettingsControl.State()) == SteadyPlan.Step.Wait, "nothing read yet: wait");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, max: null)) == SteadyPlan.Step.Wait, "the range not read: wait");
        var onlyWide = new SettingsControl.State();
        onlyWide.Ultrawide = 1;
        Check.That(SteadyPlan.Next(on: false, state: onlyWide) == SteadyPlan.Step.UltrawideOff, "off only turns Ultrawide off");
        onlyWide.Ultrawide = 0;
        Check.That(SteadyPlan.Next(on: false, state: onlyWide) == SteadyPlan.Step.Done, "off with Ultrawide off: done");
        Check.That(SteadyPlan.Next(on: false, state: new SettingsControl.State()) == SteadyPlan.Step.Wait, "off before a read: wait");

        // In 6DoF the anchor goes on last and off first (SteadySixDofTest).
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(stereo: 1), sixDof: true) == SteadyPlan.Step.Leave3D, "6DoF: to 2D");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(), sixDof: true) == SteadyPlan.Step.UltrawideOn, "6DoF: Ultrawide");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, size: 1.0), sixDof: true) == SteadyPlan.Step.ScreenMax, "6DoF: size");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1), sixDof: true) == SteadyPlan.Step.AnchorOn, "6DoF: Spatial Anchor on, last");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, anchor: 1), sixDof: true) == SteadyPlan.Step.Done, "6DoF: done");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(stereo: 1, anchor: 1), sixDof: true) == SteadyPlan.Step.Leave3D, "6DoF: an anchor already on is kept");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(wide: 1, anchor: 1), sixDof: true) == SteadyPlan.Step.AnchorOff, "6DoF off: the anchor first");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(wide: 1), sixDof: true) == SteadyPlan.Step.UltrawideOff, "6DoF off: then Ultrawide");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(), sixDof: true) == SteadyPlan.Step.Done, "6DoF off: done");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(wide: 1, anchor: 1)) == SteadyPlan.Step.UltrawideOff, "plain off leaves the anchor alone");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(anchor: null), sixDof: true) == SteadyPlan.Step.Wait, "waits for the anchor read");

        // The One series with the Eye goes wide without touching the anchor (OneEyeSixDofTest).
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(stereo: 1, anchor: null), sixDof: true, appAnchor: false) == SteadyPlan.Step.Leave3D, "One: to 2D with no anchor read");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(anchor: 1), sixDof: true, appAnchor: false) == SteadyPlan.Step.UltrawideOn, "One: an anchor that says on is left alone");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, anchor: null, size: 1.0), sixDof: true, appAnchor: false) == SteadyPlan.Step.ScreenMax, "One: size");
        Check.That(SteadyPlan.Next(on: true, state: GlassesState(wide: 1, anchor: null), sixDof: true, appAnchor: false) == SteadyPlan.Step.Done, "One: done without the anchor");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(wide: 1, anchor: 1), sixDof: true, appAnchor: false) == SteadyPlan.Step.UltrawideOff, "One off: Ultrawide only");
        Check.That(SteadyPlan.Next(on: false, state: GlassesState(anchor: 1), sixDof: true, appAnchor: false) == SteadyPlan.Step.Done, "One off: done, anchor untouched");

        // The whole order, as one list.
        SettingsControl.State from3D = GlassesState(stereo: 1, size: 0.8, max: 1.3);
        SteadyPlan.Step[] in6 = SteadyPlan.Steps(entering: true, sixDof: true, state: from3D);
        Check.That(in6.SequenceEqual(new[] { SteadyPlan.Step.Leave3D, SteadyPlan.Step.UltrawideOn, SteadyPlan.Step.ScreenMax, SteadyPlan.Step.AnchorOn }), "entering in 6DoF: 2D, Ultrawide, size, Spatial Anchor last");
        SteadyPlan.Step[] in3 = SteadyPlan.Steps(entering: true, sixDof: false, state: GlassesState(stereo: 1, anchor: 1, size: 0.8, max: 1.3));
        Check.That(in3.SequenceEqual(new[] { SteadyPlan.Step.AnchorOff, SteadyPlan.Step.Leave3D, SteadyPlan.Step.UltrawideOn, SteadyPlan.Step.ScreenMax }), "entering in 3DoF: Spatial Anchor off, 2D, Ultrawide, size");
        SteadyPlan.Step[] inOne = SteadyPlan.Steps(entering: true, sixDof: true, state: GlassesState(stereo: 1, anchor: 1, size: 0.8, max: 1.3), appAnchor: false);
        Check.That(inOne.SequenceEqual(new[] { SteadyPlan.Step.Leave3D, SteadyPlan.Step.UltrawideOn, SteadyPlan.Step.ScreenMax }), "entering on the One series with the Eye: no anchor set at all");
        Check.That(SteadyPlan.Steps(entering: true, sixDof: true, state: GlassesState(wide: 1, anchor: 1)).Length == 0, "already in Steady mode: nothing to send");
        Check.That(SteadyPlan.Steps(entering: true, sixDof: true, state: new SettingsControl.State()).SequenceEqual(new[] { SteadyPlan.Step.Wait }), "nothing read: the plan waits");
        SettingsControl.State held = GlassesState(wide: 1, anchor: 1);
        SteadyPlan.Step[] out6 = SteadyPlan.Steps(entering: false, sixDof: true, state: held,
                                                  before: new SteadyPlan.Before(stereo: 1, ultrawide: 0, size: 0.8, our3D: true));
        Check.That(out6.SequenceEqual(new[] { SteadyPlan.Step.AnchorOff, SteadyPlan.Step.UltrawideOff, SteadyPlan.Step.SizeBack, SteadyPlan.Step.StereoBack }), "leaving 6DoF: the reverse, then the size and 3D as before");
        SteadyPlan.Step[] out3 = SteadyPlan.Steps(entering: false, sixDof: false, state: GlassesState(wide: 1),
                                                  before: new SteadyPlan.Before(stereo: 0, ultrawide: 0, size: 1.2, our3D: false));
        Check.That(out3.SequenceEqual(new[] { SteadyPlan.Step.UltrawideOff }), "leaving 3DoF from 2D with the same size: Ultrawide off only");
        SteadyPlan.Step[] outWide = SteadyPlan.Steps(entering: false, sixDof: false, state: GlassesState(wide: 1),
                                                     before: new SteadyPlan.Before(stereo: 0, ultrawide: 1, size: 0.9, our3D: false));
        Check.That(outWide.SequenceEqual(new[] { SteadyPlan.Step.SizeBack, SteadyPlan.Step.UltrawideOff }), "Ultrawide's own earlier size goes back while still in Ultrawide");
        Check.That(SteadyPlan.Steps(entering: false, sixDof: true, state: GlassesState()).Length == 0, "leaving when not in it: nothing to send");

        // The size is only put back when it reads different and can be believed.
        Check.That(SteadyPlan.SizeToPutBack(before: 0.8, now: 1.2) == 0.8, "a changed size goes back");
        Check.That(SteadyPlan.SizeToPutBack(before: 1.2, now: 1.203) == null, "the same size is left");
        Check.That(SteadyPlan.SizeToPutBack(before: null, now: 1.2) == null, "a size never read is left");
        Check.That(SteadyPlan.SizeToPutBack(before: 7, now: 1.2) == null, "a size outside 0.5 to 1.5 is not believed");
        Check.That(SteadyPlan.SizeToPutBack(before: double.NaN, now: 1.2) == null, "not a number is not sent");

        // Ultrawide leaving by itself (SteadyReplugTest; seconds here).
        Check.That(SteadyPlan.Dropped(nowS: 100, lastDropS: null, linkOpenedS: 98) == SteadyPlanDropped.Replug, "off just after a new link is a re-plug");
        Check.That(SteadyPlan.Dropped(nowS: 100, lastDropS: 95, linkOpenedS: 99) == SteadyPlanDropped.Replug, "even with a drop moments ago");
        double old = 100 - SteadyPlan.ReplugS - 0.001;
        Check.That(SteadyPlan.Dropped(nowS: 100, lastDropS: null, linkOpenedS: old) == SteadyPlanDropped.BackIn, "on an old link it is the button: back in once");
        Check.That(SteadyPlan.Dropped(nowS: 100, lastDropS: 90, linkOpenedS: old) == SteadyPlanDropped.Leave, "a second time within 30 s: leave");
        Check.That(SteadyPlan.Dropped(nowS: 100, lastDropS: 60, linkOpenedS: old) == SteadyPlanDropped.BackIn, "a drop long ago does not count");

        // A new state is safe: not in Steady mode, nothing read.
        var fresh = new SettingsControl.State();
        Check.That(!fresh.Steady && fresh.SizeFactor == null && fresh.SpaceFollow == null && fresh.SteadyNote == null, "a new state is not in Steady mode");
    }

    private static void ControlWireChecks()
    {
        uint id = ControlWire.RpcId(0);
        Check.That(id == 0x8000_0001, "the first request id has the top bit set");
        // Frame: u16 type, u32 length, u32 id, then the Base protobuf with the request in field 3 (Pocket XR's GlassesWire:
        // { 1 = 1 } is "1a020801", an empty request "1a00").
        byte[]? wideOn = Try(() => ControlWire.UltrawideSetFrame(id, on: true));
        Check.That(Same(wideOn, HexBytes("28 26 00 00 00 08 80 00 00 01 1a 02 08 01")), "Ultrawide on (10278 = 1)");
        byte[]? wideOff = Try(() => ControlWire.UltrawideSetFrame(id, on: false));
        Check.That(Same(wideOff, HexBytes("28 26 00 00 00 06 80 00 00 01 1a 00")), "Ultrawide off is an empty request");
        byte[]? anchorOn = Try(() => ControlWire.AnchorSetFrame(id, on: true));
        Check.That(Same(anchorOn, HexBytes("28 a5 00 00 00 08 80 00 00 01 1a 02 08 01")), "Spatial Anchor on (10405 = 1)");
        byte[]? anchorOff = Try(() => ControlWire.AnchorSetFrame(id, on: false));
        Check.That(Same(anchorOff, HexBytes("28 a5 00 00 00 06 80 00 00 01 1a 00")), "Spatial Anchor off");
        byte[]? twoD = Try(() => ControlWire.StereoSetFrame(id, sbs: false));
        Check.That(Same(twoD, HexBytes("28 22 00 00 00 06 80 00 00 01 1a 00")), "2D (10274 = 0)");
        byte[]? size = Try(() => ControlWire.SizeSetFrame(id, size: 1.2f));
        Check.That(Same(size, HexBytes("28 9e 00 00 00 0b 80 00 00 01 1a 05 0d 9a 99 99 3f")), "screen size 1.2 as a float in field 1 (10398)");
        byte[]? recentre = Try(() => ControlWire.RecenterFrame(id));
        Check.That(Same(recentre, HexBytes("28 27 00 00 00 06 80 00 00 01 1a 00")), "recentre (10279) is an empty request");
        byte[]? follow = Try(() => ControlWire.FollowFrame(id));
        Check.That(Same(follow, HexBytes("28 2c 00 00 00 08 80 00 00 01 1a 02 08 01")), "follow (10284 = 1)");
        byte[]? rangeRead = Try(() => ControlWire.Rpc(ControlWire.SizeRange, id));
        Check.That(Same(rangeRead, HexBytes("28 9c 00 00 00 06 80 00 00 01 1a 00")), "the size range read (10396)");

        // What must never be sent is refused: EDID (10078 read, 10079 set), firmware (10325 to 10328), duty (10065, 10066).
        foreach (int forbidden in new[] { 10078, 10079, 10325, 10326, 10327, 10328, 10065, 10066, 10281, 12345 })
        {
            byte[]? frame = Try(() => ControlWire.Rpc(forbidden, id));
            Check.That(frame == null, $"message {forbidden} is refused");
        }
        // Everything Steady mode sends is on the allow list.
        foreach (int wanted in new[] { 10273, 10274, 10277, 10278, 10279, 10284, 10396, 10397, 10398, 10404, 10405, 10421 })
        {
            Check.That(ControlWire.Sendable.Contains(wanted), $"message {wanted} may be sent");
        }

        // The size answers: 10397's value in field 2, 10396's range in fields 2 and 3 of the answer (field 4).
        ControlWire.Answer? Answer(int type, byte[] body)
        {
            byte[] pb = ControlWire.PbMessage(4, body);
            byte[] bytes = ControlWire.Header(type, 4 + pb.Length).Concat(new byte[] { 0, 0, 0, 1 }).Concat(pb).ToArray();
            ControlWire.Answer[] answers = new ControlWire.Framer().Feed(bytes);
            return answers.Length > 0 ? answers[0] : null;
        }
        ControlWire.Answer? range = Answer(ControlWire.SizeRange, ControlWire.PbFloat(2, 0.85f).Concat(ControlWire.PbFloat(3, 1.2f)).ToArray());
        Check.That(range is ControlWire.Answer ra && Near(ra.Value ?? 0, 0.85, 1e-6) && Near(ra.Value2 ?? 0, 1.2, 1e-6) && ra.Result == 0, "the size range reads back");
        Check.That(ControlWire.BelievableSize(range?.Value2) is double largest && Near(largest, 1.2, 1e-6), "its largest is believed");
        ControlWire.Answer? sizeNow = Answer(ControlWire.SizeGet, ControlWire.PbFloat(2, 1.2f));
        Check.That(sizeNow is ControlWire.Answer sn && Near(sn.Value ?? 0, 1.2, 1e-6), "the screen size reads back");
        ControlWire.Answer? off = Answer(ControlWire.AnchorGet, Array.Empty<byte>());
        Check.That(off?.Flag == 0, "proto3 leaves a zero out: an empty answer is off");
        Check.That(ControlWire.BelievableSize(double.NaN) == null && ControlWire.BelievableSize(3) == null && ControlWire.BelievableSize(null) == null,
                   "a size that is not a number, or outside 0.5 to 1.5, is not believed");
    }

    private static void CapabilityChecks()
    {
        Glasses? Found(int product, int[]? classes = null, int vendor = 0x3318, UsbDeviceInfo[]? extra = null)
        {
            var devices = new List<UsbDeviceInfo>
            {
                new UsbDeviceInfo(vendor: vendor, product: product, interfaceClasses: classes ?? new[] { 0x02, 0x03 }, name: ""),
            };
            devices.AddRange(extra ?? Array.Empty<UsbDeviceInfo>());
            return Glasses.Detect(devices);
        }
        Glasses? aura = Found(0x0439, classes: new[] { 0x02, 0x0A, 0x03, 0x0E });
        Check.That(aura?.Family == GlassesFamily.Aura, "the Aura is found");
        Check.That(aura?.SteadyCapable == true && aura?.SixDofCapable == true && aura?.AppAnchor == true, "the Aura: Steady mode, 6DoF, its anchor set by the app");
        Check.That(aura?.HidImu == false && aura?.TrackingOnMac == true && aura?.Managed == true, "the Aura tracks over the network and is managed");

        // The XBX A01+ (a tester's, USB 3318:0442: three HID interfaces) is Air family, tracked over USB HID.
        Glasses? xbx = Found(0x0442, classes: new[] { 0x03, 0x03, 0x03 });
        Check.That(xbx?.Family == GlassesFamily.Air && xbx?.Name == "XREAL XBX A01+", "the XBX A01+ is found as Air family");
        Check.That(xbx?.HidImu == true && xbx?.NetworkImu == false && xbx?.SteadyCapable == false, "it tracks over USB HID, with no Steady mode");
        Check.That(Found(0x0440)?.HidImu == true, "and so does the XBX A01");

        foreach (int product in new[] { 0x0435, 0x0437, 0x043D, 0x0443 })
        {
            Glasses? one = Found(product);
            Check.That(one?.Family == GlassesFamily.One && one?.SteadyCapable == true, $"the One series is offered Steady mode ({product})");
            Check.That(one?.SixDofCapable == false, "without the Eye it has no 6DoF");
            Check.That(one?.AppAnchor == false && one?.Managed == false, "its anchor is the user's, and it is not managed");
            Check.That(one?.HidImu == false && one?.ControlLink == true, "it tracks over the network");
        }
        // An XREAL camera beside One series glasses is the Eye.
        var eye = new UsbDeviceInfo(vendor: 0x3318, product: 0x0450, interfaceClasses: new[] { 0x0E }, name: "");
        Glasses? oneEye = Found(0x0437, extra: new[] { eye });
        Check.That(oneEye?.Eye == true && oneEye?.SixDofCapable == true && oneEye?.AppAnchor == false, "the One with the Eye: 6DoF, turned on by the user");

        Glasses? air2 = Found(0x0428, classes: new[] { 0x03, 0x01 });
        Check.That(air2?.Family == GlassesFamily.Air && air2?.HidImu == true, "the Air 2 tracks over USB HID");
        Check.That(air2?.SteadyCapable == false && air2?.SixDofCapable == false && air2?.ControlLink == false, "the Air has no Steady mode and no control link");
        Check.That(air2?.TrackingOnMac == false, "HID tracking is the app's choice, not on by itself");
        Check.That(air2 is Glasses a2 && a2.Axes == HeadFusion.Axes.Aura, "the Air's samples are in head axes");
        Glasses? helen = Found(0x043F, classes: new[] { 0x03 });
        Check.That(helen?.Family == GlassesFamily.Air && helen?.HidImu == false, "an Air family model with no known stream is not read");

        Glasses? beast = Found(Glasses.VitureBeast, classes: new[] { 0x03 }, vendor: Glasses.Viture);
        Check.That(beast?.Family == GlassesFamily.Viture && beast?.HidImu == true && beast?.SteadyCapable == false, "the VITURE Beast: USB HID, no Steady mode");
        Check.That(beast is Glasses bg && bg.Axes == HeadFusion.Axes.One, "the Beast sends x right, y down, z front");
        // The Beast also comes as 0x1201 (a tester's, November 2026): the same rules, and it is preferred to a second VITURE id.
        Glasses? beast1201 = Found(Glasses.VitureBeast1201, classes: new[] { 0x03 }, vendor: Glasses.Viture);
        Check.That(beast1201?.IsBeast == true && beast1201?.HidImu == true && beast1201?.SteadyCapable == false, "the VITURE Beast 0x1201: USB HID, no Steady mode");
        Check.That(beast1201 is Glasses b1 && b1.Axes == HeadFusion.Axes.One, "the Beast 0x1201 sends x right, y down, z front");
        var otherViture = new UsbDeviceInfo(vendor: Glasses.Viture, product: 0x1301, interfaceClasses: new[] { 0x03 }, name: "");
        Glasses? beastFirst = Found(0x1201, classes: new[] { 0x03 }, vendor: Glasses.Viture, extra: new[] { otherViture });
        Check.That(beastFirst?.Product == 0x1201, "the Beast 0x1201 is picked over another VITURE id");
        Check.That(Glasses.IsBeastProduct(0x1211) && Glasses.IsBeastProduct(0x1201) && !Glasses.IsBeastProduct(0x1301) && !Glasses.IsBeastProduct(Glasses.VitureMicrophone), "the Beast's ids are 0x1211 and 0x1201");
        Glasses? viture = Found(0x1301, classes: new[] { 0x03 }, vendor: Glasses.Viture);
        Check.That(viture?.HidImu == true && (viture is Glasses vg && vg.Axes == HeadFusion.Axes.Aura), "other VITURE glasses: as the sensor sends");

        // Other XREAL glasses with a network interface are taken as One family for tracking, but get no settings sent.
        Glasses? unknown = Found(0x0499);
        Check.That(unknown?.Family == GlassesFamily.One && unknown?.SteadyCapable == false && unknown?.SixDofCapable == false, "unknown XREAL glasses are not offered Steady mode");
    }

    private static void ReportChecks()
    {
        UsbDeviceInfo[] usb =
        {
            new UsbDeviceInfo(vendor: 0x3318, product: 0x0439, interfaceClasses: new[] { 0x02, 0x0A, 0x03, 0x0E }, name: "XREAL Aura"),
            new UsbDeviceInfo(vendor: 0x05AC, product: 0x12A8, interfaceClasses: new[] { 0x06, 0xFF }, name: "Sam Example's iPhone"),
        };
        var state = new SettingsControl.State();
        state.Link = true;
        state.Stereo = 0;
        state.Anchor = 1;
        state.Ultrawide = 1;
        state.SizeFactor = 1.2;
        state.SizeMax = 1.2;
        state.Steady = true;
        string text = GlassesReport.Text(
            appVersion: "0.2.0 (12)", system: "macOS 14.6.1",
            glasses: Glasses.Detect(usb), usb: usb,
            links: new[] { "en7 (169.254.2.10 -> 169.254.2.1)", "en0 192.168.1.23 a4:83:e7:12:34:56" },
            displays: new[] { "\"XREAL AURA\" 3840x1080 at 60 Hz, serial number K7Q2X9P4A1" },
            state: state,
            trackingLine: "on, 1000 samples a second; log at /Users/samexample/Library/Logs/Pocket XR, sam@example.com");
        Check.That(text.Contains("XREAL Aura"), "the report names the model");
        Check.That(text.Contains("3318:0439"), "and its USB ids");
        Check.That(text.Contains("05AC:12A8"), "other USB devices are listed by their ids");
        Check.That(!text.Contains("Sam Example"), "but not by name: a name can be a person's");
        Check.That(text.Contains("169.254.2.10"), "the glasses' own link is kept");
        Check.That(!text.Contains("192.168.1.23"), "another network address is taken out");
        Check.That(!text.ToLowerInvariant().Contains("a4:83:e7:12:34:56"), "a hardware address is taken out");
        Check.That(!text.Contains("K7Q2X9P4A1"), "a serial number is taken out");
        Check.That(!text.Contains("samexample") && !text.Contains("sam@example.com"), "the user's name and email are taken out");
        Check.That(text.Contains("Steady mode: on"), "the settings read are in it");
        Check.That(text.Contains("Ultrawide: on (1)") && text.Contains("Spatial Anchor: on (1)") && text.Contains("Screen size: 1.20"), "with Ultrawide, the anchor and the size");
        Check.That(text.Contains("macOS 14.6.1") && text.Contains("0.2.0 (12)"), "the app and system versions are kept");
        Check.That(text.Contains("3840x1080"), "the display line is kept");

        string none = GlassesReport.Text(appVersion: "0.2.0", system: "macOS 14.6.1", glasses: null, usb: Array.Empty<UsbDeviceInfo>(), links: Array.Empty<string>(),
                                         displays: Array.Empty<string>(), state: null, trackingLine: "no glasses");
        Check.That(none.Contains("no glasses found"), "a report with no glasses says so");

        UsbDeviceInfo[] air = { new UsbDeviceInfo(vendor: 0x3318, product: 0x0432, interfaceClasses: new[] { 0x03, 0x01 }, name: "Air 2 Pro") };
        string airText = GlassesReport.Text(appVersion: "0.2.0", system: "macOS 14.6.1", glasses: Glasses.Detect(air), usb: air,
                                            links: Array.Empty<string>(), displays: Array.Empty<string>(), state: null, trackingLine: "not started");
        Check.That(airText.Contains("XREAL Air 2 Pro") && airText.Contains("USB HID") && airText.Contains("untested"), "an Air's report says HID, and untested on a PC");
    }
}
