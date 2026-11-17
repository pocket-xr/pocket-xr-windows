using System;
using System.Collections.Generic;
using System.Linq;

namespace PocketXR.Core;

/// <summary>
/// Where the system should put each floating screen's display, so the pointer moves between them the way they look in
/// the glasses: screens side by side are left and right of each other, screens stacked are above and below.
/// </summary>
public static class DisplayGrid
{
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
