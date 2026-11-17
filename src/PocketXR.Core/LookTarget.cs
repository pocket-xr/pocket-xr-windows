using System;
using System.Collections.Generic;

namespace PocketXR.Core;

/// <summary>Which screen the head points at, for moving the pointer to the screen you look at and lighting it.</summary>
public static class LookTarget
{
    /// <summary>
    /// The screen the head points at: the index in [panels] and where on it (u, v 0...1 from the top left), or null
    /// if the head's forward ray misses every screen. [head] is the drawn pose; forward is -z. A curved screen is
    /// met on its cylinder, so it counts by its angular extent as seen from the head. Where screens overlap, the
    /// nearest one wins.
    /// </summary>
    public static (int Index, double U, double V)? Hit(Quat head, IReadOnlyList<PanelPlacement> panels)
    {
        Vec3 forward = head.Rotate(new Vec3(0, 0, -1));
        (int Index, double U, double V)? best = null;
        double bestT = double.PositiveInfinity;
        for (int i = 0; i < panels.Count; i++)
        {
            (double T, double X, double Y)? met = PanelCurve.Intersect(panels[i], direction: forward);
            if (met is null) continue;
            (double T, double X, double Y) h = met.Value;
            if (Math.Abs(h.X) > 0.5 || Math.Abs(h.Y) > 0.5) continue;
            if (h.T < bestT)
            {
                bestT = h.T;
                best = (Index: i, U: h.X + 0.5, V: 0.5 - h.Y);
            }
        }
        return best;
    }
}
