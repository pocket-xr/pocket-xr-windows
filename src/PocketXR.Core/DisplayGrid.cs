using System;
using System.Collections.Generic;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// Where the system should put each floating screen's display, so the pointer moves between them the way they look in
/// the glasses: screens side by side are left and right of each other, screens stacked are above and below.
/// </summary>
/// <summary>Where this PC's own screen sits next to the floating screens on Windows' desktop (for the pointer).</summary>
public enum PcSide { Left, Below, Right, Above }

public static class PcSideExt
{
    public static readonly PcSide[] All = { PcSide.Left, PcSide.Below, PcSide.Right, PcSide.Above };
    public static string Title(this PcSide s) => s switch { PcSide.Below => "Below", PcSide.Right => "Right", PcSide.Above => "Above", _ => "Left" };
    public static string Raw(this PcSide s) => s.ToString().ToLowerInvariant();
    public static PcSide FromRaw(string? raw) => All.FirstOrDefault(s => s.Raw() == raw);
}

public static class DisplayGrid
{
    /// <summary>The floating screens' block, from their places (Origins) and sizes: its width and height.</summary>
    public static (int Width, int Height) Block(IReadOnlyList<(int X, int Y)> origins, IReadOnlyList<(int Width, int Height)> sizes)
    {
        int w = 0, h = 0;
        for (int i = 0; i < origins.Count && i < sizes.Count; i++)
        {
            w = Math.Max(w, origins[i].X + sizes[i].Width);
            h = Math.Max(h, origins[i].Y + sizes[i].Height);
        }
        return (w, h);
    }

    /// <summary>
    /// Where the floating screens' block's top left goes on the desktop, with this PC's screen ([ownX], [ownY], [ownW] x
    /// [ownH]) on [side] of it: Left (the block to its right, tops level: as before), Right (the block to its left),
    /// Below (the block above it, centred: a laptop under the glasses' screens, asked by a tester) or Above.
    /// </summary>
    public static (int X, int Y) BlockOrigin(PcSide side, int ownX, int ownY, int ownW, int ownH, int blockW, int blockH) => side switch
    {
        PcSide.Right => (ownX - blockW, ownY),
        PcSide.Below => (ownX + (ownW - blockW) / 2, ownY - blockH),
        PcSide.Above => (ownX + (ownW - blockW) / 2, ownY + ownH),
        _ => (ownX + ownW, ownY),
    };

    /// <summary>Where the glasses' display goes, out of the floating screens' way: left of this PC's screen, or right of
    /// it when the floating screens are on its left.</summary>
    public static (int X, int Y) GlassesOrigin(PcSide side, int ownX, int ownY, int ownW, int glassesW) =>
        side == PcSide.Right ? (ownX + ownW, ownY) : (ownX - glassesW, ownY);

    /// <summary>
    /// Each screen's top left corner in display points (x right, y down), from where the screens sit in the Space
    /// ([panels], one per screen, in the screens' order) and the displays' sizes. Screens at the same turn of the
    /// head form a column, upper ones first; columns run left to right and are centred on each other. The first
    /// column's top is y 0 and its left is x 0, so the caller can set it against the computer's own screen.
    /// With a count that does not match, a plain row.
    /// </summary>
    public static (int X, int Y)[] Origins(IReadOnlyList<PanelPlacement> panels, IReadOnlyList<(int Width, int Height)> sizes)
    {
        int n = sizes.Count;
        if (n <= 0) return Array.Empty<(int X, int Y)>();
        var result = new (int X, int Y)[n];
        int x = 0;
        if (panels.Count != n)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] = (X: x, Y: 0);
                x += sizes[i].Width;
            }
            return result;
        }
        // Columns: screens whose middles are closer in yaw than half the narrower one's width.
        var columns = new List<List<int>>();
        // left first (left is positive)
        List<int> order = Enumerable.Range(0, n).OrderByDescending(i => panels[i].YawDeg).ToList();
        foreach (int i in order)
        {
            if (columns.Count > 0 && columns[columns.Count - 1].Count > 0)
            {
                int j = columns[columns.Count - 1][0];
                if (Math.Abs(panels[j].YawDeg - panels[i].YawDeg) < HeadMath.Min(panels[j].WidthDeg, panels[i].WidthDeg) / 2)
                {
                    columns[columns.Count - 1].Add(i);
                    continue;
                }
            }
            columns.Add(new List<int> { i });
        }
        for (int c = 0; c < columns.Count; c++)
        {
            // upper first (up is positive)
            columns[c] = columns[c].OrderByDescending(i => panels[i].PitchDeg).ToList();
        }
        int[] heights = columns.Select(column => column.Aggregate(0, (sum, i) => sum + sizes[i].Height)).ToArray();
        int tallest = heights.Length > 0 ? heights.Max() : 0;
        int firstTop = (tallest - (heights.Length > 0 ? heights[0] : 0)) / 2;
        for (int c = 0; c < columns.Count; c++)
        {
            int y = (tallest - heights[c]) / 2 - firstTop;
            int widest = 0;
            foreach (int i in columns[c])
            {
                result[i] = (X: x, Y: y);
                y += sizes[i].Height;
                widest = Math.Max(widest, sizes[i].Width);
            }
            x += widest;
        }
        return result;
    }
}
