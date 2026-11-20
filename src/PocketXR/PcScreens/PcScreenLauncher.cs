using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PocketXR.Core;
using PocketXR.Platform;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens from the app: each "Open a PC screen" starts the next PC screen (1 to 3) as its own process, over the
/// floating screen of the same number when there is one (a borderless window filling it, so it shows in the glasses
/// as that screen), else as an ordinary window. A PC screen already open is brought forward instead of opened twice.
/// </summary>
public static class PcScreenLauncher
{
    private static readonly Process?[] running = new Process?[PcScreenLogic.Count];

    /// <summary>Whether Moonlight's core is beside the app (a build without native/moonlight has none).</summary>
    public static bool Available => File.Exists(Path.Combine(AppContext.BaseDirectory, "moonlight-pxr.dll"));

    public static int OpenCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < running.Length; i++) if (Alive(i)) n++;
            return n;
        }
    }

    private static bool Alive(int slot)
    {
        var p = running[slot];
        if (p == null) return false;
        try { return !p.HasExited; } catch (InvalidOperationException) { return false; }
    }

    /// <summary>Opens the next PC screen not yet open. <paramref name="floating"/>: the floating screens, in order.
    /// Returns the PC screen's number, or 0 when all three are open.</summary>
    public static int OpenNext(IReadOnlyList<DisplayInfo> floating)
    {
        for (int slot = 0; slot < PcScreenLogic.Count; slot++)
        {
            if (Alive(slot)) continue;
            var args = new List<string> { "--pc-screen", (slot + 1).ToString() };
            if (slot < floating.Count)
            {
                var d = floating[slot];
                args.AddRange(new[] { "--at", d.X.ToString(), d.Y.ToString(), d.Width.ToString(), d.Height.ToString() });
            }
            var start = new ProcessStartInfo(Environment.ProcessPath ?? "PocketXR.exe") { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            running[slot] = Process.Start(start);
            Log.P($"pc screens: opened PC screen {slot + 1} ({string.Join(' ', args)})");
            return slot + 1;
        }
        Log.P("pc screens: all three are open");
        return 0;
    }

    /// <summary>Closes every PC screen (each ends its session on its host as it closes).</summary>
    public static void CloseAll()
    {
        for (int slot = 0; slot < running.Length; slot++)
        {
            var p = running[slot];
            if (p == null) continue;
            try
            {
                if (!p.HasExited) p.CloseMainWindow();
            }
            catch (InvalidOperationException) { }
            running[slot] = null;
        }
    }
}
