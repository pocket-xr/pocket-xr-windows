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
}
