using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR;

/// <summary>
/// Pocket XR's app icon, drawn in its look: a dark tile with the glasses mark in light. `PocketXR --make-icon FILE`
/// writes it as an .ico (PNG pictures inside, as Windows Vista and later read) for the build to put in the .exe, and a
/// PNG beside it for review.
/// </summary>
public static class AppIcon
{
    private static readonly int[] Sizes = { 256, 64, 48, 40, 32, 24, 20, 16 };

    /// <summary>The icon at [size] pixels.</summary>
    public static BitmapSource Draw(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double s = size / 32.0;
            // The tile: page to card, top to bottom, a hairline rim. Small sizes fill more of the square.
            double inset = size >= 48 ? 2 * s : 0.5 * s, radius = size >= 48 ? 7 * s : 5 * s;
            var tile = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
            var fill = new LinearGradientBrush(Theme.C(Look.GlassTop), Theme.C(Look.Page), 90);
            dc.DrawRoundedRectangle(fill, new Pen(Theme.White(0.16), Math.Max(1, 0.75 * s)), tile, radius, radius);
            // The glasses: two rounded lenses and a bridge, the lenses lit with the accent.
            double stroke = Math.Max(1.25, 1.6 * s);
            var pen = new Pen(Brushes.White, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            Brush lens = Theme.B(Look.Accent.Alpha(0.85));
            var left = new Rect(5 * s, 11.5 * s, 9.5 * s, 8.5 * s);
            var right = new Rect(17.5 * s, 11.5 * s, 9.5 * s, 8.5 * s);
            dc.DrawRoundedRectangle(lens, pen, left, 3 * s, 3 * s);
            dc.DrawRoundedRectangle(lens, pen, right, 3 * s, 3 * s);
            dc.DrawLine(pen, new Point(14.5 * s, 14.5 * s), new Point(17.5 * s, 14.5 * s));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Writes [path] (.ico) and a 256 px PNG beside it.</summary>
    public static int Make(string path)
    {
        var pngs = new List<byte[]>();
        foreach (int size in Sizes)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Draw(size)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pngs.Add(ms.ToArray());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = new BinaryWriter(File.Create(path)))
        {
            // ICONDIR, then one ICONDIRENTRY per picture, then the pictures.
            file.Write((ushort)0);
            file.Write((ushort)1);
            file.Write((ushort)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int size = Sizes[i];
                file.Write((byte)(size >= 256 ? 0 : size));
                file.Write((byte)(size >= 256 ? 0 : size));
                file.Write((byte)0);
                file.Write((byte)0);
                file.Write((ushort)1);
                file.Write((ushort)32);
                file.Write(pngs[i].Length);
                file.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] png in pngs) file.Write(png);
        }
        File.WriteAllBytes(Path.ChangeExtension(path, ".png"), pngs[0]);
        Console.WriteLine($"Icon written: {path}");
        return 0;
    }
}
