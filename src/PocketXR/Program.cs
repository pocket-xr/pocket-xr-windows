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
    /// PocketXR --driver             the virtual display driver's own details (changes nothing)
    /// PocketXR --quit               asks the running copy to quit and put the glasses back
    /// PocketXR --stand-in N         a test: monitor N (not the main one) stands in for the glasses' display
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
                    case "--make-icon":
                        new System.Windows.Application();
                        return AppIcon.Make(args.Length > 1 ? args[1] : "PocketXR.ico");
                    case "--driver-test": return Tools.DriverTest(args.Length > 1 && double.TryParse(args[1], out double d) ? d : 8);
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

        // One copy at a time: a second start brings the first one's control panel forward.
        using var once = new Mutex(true, "PocketXR.single", out bool first);
        if (!first)
        {
            using var wake = EventWaitHandle.OpenExisting("PocketXR.show");
            wake.Set();
            return 0;
        }
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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
