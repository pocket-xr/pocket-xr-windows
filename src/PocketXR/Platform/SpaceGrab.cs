using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// Moving the Space from anywhere, hand on the mouse: hold Ctrl and Alt, and moving the pointer carries the whole
/// Space with it. The same two keys as every Pocket XR shortcut.
///
/// Windows tells an app about the mouse in other apps only through a hook into every mouse event on the PC, which
/// Pocket XR does not install. So the two keys and the pointer's place are looked at 60 times a second instead, and
/// only while there are floating screens. The wheel cannot be read that way, so resizing with Ctrl+Alt and the wheel
/// (as on the Mac) is left out: Ctrl+Alt+= and Ctrl+Alt+- do it.
/// </summary>
public sealed class SpaceGrab
{
    private readonly Func<bool> enabled;
    private readonly Action<double, double> move;
    private readonly DispatcherTimer timer;
    /// <summary>Pointer travel since the keys went down, in pixels: the Space starts to follow only past the dead
    /// zone, so the hand's small movements while pressing a shortcut do not move it.</summary>
    private double travelled;
    private (int X, int Y)? last;
    private const double DeadZone = 18.0;
    /// <summary>Degrees of turn for a pixel of pointer travel.</summary>
    private const double DegreesPerPixel = 0.045;

    public SpaceGrab(Func<bool> enabled, Action<double, double> move)
    {
        this.enabled = enabled;
        this.move = move;
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Stop() => timer.Stop();

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void Tick()
    {
        // Ctrl and Alt alone: not with Shift or the Windows key.
        bool held = Down(0x11) && Down(0x12) && !Down(0x10) && !Down(0x5B) && !Down(0x5C);
        if (!held || !enabled() || Pointer.Position is not (int x, int y))
        {
            travelled = 0;
            last = null;
            return;
        }
        if (last is not (int lx, int ly))
        {
            last = (x, y);
            return;
        }
        double dx = x - lx, dy = y - ly;
        last = (x, y);
        if (dx == 0 && dy == 0) return;
        if (travelled < DeadZone)
        {
            travelled += Math.Sqrt(dx * dx + dy * dy);
            return;
        }
        // Pointer right carries the Space right (left is positive), pointer up carries it up (y grows down).
        move(-dx * DegreesPerPixel, -dy * DegreesPerPixel);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
