using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using PocketXR.Core;

namespace PocketXR;

public static class Program
{
    /// <summary>
    /// PocketXR                      the app, in the notification area
    /// PocketXR --render-ui FOLDER   every window drawn to PNG files, with no glasses and no window
    /// PocketXR --usb                the USB devices and displays Windows has, and the glasses found among them
    /// PocketXR --imu-test [SECONDS] head tracking alone: yaw and pitch ten times a second
    /// PocketXR --hands-test FILE     hand tracking's detector on a picture: the hand's points
    /// PocketXR --hands-camera [SECONDS] [NAME]  the camera and detector alone, what they see
    /// PocketXR --driver             the virtual display driver's own details (changes nothing)
    /// PocketXR --quit               asks the running copy to quit and put the glasses back
    /// PocketXR --stand-in N         a test: monitor N (not the main one) stands in for the glasses' display
    /// PocketXR --pc-screen N [--host ADDRESS[:PORT]] [--at X Y W H]  PC screen N: another computer's desktop, streamed
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal) && args[0] != "--resume-steady" && args[0] != "--stand-in" && args[0] != "--quit")
        {
            // From a terminal, print there. When the output already goes somewhere (a pipe, as over SSH, or a file),
            // attaching to a console would take it away from there.
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            Log.Shared.Echo = args[0] != "--render-ui";
            try
            {
                switch (args[0])
                {
                    case "--render-ui":
                        return Tools.RenderUi(args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "pocket-xr-pictures"),
                            args.Skip(2).ToArray());
                    case "--usb": return Tools.Usb();
                    case "--imu-test": return Tools.ImuTest(args.Length > 1 && double.TryParse(args[1], out double s) ? s : 20);
                    case "--render-space":
                        return Render.RenderTools.RenderSpace(args.Length > 1 ? args[1] : "pocket-xr-space.png", args.Skip(2).ToArray());
                    case "--capture-test":
                        return Render.RenderTools.CaptureTest(args.Length > 1 && double.TryParse(args[1], out double c) ? c : 3);
                    case "--driver": return Tools.Driver();
                    case "--screen-look-test":
                        return args.Length > 2 && int.TryParse(args[1], out int tw) && int.TryParse(args[2], out int th)
                            ? Tools.ScreenLookTest(tw, th) : Tools.ScreenLookTest();
                    case "--hands-test": return Tools.HandsTest(args[1]);
                    case "--hands-camera":
                        return Tools.HandsCamera(args.Length > 2 ? args[2] : null, args.Length > 1 && double.TryParse(args[1], out double hs) ? hs : 15);
                    case "--make-icon":
                        new System.Windows.Application();
                        return AppIcon.Make(args.Length > 1 ? args[1] : "PocketXR.ico");
                    case "--driver-test": return Tools.DriverTest(args.Length > 1 && double.TryParse(args[1], out double d) ? d : 8);
                    case "--pc-screen": return PcScreens.PcScreenSession.Run(args);
                    case "--host": return PcHost.HostServer.RunCommand(args);
                    case "--host-encoder-test": return PcHost.HostServer.EncoderTest(args);
                    case "--host-clipboard-test": return PcHost.HostServer.ClipboardTest(args);
                    case "--host-audio-test": return PcHost.HostServer.AudioTest(args);
                    case "--host-pin": return PcHost.HostServer.GivePin(args);
                    case "--host-stop": return PcHost.HostServer.StopCommand();
                    case "--pc-screen-tls": return PcScreens.PcScreenSession.TlsReport(args);
                    case "--pc-screens-find": return PcScreens.PcScreenSession.FindReport();
                    default:
                        Console.Error.WriteLine($"Pocket XR does not know {args[0]}.");
                        return 2;
                }
            }
            catch (Exception e)
            {
                Log.P($"{args[0]}: failed: {e}");
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        // PocketXR --quit: asks the running copy to quit as its own Quit does (the glasses are put back first).
        if (args.Length > 0 && args[0] == "--quit")
        {
            try
            {
                using var quit = EventWaitHandle.OpenExisting("PocketXR.quit");
                quit.Set();
                return 0;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return 1;
            }
        }

        // One copy at a time, and the newest: a second start of the same copy brings its control panel forward; another
        // version (or another file, or a copy from before this, which keeps no record) is asked to quit, and this one
        // takes over (RunningCopy).
        using var once = new Mutex(true, "PocketXR.single", out bool first);
        if (!first && !TakeOver(once)) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.Theme.InstallResources(app);
        int standInAt = Array.IndexOf(args, "--stand-in");
        string? standIn = standInAt >= 0 && standInAt + 1 < args.Length ? args[standInAt + 1] : null;
        var controller = new AppController(app, resumeSteady: args.Contains("--resume-steady"), standIn);
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.P($"unhandled: {e.Exception}");
            e.Handled = true;
        };
        app.Startup += (_, _) => controller.Start();
        app.Exit += (_, _) => controller.Dispose();
        return app.Run();
    }

    /// <summary>The running copy's record (RunningCopy), kept while Pocket XR runs.</summary>
    public static string RunningFile => Path.Combine(Log.Shared.Folder, "running.txt");

    /// <summary>
    /// A second start: the same copy is brought forward (false: this one stops); another is asked to quit, and this one
    /// waits for it (up to 45 s: it puts the glasses back first) and goes on (true). If it does not quit, says so.
    /// </summary>
    private static bool TakeOver(Mutex once)
    {
        string me = Environment.ProcessPath ?? "";
        string myVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";
        string? text = null;
        try { text = File.ReadAllText(RunningFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var record = RunningCopy.Read(text);
        bool alive = false;
        if (record is var (_, _, pid))
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; }
            catch (ArgumentException) { }
        }
        if (RunningCopy.Decide(record, alive, myVersion, me) == RunningCopy.Choice.ShowRunning)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting("PocketXR.show");
                wake.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return false;
        }
        string running = RunningCopy.Describe(record, alive);
        Log.P($"start: Pocket XR {running} is running; asking it to quit so this copy ({myVersion}, {me}) takes over");
        try
        {
            using var quit = EventWaitHandle.OpenExisting("PocketXR.quit");
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        bool got;
        try { got = once.WaitOne(TimeSpan.FromSeconds(45)); }
        catch (AbandonedMutexException) { got = true; } // it ended without letting go: it is gone
        if (got)
        {
            Log.P($"start: Pocket XR {running} quit; this copy carries on");
            return true;
        }
        Log.P($"start: Pocket XR {running} did not quit within 45 s; this copy stops");
        MessageBox.Show($"Pocket XR {running} is still running in the notification area, so this copy ({myVersion}) cannot start.\n\n" +
            "Right-click Pocket XR's glasses icon by the clock, choose \"Quit and put the glasses back\", then open this copy again.",
            "Pocket XR", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
