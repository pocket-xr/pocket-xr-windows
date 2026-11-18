using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using PocketXR.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace PocketXR.Hands;

// The camera side of hand tracking: finds the glasses' camera among Windows' cameras, opens it with Windows' own
// MediaCapture, and runs the hand detector (HandDetector.cs) on its frames on a thread of its own, as the Mac does
// with AVFoundation and Vision. A frame that comes while the detector is busy is let go, never queued.

/// <summary>One camera Windows lists.</summary>
public sealed record HandCameraDevice(string Id, string Name, bool BuiltIn, bool Glasses);

/// <summary>One frame's hand, as the detector found it.</summary>
public sealed record HandCameraFrame(IReadOnlyList<HandPoints> Hands, double TimeMs, double Aspect);

public sealed class HandCamera
{
    /// <summary>The names a pair of glasses' camera goes by, and the glasses makers' USB vendor numbers as Windows
    /// writes them in a camera's id (XREAL 3318, VITURE 35CA). Only such a camera is ever taken as the glasses': the
    /// PC's own faces the wrong way and is never used unasked.</summary>
    private static readonly string[] GlassesNames = { "xreal", "aura", "viture", "rokid" };
    private static readonly string[] GlassesVendors = { "vid_3318", "vid_35ca" };

    public static bool IsGlasses(string name, string id)
    {
        string n = name.ToLowerInvariant(), i = id.ToLowerInvariant();
        return GlassesNames.Any(n.Contains) || GlassesVendors.Any(i.Contains);
    }

    /// <summary>Every camera Windows has now. Waits for Windows, so not on the main thread.</summary>
    public static List<HandCameraDevice> List()
    {
        var found = new List<HandCameraDevice>();
        try
        {
            DeviceInformationCollection all = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().GetAwaiter().GetResult();
            foreach (DeviceInformation d in all)
            {
                if (!d.IsEnabled) continue;
                bool builtIn = d.EnclosureLocation?.Panel is Panel.Front or Panel.Back || d.EnclosureLocation?.InLid == true;
                found.Add(new HandCameraDevice(d.Id, d.Name, builtIn, IsGlasses(d.Name, d.Id)));
            }
        }
        catch (Exception e)
        {
            Log.P($"hands: Windows would not list its cameras: {e.Message}");
        }
        return found;
    }

    /// <summary>The camera a choice comes to now, from [all]. The glasses' camera, when the glasses have none Windows
    /// can open, falls back to the PC's own ([FellBack]). Null when there is no such camera.</summary>
    public static (HandCameraDevice? Device, bool FellBack) Resolve(HandCameraChoice choice, IReadOnlyList<HandCameraDevice> all)
    {
        HandCameraDevice? glasses = all.FirstOrDefault(d => d.Glasses);
        List<HandCameraDevice> others = Others(all);
        HandCameraDevice? pc = others.FirstOrDefault(d => d.BuiltIn) ?? others.FirstOrDefault();
        return choice.Kind switch
        {
            HandCameraChoiceKind.Glasses => glasses != null ? (glasses, false) : (pc, pc != null),
            HandCameraChoiceKind.Mac => (pc, false),
            _ => (others.FirstOrDefault(d => d.Id == choice.Id), false),
        };
    }

    /// <summary>Every camera that is not the glasses': built-in ones first. These all face the person.</summary>
    public static List<HandCameraDevice> Others(IReadOnlyList<HandCameraDevice> all) =>
        all.Where(d => !d.Glasses).OrderBy(d => d.BuiltIn ? 0 : 1).ToList();

    public string Name => device.Name;
    public string Id => device.Id;
    /// <summary>The camera faces the person (any camera but the glasses'): its picture is swapped left to right
    /// before use, so a hand moved to the right moves the pointer to the right.</summary>
    public bool Facing => !device.Glasses;
    /// <summary>The camera's width of view, degrees: the Android app's measured estimate for the XREAL Aura, a common
    /// webcam's otherwise.</summary>
    public double FovDeg => device.Glasses ? HandCameraModel.AuraFovDeg : HandCameraModel.UnknownFovDeg;
    /// <summary>The picture is black, or nearly: read by hand tracking for its status line.</summary>
    public bool Dark => Volatile.Read(ref dark);

    /// <summary>Called on the detector's thread with each frame's hand.</summary>
    public Action<HandCameraFrame>? OnFrame;
    /// <summary>A small mirrored copy of the picture (width, height, 4 bytes a pixel), about 10 times a second,
    /// while [PreviewWanted]. Called on the detector's thread.</summary>
    public Action<int, int, byte[]>? OnPreview;
    /// <summary>The camera stopped by itself (unplugged, or taken by another app). Called on any thread.</summary>
    public Action? OnLost;
    /// <summary>The settings window is showing the camera's picture.</summary>
    public volatile bool PreviewWanted;

    private readonly HandCameraDevice device;
    private readonly HandDetector detector;
    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private Thread? worker;
    private readonly AutoResetEvent arrived = new(false);
    private readonly object latestGate = new();
    private SoftwareBitmap? latest;
    private volatile bool stopped;
    private bool dark;

    private HandCamera(HandCameraDevice device, HandDetector detector)
    {
        this.device = device;
        this.detector = detector;
    }

    /// <summary>Why the camera could not be opened.</summary>
    public enum Failure { None, NotAllowed, NoOpen }

    /// <summary>Opens [device] and starts reading. Waits for Windows, so not on the main thread.</summary>
    public static (HandCamera? Camera, Failure Why) Open(HandCameraDevice device, HandDetector detector)
    {
        var camera = new HandCamera(device, detector);
        try
        {
            camera.OpenNow();
            return (camera, Failure.None);
        }
        catch (UnauthorizedAccessException)
        {
            Log.P($"hands: Windows would not let Pocket XR use {device.Name} (Settings, Privacy, Camera)");
            camera.Stop();
            return (null, Failure.NotAllowed);
        }
        catch (Exception e)
        {
            Log.P($"hands: {device.Name} could not be opened: {e.Message}");
            camera.Stop();
            return (null, Failure.NoOpen);
        }
    }

    private void OpenNow()
    {
        var c = new MediaCapture();
        capture = c;
        c.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = device.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            // Frames in the PC's memory, where the detector reads them. Its own control, so its picture size can be
            // chosen: a camera another app is using cannot be opened.
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        }).AsTask().GetAwaiter().GetResult();
        c.Failed += (_, e) =>
        {
            Log.P($"hands: {device.Name} stopped: {e.Message}");
            if (!stopped) OnLost?.Invoke();
        };
        MediaFrameSource source = c.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new InvalidOperationException("it gives no colour picture");
        // 640 x 480, or the nearest the camera offers at up to 30 a second: the detector needs no more.
        MediaFrameFormat? format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width >= 320 && f.FrameRate.Denominator > 0 && (double)f.FrameRate.Numerator / f.FrameRate.Denominator >= 14)
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - 640) + Math.Abs((int)f.VideoFormat.Height - 480))
            .ThenBy(f => Math.Abs((double)f.FrameRate.Numerator / f.FrameRate.Denominator - 30))
            .FirstOrDefault();
        if (format != null)
        {
            try
            {
                source.SetFormatAsync(format).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.P($"hands: {device.Name} kept its own picture size ({e.Message})");
            }
        }
        MediaFrameReader r = c.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().GetAwaiter().GetResult();
        r.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        r.FrameArrived += FrameArrived;
        reader = r;
        stopped = false;
        worker = new Thread(Work) { IsBackground = true, Name = "Pocket XR hands", Priority = ThreadPriority.BelowNormal };
        worker.Start();
        MediaFrameReaderStartStatus status = r.StartAsync().AsTask().GetAwaiter().GetResult();
        if (status != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"it would not start ({status})");
        var f = source.CurrentFormat;
        Log.P($"hands: opened {device.Name}, {f.VideoFormat.Width} x {f.VideoFormat.Height} {f.Subtype}, " +
              (Facing ? "facing the person, mirrored" : $"taken as {FovDeg:0} degrees wide"));
    }

    /// <summary>Lets the camera go. A frame already being looked at may still be reported: whoever listens checks
    /// that this is still its camera.</summary>
    public void Stop()
    {
        if (stopped && capture == null) return;
        stopped = true;
        arrived.Set();
        MediaFrameReader? r = reader;
        MediaCapture? c = capture;
        reader = null;
        capture = null;
        // Windows can take a moment to let a camera go: not on the main thread.
        Task.Run(() =>
        {
            try
            {
                if (r != null)
                {
                    r.FrameArrived -= FrameArrived;
                    r.StopAsync().AsTask().Wait(3000);
                    r.Dispose();
                }
                c?.Dispose();
            }
            catch (Exception e)
            {
                Log.P($"hands: closing {device.Name}: {e.Message}");
            }
            lock (latestGate)
            {
                latest?.Dispose();
                latest = null;
            }
            Log.P($"hands: {device.Name} closed");
        });
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (stopped) return;
        using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
        SoftwareBitmap? bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return;
        SoftwareBitmap copy = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        lock (latestGate)
        {
            // The one before, not yet looked at, is let go: only the newest is worth the detector's time.
            latest?.Dispose();
            latest = copy;
        }
        arrived.Set();
    }

    // MARK: the detector's thread

    private void Work()
    {
        byte[] pixels = Array.Empty<byte>();
        var clock = Stopwatch.StartNew();
        double lastUsed = -1000, lastPreview = -1000, reportStart = 0;
        int detections = 0, seen = 0, palms = 0, lightChecks = 0;
        double totalMs = 0, light = 0, spread = 0;
        while (!stopped)
        {
            arrived.WaitOne(500);
            if (stopped) break;
            SoftwareBitmap? bitmap;
            lock (latestGate)
            {
                bitmap = latest;
                latest = null;
            }
            if (bitmap == null) continue;
            double now = Now;
            using (bitmap)
            {
                // A 60 a second camera is taken at 30.
                if (now - lastUsed < 30) continue;
                lastUsed = now;
                int w = bitmap.PixelWidth, h = bitmap.PixelHeight, stride = w * 4;
                if (pixels.Length != stride * h) pixels = new byte[stride * h];
                bitmap.CopyToBuffer(pixels.AsBuffer());
                double start = Now;
                DetectedHand? hand;
                try
                {
                    hand = detector.Detect(pixels, w, h, stride);
                }
                catch (Exception e)
                {
                    Log.P($"hands: the detector failed: {e.Message}");
                    hand = null;
                }
                double took = Now - start;
                totalMs += took;
                detections++;
                if (detector.RanPalm) palms++;
                double aspect = (double)w / Math.Max(h, 1);
                var hands = new List<HandPoints>();
                if (hand != null) hands.Add(hand.ToHandPoints(aspect, Facing));
                seen += hands.Count;
                // About once a second: is there anything to see in the picture at all?
                if (lightChecks++ % 8 == 0)
                {
                    (light, spread) = Light(pixels, w, h, stride);
                    Volatile.Write(ref dark, light < 14 && spread < 30);
                }
                if (reportStart == 0) reportStart = now;
                if (now - reportStart >= 10_000)
                {
                    double seconds = (now - reportStart) / 1000;
                    Log.P($"hands: {detections / seconds:0.0} detections a second at {w} x {h}, {totalMs / Math.Max(detections, 1):0} ms each " +
                          $"(palm search on {palms}), {(double)seen / Math.Max(detections, 1):0.0} hands a frame, picture light {light:0} of 255 (spread {spread:0})");
                    detections = seen = palms = 0;
                    totalMs = 0;
                    reportStart = now;
                }
                OnFrame?.Invoke(new HandCameraFrame(hands, now, aspect));
                if (PreviewWanted && now - lastPreview >= 100)
                {
                    lastPreview = now;
                    (int pw, int ph, byte[] small) = Preview(pixels, w, h, stride, Facing);
                    OnPreview?.Invoke(pw, ph, small);
                }
            }
        }
    }

    /// <summary>Milliseconds on the same clock as the head tracking's (HeadFusion.SystemUptime).</summary>
    public static double Now => HeadFusion.SystemUptime * 1000;

    /// <summary>How light the picture is, 0 black to 255 white, and the spread between its darkest and lightest
    /// parts, from a scatter of samples. A covered camera, or a room too dark, shows here.</summary>
    private static (double Light, double Spread) Light(byte[] p, int w, int h, int stride)
    {
        if (w < 16 || h < 16) return (0, 0);
        int sum = 0, low = 255, high = 0, n = 0;
        for (int y = h / 16; y < h; y += h / 16)
        {
            for (int x = w / 16; x < w; x += w / 16)
            {
                int o = y * stride + x * 4;
                int v = (p[o] * 29 + p[o + 1] * 150 + p[o + 2] * 77) >> 8;
                sum += v; low = Math.Min(low, v); high = Math.Max(high, v); n++;
            }
        }
        return n == 0 ? (0, 0) : ((double)sum / n, high - low);
    }

    /// <summary>The picture made 216 wide, swapped left to right when the camera faces the person.</summary>
    private static (int W, int H, byte[] Pixels) Preview(byte[] p, int w, int h, int stride, bool mirror)
    {
        const int pw = 216;
        int ph = Math.Max(1, (int)Math.Round((double)h * pw / w));
        var o = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(h - 1, y * h / ph);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(w - 1, (mirror ? pw - 1 - x : x) * w / pw);
                int s = sy * stride + sx * 4, d = (y * pw + x) * 4;
                o[d] = p[s];
                o[d + 1] = p[s + 1];
                o[d + 2] = p[s + 2];
                o[d + 3] = 255;
            }
        }
        return (pw, ph, o);
    }
}
