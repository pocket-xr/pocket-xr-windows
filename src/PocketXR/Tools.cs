using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Windows;

namespace PocketXR;

/// <summary>Pocket XR's checks from a terminal: each one only reads, and says what it saw.</summary>
public static class Tools
{
    /// <summary>Every window drawn to PNG files in [folder]. [only] names windows to draw (control, tray, settings,
    /// tour, launcher); none means all.</summary>
    public static int RenderUi(string folder, string[] only)
    {
        Ui.Theme.Static = true;
        ShortcutChoices.Persist = false;
        var app = new Application();
        Ui.Theme.InstallResources(app);
        Directory.CreateDirectory(folder);
        bool Want(string name) => only.Length == 0 || only.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (Want("control")) Pictures.ControlPanelPictures(folder);
        // Only when named: 25 pictures of the Place pad (PadPictures).
        if (only.Contains("pad", StringComparer.OrdinalIgnoreCase)) Pictures.PadPictures(folder);
        if (Want("tray")) TrayMenu.Pictures(folder);
        if (Want("settings")) SettingsWindow.Pictures(folder);
        if (Want("tour")) TourWindow.Pictures(folder);
        if (Want("launcher")) Launcher.Pictures(folder);
        if (Want("driver")) DriverHelp.Pictures(folder);
        if (Want("pocket"))
        {
            PocketMode.Pictures(folder);
            EdgeHint.Pictures(folder);
        }
        Console.WriteLine($"Pictures are in {Path.GetFullPath(folder)}");
        return 0;
    }

    /// <summary>The glasses on USB, the displays, and the glasses' network link, as Windows has them now.</summary>
    public static int Usb()
    {
        List<UsbDeviceInfo> usb = UsbDevices.List();
        Glasses? g = Glasses.Detect(usb);
        Glasses.SetUsbName(Glasses.UsbName(usb, g));
        Console.WriteLine(g is Glasses found
            ? $"Glasses: {found.Name} ({found.Vendor:X4}:{found.Product:X4})"
            : "Glasses: none that Pocket XR knows are plugged in");
        var theirs = usb.Where(d => d.Vendor == Glasses.Xreal || d.Vendor == Glasses.Viture).ToList();
        Console.WriteLine($"USB (XREAL and VITURE devices): {UsbDevices.Describe(theirs)}");
        List<DisplayInfo> displays = Displays.List();
        Console.WriteLine("Displays:");
        foreach (DisplayInfo d in displays) Console.WriteLine($"  {d}{(Displays.IsVirtual(d) ? " (virtual)" : "")}");
        DisplayInfo? glassesDisplay = Displays.Glasses(displays);
        Console.WriteLine(glassesDisplay != null
            ? $"Glasses' display: {glassesDisplay}"
            : Displays.GlassesMirrored(displays)
                ? "Glasses' display: Windows is showing a copy of another screen on the glasses (Duplicate). Press Win+P and choose Extend."
                : "Glasses' display: none. No picture is reaching the glasses (they need a USB-C port that carries video).");
        List<GlassesNet.Route> routes = GlassesNet.Routes();
        Console.WriteLine(routes.Count > 0
            ? "Glasses' network link: " + string.Join("; ", routes.Select(r => r.Description))
            : "Glasses' network link: none (no adapter has a 169.254.2.x or 169.254.1.x address)");
        return 0;
    }

    /// <summary>Head tracking alone: reads the glasses' motion stream for [seconds] and prints where the head points
    /// ten times a second. Nothing is sent to the glasses.</summary>
    public static int ImuTest(double seconds)
    {
        Glasses? g = Glasses.Detect(UsbDevices.List());
        if (g is not Glasses glasses)
        {
            Console.WriteLine("No glasses that Pocket XR knows are plugged in.");
            return 1;
        }
        if (!glasses.NetworkImu)
        {
            Console.WriteLine($"{glasses.Name} does not send its motion over the USB network; that way of tracking is not in Pocket XR for Windows yet.");
            return 1;
        }
        Console.WriteLine($"{glasses.Name}: reading head tracking for {seconds:0} s. Turn your head; hold still for 2 s to let it learn its zero.");
        var fusion = new HeadFusion(glasses.Axes);
        var pose = new HeadPose(fusion);
        var options = new TrackingOptions(new SpaceSettings());
        var reader = new ImuReader(s => fusion.Report(s));
        reader.Start();
        double start = HeadFusion.SystemUptime, lastCount = start;
        int lastSamples = 0, lines = 0;
        bool sawData = false;
        while (HeadFusion.SystemUptime - start < seconds)
        {
            Thread.Sleep(100);
            double now = HeadFusion.SystemUptime;
            pose.DrawPose(now, 1.0 / 60, options);
            (double yaw, double pitch) = pose.YawPitchDeg;
            int samples = reader.Samples;
            double hz = (samples - lastSamples) / Math.Max(now - lastCount, 0.001);
            lastSamples = samples;
            lastCount = now;
            if (samples > 0) sawData = true;
            if (++lines % 5 == 0 || !sawData)
                Console.WriteLine(FormattableString.Invariant(
                    $"  yaw {yaw,7:0.0}°  pitch {pitch,6:0.0}°  {hz,5:0} reports/s  zero {(fusion.BiasReady ? "learnt" : "learning")}  via {reader.ConnectedVia ?? "not connected"}"));
        }
        reader.Stop();
        (int reports, int imu, int dropped, int badLength) = reader.Counts;
        double took = HeadFusion.SystemUptime - start;
        Console.WriteLine(FormattableString.Invariant(
            $"Done: {reader.Samples} motion samples in {took:0.0} s ({reader.Samples / took:0} a second), {reports} reports, {dropped} bytes skipped, {badLength} wrong lengths, zero {(fusion.BiasReady ? "learnt" : "not learnt")}."));
        Log.P($"imu-test: {reader.Samples} samples in {took:0.0} s, dropped {dropped}, bad length {badLength}, bias {(fusion.BiasReady ? "learnt" : "not learnt")}");
        return sawData ? 0 : 1;
    }

    /// <summary>
    /// A test that changes the PC's display setup for a short while: adds one floating screen, says how Windows lists
    /// it, keeps it [seconds], and takes it away again. Run it only when the person at the PC has said yes: open
    /// windows can move, and the screens can blink, as when a monitor is plugged in.
    /// </summary>
    public static int DriverTest(double seconds)
    {
        List<DisplayInfo> before = Displays.List();
        Console.WriteLine($"Before: {before.Count} displays.");
        using VirtualDisplays? made = VirtualDisplays.Make(1, 1920, 1080, 60, out string why);
        if (made == null)
        {
            Console.WriteLine($"No floating screen was added: {why}.");
            return 1;
        }
        VirtualDisplays.Screen screen = made.Screens[0];
        DisplayInfo? seen = null;
        double start = HeadFusion.SystemUptime;
        while (seen == null && HeadFusion.SystemUptime - start < 5)
        {
            Thread.Sleep(100);
            seen = VirtualDisplays.Find(screen, Displays.List());
        }
        double took = HeadFusion.SystemUptime - start;
        Console.WriteLine(seen != null
            ? FormattableString.Invariant($"Added in {took:0.0} s: {seen} (seen as virtual: {Displays.IsVirtual(seen)}; path {seen.DevicePath})")
            : "The driver said yes, but Windows did not list the new display within 5 s.");
        Log.P($"driver-test: {(seen != null ? seen.ToString() : "not listed")} path {seen?.DevicePath}");
        if (seen != null)
        {
            // The new display is captured for the time it is kept: numbers only, nothing is saved.
            try
            {
                using Render.GpuDevice gpu = Render.GpuDevice.ForDisplay(seen);
                using var capture = new Render.DisplayCapture(gpu, seen);
                capture.Start();
                Thread.Sleep((int)(Math.Max(seconds, 1) * 1000));
                Console.WriteLine(FormattableString.Invariant(
                    $"Captured: {capture.Size.Width}x{capture.Size.Height}, {capture.Frames} pictures, {capture.Fps:0} a second at the end{(capture.CopiesThroughMemory ? ", copied through memory" : "")}{(capture.Error != null ? ", error: " + capture.Error : "")}"));
                Log.P($"driver-test: captured {capture.Frames} pictures, error {capture.Error ?? "none"}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Capture of the new display failed: {e.Message}");
            }
        }
        else
        {
            Thread.Sleep((int)(seconds * 1000));
        }
        made.Dispose();
        Thread.Sleep(1500);
        List<DisplayInfo> after = Displays.List();
        Console.WriteLine($"After: {after.Count} displays{(after.Count == before.Count ? ", as before" : " (not as before!)")}.");
        return seen != null && after.Count == before.Count ? 0 : 1;
    }

    /// <summary>The virtual display driver's own details. Changes nothing: no display is added.</summary>
    /// <summary>
    /// PocketXR --screen-look-test: one virtual display of its own for a few seconds, to see what Text size and
    /// Readability can do with this PC's driver: the sizes it offers, and Windows' scale set to 125 and 150 percent and
    /// read back. Then it is removed. Changes nothing else.
    /// </summary>
    public static int ScreenLookTest(int width = 1920, int height = 1080)
    {
        using VirtualDisplays? made = VirtualDisplays.Make(1, width, height, 60, out string why, idOffset: 0x70);
        if (made == null)
        {
            Console.WriteLine($"No virtual display: {why}.");
            return 1;
        }
        DisplayInfo? d = null;
        for (int i = 0; i < 50 && d == null; i++)
        {
            d = VirtualDisplays.Find(made.Screens[0], Displays.List());
            if (d == null) System.Threading.Thread.Sleep(100);
        }
        if (d == null)
        {
            Console.WriteLine("The virtual display did not reach the desktop.");
            return 1;
        }
        Console.WriteLine($"Made: {d}");
        if (d.Width != width || d.Height != height)
        {
            int r = Displays.SetMode(d.GdiName, width, height, 60);
            System.Threading.Thread.Sleep(300);
            d = VirtualDisplays.Find(made.Screens[0], Displays.List()) ?? d;
            Console.WriteLine($"Set to {width}x{height}: {(r == 0 ? "done" : $"refused ({r})")}; now {d.Width}x{d.Height}");
            var pick = ScreenLook.Offered(width, height, Displays.Modes(d.GdiName));
            Console.WriteLine($"The host would use: {(pick is var (pw, ph) ? $"{pw}x{ph}" : "the size it has")}");
        }
        var modes = Displays.Modes(d.GdiName);
        if (Environment.GetCommandLineArgs().Contains("--portrait"))
        {
            int r = Displays.SetPortrait(d.GdiName, true);
            System.Threading.Thread.Sleep(500);
            var turned = VirtualDisplays.Find(made.Screens[0], Displays.List());
            Console.WriteLine($"Portrait: {(r == 0 ? "done" : $"refused ({r})")}; now {turned?.Width}x{turned?.Height}, turned {Displays.IsPortrait(d.GdiName)}");
            if (turned != null)
            {
                using var gpu = Render.GpuDevice.Create();
                using var cap = new Render.DisplayCapture(gpu, turned);
                cap.Start();
                for (int i = 0; i < 40 && cap.Texture == null; i++) System.Threading.Thread.Sleep(50);
                Console.WriteLine($"Capture: {(cap.Texture != null ? "pictures arriving" : "no picture yet")}, rotation {cap.Rotation} (1 upright, 2 a quarter)");
            }
            int back = Displays.SetPortrait(d.GdiName, false);
            System.Threading.Thread.Sleep(500);
            var upright = VirtualDisplays.Find(made.Screens[0], Displays.List());
            Console.WriteLine($"Upright again: {(back == 0 ? "done" : $"refused ({back})")}; now {upright?.Width}x{upright?.Height}");
        }
        Console.WriteLine($"Sizes it offers ({modes.Count}): {string.Join(", ", modes.Select(m => $"{m.Width}x{m.Height}"))}");
        foreach (var size in ScreenLook.ReadabilitySizes)
            Console.WriteLine($"Readability {size.Width}x{size.Height}: {(modes.Contains(size) ? "offered" : "not offered")}");
        Console.WriteLine($"1920x1200: {(modes.Contains((1920, 1200)) ? "offered" : "not offered")}");
        Console.WriteLine($"Scale now: {Displays.Scale(d.GdiName)?.ToString() ?? "not given"}");
        foreach (int want in new[] { 125, 150, 100 })
        {
            int? got = Displays.SetScale(d.GdiName, want);
            Console.WriteLine($"Scale {want}%: {(got is int g ? $"now {g}%" : "not set")}");
        }
        return 0;
    }

    public static int Driver()
    {
        VirtualDisplays.DriverInfo? info = VirtualDisplays.Probe(out string why);
        if (info == null)
        {
            Console.WriteLine($"Virtual display driver: {why}.");
            return 1;
        }
        Console.WriteLine($"Virtual display driver: {info.Name}, version {info.Version}{(info.TestBuild ? " (test build)" : "")}{(info.WatchdogTimeoutS > 0 ? $", watchdog {info.WatchdogTimeoutS} s" : "")}.");
        Console.WriteLine("It answered. No display was added.");
        return 0;
    }

    /// <summary>Hand tracking's detector on a picture file: the hand's 21 points, as 0 to 1 of the picture, run
    /// twice (the second time following the hand from the first, as it does from frame to frame).</summary>
    public static int HandsTest(string file)
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(Path.GetFullPath(file)),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        Hands.HandDetector? detector = Hands.HandDetector.Load(out string? error);
        if (detector == null)
        {
            Console.WriteLine($"The hand models could not be loaded: {error}");
            return 1;
        }
        for (int pass = 1; pass <= 2; pass++)
        {
            Hands.DetectedHand? hand = detector.Detect(pixels, w, h, stride);
            Console.WriteLine($"pass {pass}: {(detector.RanPalm ? "palm search" : "followed")}, palm {detector.PalmMs:0} ms, points {detector.MarksMs:0} ms");
            if (hand == null)
            {
                Console.WriteLine("  no hand");
                continue;
            }
            Console.WriteLine(FormattableString.Invariant($"  presence {hand.Presence:0.000}, MediaPipe's label {(hand.LabelRight ? "Right" : "Left")} ({hand.LabelScore:0.00})"));
            for (int n = 0; n < 21; n++) Console.WriteLine(FormattableString.Invariant($"  {n,2} {hand.Points[n].X:0.0000} {hand.Points[n].Y:0.0000}"));
            HandPoints p = hand.ToHandPoints((double)w / h, mirrored: false);
            Console.WriteLine(FormattableString.Invariant($"  pinch ratio {p.PinchRatio:0.00}, side {p.Side?.RawValue() ?? "unknown"}, palm to camera {HandPalm.FacesCamera(p)}"));
        }
        return 0;
    }

    /// <summary>Hand tracking's camera and detector alone for [seconds]: [camera] is part of a camera's name (the
    /// first that is not the glasses' when none is given). Prints what it sees five times a second.</summary>
    public static int HandsCamera(string? name, double seconds)
    {
        List<Hands.HandCameraDevice> all = Hands.HandCamera.List();
        foreach (Hands.HandCameraDevice d in all) Console.WriteLine($"camera: {d.Name}{(d.BuiltIn ? " (built in)" : "")}{(d.Glasses ? " (glasses)" : "")}");
        Hands.HandCameraDevice? device = name != null
            ? all.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            : Hands.HandCamera.Others(all).FirstOrDefault();
        if (device == null)
        {
            Console.WriteLine("No such camera.");
            return 1;
        }
        Hands.HandDetector? detector = Hands.HandDetector.Load(out string? error);
        if (detector == null)
        {
            Console.WriteLine($"The hand models could not be loaded: {error}");
            return 1;
        }
        (Hands.HandCamera? camera, Hands.HandCamera.Failure why) = Hands.HandCamera.Open(device, detector);
        if (camera == null)
        {
            Console.WriteLine($"Could not open {device.Name}: {why}");
            return 1;
        }
        double last = 0;
        int frames = 0;
        // One small picture of what the camera sees, beside the log, to look at afterwards (after 3 seconds, once
        // the camera's exposure has settled).
        new Thread(() => { Thread.Sleep(3000); camera.PreviewWanted = true; }) { IsBackground = true }.Start();
        camera.OnPreview = (pw, ph, px) =>
        {
            camera.PreviewWanted = false;
            var picture = System.Windows.Media.Imaging.BitmapSource.Create(pw, ph, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, pw * 4);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            string file = Path.Combine(Log.Shared.Folder, "hands-camera.png");
            using (FileStream f = File.Create(file)) png.Save(f);
            Console.WriteLine($"What the camera sees (mirrored): {file}");
        };
        camera.OnFrame = f =>
        {
            frames++;
            if (f.TimeMs - last < 200) return;
            last = f.TimeMs;
            string what = f.Hands.Count == 0 ? (camera.Dark ? "dark, no hand" : "no hand")
                : FormattableString.Invariant($"hand at {f.Hands[0].Centre.X:0.00}, {f.Hands[0].Centre.Y:0.00}, pinch {f.Hands[0].PinchRatio:0.00}, {f.Hands[0].Side?.RawValue() ?? "side unknown"}, palm to camera {HandPalm.FacesCamera(f.Hands[0])}");
            Console.WriteLine($"{f.TimeMs / 1000:0.0} s  {what}");
        };
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        camera.Stop();
        Console.WriteLine($"{frames} frames looked at in {seconds:0} s");
        Thread.Sleep(1000);
        return 0;
    }
}
