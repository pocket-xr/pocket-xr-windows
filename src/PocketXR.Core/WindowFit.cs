using System;

namespace PocketXR.Core;

/// <summary>A window kept inside a screen's work area (the app's ScreenFit does it with these sums, in pixels).</summary>
public static class WindowFit
{
    /// <summary>The tallest the window may be, in its own units: the work area's height at its scale, less a margin,
    /// never under [least].</summary>
    public static double MaxHeight(int workTop, int workBottom, double scale, double margin = 12, double least = 320) =>
        Math.Max((workBottom - workTop) / Math.Max(scale, 0.1) - margin, least);

    /// <summary>Where a window at [left], [top], [width] x [height] goes so all of it is in the work area (top left
    /// kept in when it is bigger than the area).</summary>
    public static (int Left, int Top) Place(int left, int top, int width, int height,
        int workLeft, int workTop, int workRight, int workBottom) =>
        (Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - width)),
         Math.Clamp(top, workTop, Math.Max(workTop, workBottom - height)));
}
