using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PocketXR.Render;

/// <summary>
/// The renderer and the capture run from a terminal, with no glasses:
/// --render-space OUT.png [--sbs] [--curve X] [--screens N] [--yaw D] [--pitch D] [--layout row|mainSide|stacked|theater]
/// draws the Space with made-up test pictures into a PNG, and --capture-test SECONDS counts this PC's displays'
/// pictures a second (numbers only: nothing that is on a screen is saved or printed).
/// </summary>
public static class RenderTools
{
    // MARK: - --render-space

    /// <summary>
    /// Draws the Space as the glasses would get it (1920x1080, or 3840x1080 with --sbs) and saves it as a PNG.
    /// Each screen shows a grid, its number, a red corner with an F at its top left and a green corner at its top
    /// right, so a flipped or mirrored picture shows at once. Also taken: --focus N (the lit screen, 1 is the
    /// first), --blank N (that screen has no picture yet), --fade off|soft|wide, --mono, --width DEGREES,
    /// --glow 0..1, --brightness 0.3..1. --yaw and --pitch turn the head: left and up are positive.
    /// </summary>
    public static int RenderSpace(string outPath, string[] options)
    {
        bool sbs = false, mono = false;
        double curve = 0, yaw = 0, pitch = 0, widthDeg = SpaceLayout.DefaultWidthDeg, glow = 1, brightness = 1;
        int screens = 3;
        int? focus = null, blank = null;
        ArrangeLayout layout = ArrangeLayout.Row;
        EdgeFade fade = EdgeFade.Soft;
        for (int i = 0; i < options.Length; i++)
        {
            string a = options[i];
            string Next() => i + 1 < options.Length ? options[++i] : throw new ArgumentException($"{a} needs a value");
            double Number() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--sbs": sbs = true; break;
                case "--mono": mono = true; break;
                case "--curve": curve = Number(); break;
                case "--screens": screens = (int)Number(); break;
                case "--yaw": yaw = Number(); break;
                case "--pitch": pitch = Number(); break;
                case "--width": widthDeg = Number(); break;
                case "--glow": glow = Number(); break;
                case "--brightness": brightness = Number(); break;
                case "--focus": focus = (int)Number() - 1; break;
                case "--blank": blank = (int)Number() - 1; break;
                case "--layout":
                    layout = ArrangeLayoutExt.FromRawValue(Next()) ?? throw new ArgumentException("--layout is row, mainSide, stacked or theater");
                    break;
                case "--fade":
                    fade = EdgeFadeExt.FromRawValue(Next()) ?? throw new ArgumentException("--fade is off, soft or wide");
                    break;
                default: throw new ArgumentException($"--render-space does not know {a}");
            }
        }
        screens = Math.Clamp(screens, 1, 3);
        int width = sbs ? 3840 : 1920, height = 1080;

        using GpuDevice gpu = GpuDevice.Create();
        using var renderer = new Renderer(gpu);
        var aspects = new double[screens];
        Array.Fill(aspects, 16.0 / 9.0);
        PanelPlacement[] placements = SpaceLayout.Arrange(layout, aspects, widthDeg, SpaceLayout.DefaultDistance, curve);
        var textures = new List<ID3D11Texture2D>();
        var views = new List<ID3D11ShaderResourceView>();
        var panels = new List<(PanelPlacement Placement, ID3D11ShaderResourceView? Texture)>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                if (blank == i)
                {
                    panels.Add((placements[i], null));
                    continue;
                }
                ID3D11Texture2D t = MakeTexture(gpu, TestPattern(i + 1, 1920, 1080), 1920, 1080);
                textures.Add(t);
                ID3D11ShaderResourceView v = gpu.Device.CreateShaderResourceView(t);
                views.Add(v);
                panels.Add((placements[i], v));
            }
            Quat head = Quat.AboutY(yaw * Math.PI / 180) * Quat.AboutX(pitch * Math.PI / 180);
            var look = new RenderOptions { Focused = focus, EdgeFade = fade, Mono = mono, Glow = glow, Brightness = brightness };
            byte[] picture = DrawOffscreen(renderer, width, height, head, panels, look);
            WritePng(outPath, picture, width, height);

            bool twoEyes = !mono && GlassesOptics.IsSideBySide(width, height);
            Console.WriteLine(FormattableString.Invariant(
                $"{outPath}: {width}x{height}, {(twoEyes ? "two eyes side by side" : "one picture")}, {screens} screen(s), layout {layout.RawValue()}, curve {curve}, head yaw {yaw} pitch {pitch}, on {gpu.AdapterName}"));
            // Where each screen's middle should be in the picture, worked out apart from the graphics card.
            int eyeWidth = twoEyes ? width / 2 : width;
            for (int i = 0; i < placements.Length; i++)
            {
                PanelPlacement p = placements[i];
                string line = FormattableString.Invariant($"  screen {i + 1}: yaw {p.YawDeg:0.##} pitch {p.PitchDeg:0.##} width {p.WidthDeg:0.##} deg, {p.Distance:0.##} m");
                for (int eye = 0; eye < (twoEyes ? 2 : 1); eye++)
                {
                    double eyeX = twoEyes ? (eye == 0 ? -look.Ipd / 2 : look.Ipd / 2) : 0;
                    Mat4 vp = SpaceMath.Perspective(look.FovYDeg, (double)eyeWidth / height) * SpaceMath.View(head, eyeX);
                    Vec3 c = PanelCurve.Point(p, 0, 0);
                    Vec4f clip = vp * new Vec4f((float)c.X, (float)c.Y, (float)c.Z, 1);
                    if (clip.W <= 0) { line += ", behind the head"; continue; }
                    double px = eye * eyeWidth + (clip.X / clip.W + 1) / 2 * eyeWidth;
                    double py = (1 - clip.Y / clip.W) / 2 * height;
                    line += FormattableString.Invariant($", {(twoEyes ? (eye == 0 ? "left eye" : "right eye") : "middle")} at {px:0},{py:0}");
                    int ix = (int)Math.Round(px), iy = (int)Math.Round(py);
                    if (ix >= eye * eyeWidth && ix < (eye + 1) * eyeWidth && iy >= 0 && iy < height)
                    {
                        int o = (iy * width + ix) * 4;
                        line += $" (drawn there: r{picture[o + 2]} g{picture[o + 1]} b{picture[o]})";
                    }
                    else
                    {
                        line += " (outside the picture)";
                    }
                }
                Console.WriteLine(line);
            }
        }
        finally
        {
            foreach (var v in views) v.Dispose();
            foreach (var t in textures) t.Dispose();
        }
        return 0;
    }

    /// <summary>One frame drawn into a texture off screen and read back: blue, green, red, alpha per pixel, top row first.</summary>
    internal static byte[] DrawOffscreen(Renderer renderer, int width, int height, Quat head,
        IReadOnlyList<(PanelPlacement Placement, ID3D11ShaderResourceView? Texture)> panels, RenderOptions options)
    {
        GpuDevice gpu = renderer.Gpu;
        using ID3D11Texture2D target = gpu.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using ID3D11RenderTargetView view = gpu.Device.CreateRenderTargetView(target);
        renderer.Draw(view, width, height, head, panels, options);
        return ReadBack(gpu, target);
    }

    /// <summary>A texture's pixels, through a staging copy: 4 bytes a pixel, top row first.</summary>
    internal static byte[] ReadBack(GpuDevice gpu, ID3D11Texture2D texture)
    {
        Texture2DDescription d = texture.Description;
        int width = (int)d.Width, height = (int)d.Height;
        using ID3D11Texture2D staging = gpu.Device.CreateTexture2D(new Texture2DDescription(
            d.Format, d.Width, d.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        var pixels = new byte[width * height * 4];
        using (gpu.Hold())
        {
            gpu.Context.CopyResource(staging, texture);
            MappedSubresource map = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), pixels, y * width * 4, width * 4);
                }
            }
            finally
            {
                gpu.Context.Unmap(staging, 0);
            }
        }
        return pixels;
    }

    internal static ID3D11Texture2D MakeTexture(GpuDevice gpu, byte[] bgra, int width, int height)
    {
        ID3D11Texture2D t = gpu.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
        using (gpu.Hold()) gpu.Context.UpdateSubresource<byte>(bgra.AsSpan(), t, 0, (uint)(width * 4), 0);
        return t;
    }

    // MARK: - The test picture

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." },
        ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
    };

    /// <summary>
    /// Screen [number]'s made-up picture, blue, green, red, alpha: a grid on the screen's own colour (1 blue, 2
    /// green, 3 red-brown), a light border, its number in the middle, a red square with a white F at the top left
    /// and a green square at the top right.
    /// </summary>
    internal static byte[] TestPattern(int number, int width, int height)
    {
        var p = new byte[width * height * 4];
        (byte R, byte G, byte B) ground = number switch
        {
            1 => (0x20, 0x40, 0x78),
            2 => (0x1C, 0x60, 0x40),
            _ => (0x78, 0x34, 0x2C),
        };
        void Fill(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (int yy = Math.Max(y, 0); yy < Math.Min(y + h, height); yy++)
            {
                for (int xx = Math.Max(x, 0); xx < Math.Min(x + w, width); xx++)
                {
                    int o = (yy * width + xx) * 4;
                    p[o] = b; p[o + 1] = g; p[o + 2] = r; p[o + 3] = 255;
                }
            }
        }
        void Glyph(char c, int x, int y, int cell, byte r, byte g, byte b)
        {
            string[] rows = Glyphs[c];
            for (int gy = 0; gy < rows.Length; gy++)
            {
                for (int gx = 0; gx < rows[gy].Length; gx++)
                {
                    if (rows[gy][gx] == '#') Fill(x + gx * cell, y + gy * cell, cell, cell, r, g, b);
                }
            }
        }
        Fill(0, 0, width, height, ground.R, ground.G, ground.B);
        byte lr = (byte)(ground.R + 0x38), lg = (byte)(ground.G + 0x38), lb = (byte)(ground.B + 0x38);
        for (int x = 0; x < width; x += 120) Fill(x - 1, 0, 2, height, lr, lg, lb);
        for (int y = 0; y < height; y += 120) Fill(0, y - 1, width, 2, lr, lg, lb);
        Fill(0, 0, width, 8, 0xD8, 0xD8, 0xD8);
        Fill(0, height - 8, width, 8, 0xD8, 0xD8, 0xD8);
        Fill(0, 0, 8, height, 0xD8, 0xD8, 0xD8);
        Fill(width - 8, 0, 8, height, 0xD8, 0xD8, 0xD8);
        Fill(0, 0, 200, 200, 0xE0, 0x28, 0x28);
        Glyph('F', 50, 30, 20, 0xFF, 0xFF, 0xFF);
        Fill(width - 120, 0, 120, 120, 0x30, 0xD0, 0x50);
        int cell = height / 14;
        Glyph((char)('0' + Math.Clamp(number, 1, 3)), (width - 5 * cell) / 2, (height - 7 * cell) / 2, cell, 0xFF, 0xFF, 0xFF);
        return p;
    }

    // MARK: - --capture-test

    /// <summary>
    /// Captures every display of this PC for [seconds] and prints each one's size and pictures a second. Numbers
    /// only: no picture is saved, shown or described. A display where nothing moves gives few pictures, since
    /// Windows only sends a new one when something changed. [throughMemory] takes the two graphics card path on one.
    /// </summary>
    public static int CaptureTest(double seconds, bool throughMemory = false)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        List<DisplayInfo> displays = Displays.List();
        if (displays.Count == 0)
        {
            Console.WriteLine("No displays.");
            return 1;
        }
        using GpuDevice gpu = GpuDevice.Create();
        Console.WriteLine($"Capturing {displays.Count} display(s) for {seconds:0.#} s on {gpu.AdapterName}{(throughMemory ? ", through memory" : "")}.");
        var captures = new List<DisplayCapture>();
        try
        {
            foreach (DisplayInfo d in displays)
            {
                var c = new DisplayCapture(gpu, d) { ForceCopyThroughMemory = throughMemory };
                c.Start();
                captures.Add(c);
            }
            var best = new double[captures.Count];
            var firstAt = new double[captures.Count];
            Array.Fill(firstAt, -1);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(20);
                for (int i = 0; i < captures.Count; i++)
                {
                    best[i] = Math.Max(best[i], captures[i].Fps);
                    if (firstAt[i] < 0 && captures[i].Texture != null) firstAt[i] = clock.Elapsed.TotalSeconds;
                }
            }
            double ran = clock.Elapsed.TotalSeconds;
            bool all = true;
            for (int i = 0; i < captures.Count; i++)
            {
                DisplayCapture c = captures[i];
                DisplayInfo d = displays[i];
                var size = c.Size;
                var pointer = c.PointerShapes;
                bool got = c.Texture != null;
                all &= got;
                Console.WriteLine(FormattableString.Invariant(
                    $"  {d.GdiName}: display {d.Width}x{d.Height} at {d.RefreshHz:0.#} Hz, picture {size.Width}x{size.Height}, ")
                    + (got ? FormattableString.Invariant($"first after {firstAt[i]:0.00} s, ") : "no picture, ")
                    + FormattableString.Invariant($"{c.Frames} pictures in {ran:0.0} s ({c.Frames / ran:0.0} a second, {best[i]:0.0} in the best second), ")
                    + $"pointer shapes {pointer.Count}" + (pointer.Count > 0 ? $" (kind {pointer.Type}, {pointer.Width}x{pointer.Height})" : "")
                    + (c.CopiesThroughMemory ? ", copied through memory" : ", no copy through memory")
                    + (c.Error != null ? $", error: {c.Error}" : ""));
            }
            return all ? 0 : 1;
        }
        finally
        {
            foreach (DisplayCapture c in captures) c.Dispose();
        }
    }

    // MARK: - Checks that show a little or nothing

    /// <summary>
    /// The glasses' window and its drawing loop, on a small ordinary window (480x270, not topmost, never full
    /// screen) in the main display's bottom right corner for at most 5 seconds, showing the test pictures. Prints the
    /// frames a second it reached. Half way it is made smaller, to see the swap chain follow. [capture] also
    /// captures every display meanwhile (not shown, counted only), to see neither holds the other up.
    /// </summary>
    public static int WindowTest(double seconds, bool layered = false, bool capture = false, bool throughMemory = false)
    {
        seconds = Math.Clamp(seconds, 1, 5);
        const int width = 480, height = 270;
        List<DisplayInfo> displays = Displays.List();
        DisplayInfo? main = Displays.Own(displays);
        if (main == null)
        {
            Console.WriteLine("No display.");
            return 1;
        }
        int x = main.X + main.Width - width - 60, y = main.Y + main.Height - height - 120;
        using GpuDevice gpu = GpuDevice.ForDisplay(main);
        using var renderer = new Renderer(gpu);
        PanelPlacement[] placements = SpaceLayout.Arrange(ArrangeLayout.Row, new[] { 16.0 / 9, 16.0 / 9, 16.0 / 9 },
            SpaceLayout.DefaultWidthDeg, SpaceLayout.DefaultDistance, 0);
        var textures = new List<ID3D11Texture2D>();
        var panels = new List<(PanelPlacement Placement, ID3D11ShaderResourceView? Texture)>();
        var captures = new List<DisplayCapture>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                ID3D11Texture2D t = MakeTexture(gpu, TestPattern(i + 1, 1920, 1080), 1920, 1080);
                textures.Add(t);
                panels.Add((placements[i], gpu.Device.CreateShaderResourceView(t)));
            }
            if (capture)
            {
                foreach (DisplayInfo d in displays)
                {
                    var c = new DisplayCapture(gpu, d) { ForceCopyThroughMemory = throughMemory };
                    c.Start();
                    captures.Add(c);
                }
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long asked = 0;
            var look = new RenderOptions { Focused = 1 };
            FrameInput Frame()
            {
                Interlocked.Increment(ref asked);
                double t = clock.Elapsed.TotalSeconds;
                return new FrameInput(Quat.AboutY(Math.Sin(t * 1.5) * 0.5), panels, look);
            }
            string? lost = null;
            using (GlassesWindow window = GlassesWindow.Windowed(x, y, width, height, renderer, Frame, layered))
            {
                window.Lost += why => lost = why;
                double best = 0, bestCapture = 0;
                bool resized = false;
                (int Width, int Height) first = (0, 0);
                IntPtr under = IntPtr.Zero;
                while (clock.Elapsed.TotalSeconds < seconds && window.Running)
                {
                    Thread.Sleep(50);
                    best = Math.Max(best, window.Fps);
                    foreach (DisplayCapture c in captures) bestCapture = Math.Max(bestCapture, c.Fps);
                    if (!resized && clock.Elapsed.TotalSeconds >= seconds / 2)
                    {
                        resized = true;
                        first = window.Size;
                        under = WindowFromPoint(new Point { X = x + width / 2, Y = y + height / 2 });
                        SetWindowPos(window.Handle, IntPtr.Zero, x + 96, y + 54, 384, 216, 0x10 | 0x4); // no activate, no z order
                    }
                }
                double ran = clock.Elapsed.TotalSeconds;
                var last = window.Size;
                Console.WriteLine(FormattableString.Invariant(
                    $"Test window ({(layered ? "layered" : "not layered")}, not topmost) on {main.GdiName} ({main.RefreshHz:0.#} Hz), {gpu.AdapterName}: ")
                    + FormattableString.Invariant($"{window.Frames} frames in {ran:0.0} s ({window.Frames / ran:0.0} a second, {best:0.0} in the best second), ")
                    + $"pose asked {Interlocked.Read(ref asked)} times, picture {first.Width}x{first.Height} then {last.Width}x{last.Height} after the window was made smaller, "
                    + $"a click on it would go to {(under == window.Handle ? "this window" : "what is under it")}"
                    + (lost != null ? $", stopped by itself: {lost}" : ""));
                foreach (DisplayCapture c in captures)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"  meanwhile {c.Display.GdiName}: {c.Frames} pictures ({c.Frames / ran:0.0} a second, {c.Fps:0.0} in the last second)")
                        + (c.Error != null ? $", error: {c.Error}" : ""));
                }
            }
            return lost == null ? 0 : 1;
        }
        finally
        {
            foreach (DisplayCapture c in captures) c.Dispose();
            foreach (var p in panels) p.Texture?.Dispose();
            foreach (var t in textures) t.Dispose();
        }
    }

    /// <summary>
    /// The pointer drawn into a picture, with made-up shapes of the three kinds Windows has (monochrome, colour,
    /// masked colour) over four grounds (black, white, a colour, grey), checked number by number and saved eight
    /// times enlarged to [outPath]. No screen is read.
    /// </summary>
    public static int PointerTest(string outPath)
    {
        const int width = 160, height = 128, band = 32;
        (byte R, byte G, byte B)[] grounds = { (0, 0, 0), (255, 255, 255), (64, 128, 200), (128, 128, 128) };
        var ground = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var g = grounds[y / band];
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                ground[o] = g.B; ground[o + 1] = g.G; ground[o + 2] = g.R; ground[o + 3] = 255;
            }
        }
        // Monochrome, 32x32: columns 0-7 black, 8-15 white, 16-23 turn the screen over, 24-31 leave it.
        var monoBytes = new byte[4 * 64];
        for (int y = 0; y < 32; y++)
        {
            monoBytes[y * 4 + 0] = 0x00; monoBytes[y * 4 + 1] = 0x00; monoBytes[y * 4 + 2] = 0xFF; monoBytes[y * 4 + 3] = 0xFF; // AND
            monoBytes[(y + 32) * 4 + 0] = 0x00; monoBytes[(y + 32) * 4 + 1] = 0xFF; monoBytes[(y + 32) * 4 + 2] = 0xFF; monoBytes[(y + 32) * 4 + 3] = 0x00; // XOR
        }
        // Colour, 32x32: red, its alpha rising from 0 at the left to 248 at the right.
        var colourBytes = new byte[32 * 32 * 4];
        // Masked colour, 32x32: columns 0-9 green over the screen, 10-19 white XOR (turned over), 20-31 black XOR (left).
        var maskedBytes = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int o = (y * 32 + x) * 4;
                colourBytes[o + 2] = 255; colourBytes[o + 3] = (byte)(x * 8);
                if (x < 10) { maskedBytes[o + 1] = 255; maskedBytes[o + 3] = 0; }
                else if (x < 20) { maskedBytes[o] = 255; maskedBytes[o + 1] = 255; maskedBytes[o + 2] = 255; maskedBytes[o + 3] = 255; }
                else { maskedBytes[o + 3] = 255; }
            }
        }
        PointerImage?[] shapes =
        {
            PointerImage.From(PointerImage.Monochrome, 32, 64, 4, monoBytes),
            PointerImage.From(PointerImage.Color, 32, 32, 128, colourBytes),
            PointerImage.From(PointerImage.MaskedColor, 32, 32, 128, maskedBytes),
        };
        int[] shapeX = { 8, 64, 120 };

        using GpuDevice gpu = GpuDevice.Create();
        using ID3D11Texture2D target = gpu.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, width, height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using ID3D11RenderTargetView view = gpu.Device.CreateRenderTargetView(target);
        using (gpu.Hold()) gpu.Context.UpdateSubresource<byte>(ground.AsSpan(), target, 0, width * 4, 0);
        using (var painter = new PointerPainter(gpu.Device))
        {
            for (int s = 0; s < shapes.Length; s++)
            {
                painter.SetShape(shapes[s]);
                for (int row = 0; row < grounds.Length; row++)
                {
                    using (gpu.Hold()) painter.Draw(gpu.Context, view, width, height, shapeX[s], row * band);
                }
            }
        }
        byte[] got = ReadBack(gpu, target);

        int wrong = 0, checkedCount = 0, worst = 0;
        void Expect(int x, int y, double r, double g, double b)
        {
            int o = (y * width + x) * 4;
            int off = Math.Max(Math.Abs(got[o + 2] - (int)Math.Round(r)), Math.Max(Math.Abs(got[o + 1] - (int)Math.Round(g)), Math.Abs(got[o] - (int)Math.Round(b))));
            checkedCount++;
            worst = Math.Max(worst, off);
            if (off > 2)
            {
                wrong++;
                if (wrong <= 8) Console.WriteLine($"  wrong at {x},{y}: r{got[o + 2]} g{got[o + 1]} b{got[o]}, expected r{r:0} g{g:0} b{b:0}");
            }
        }
        for (int row = 0; row < grounds.Length; row++)
        {
            var g = grounds[row];
            for (int y = row * band; y < (row + 1) * band; y += 5)
            {
                for (int x = 0; x < 32; x++)
                {
                    // Monochrome.
                    if (x < 8) Expect(shapeX[0] + x, y, 0, 0, 0);
                    else if (x < 16) Expect(shapeX[0] + x, y, 255, 255, 255);
                    else if (x < 24) Expect(shapeX[0] + x, y, 255 - g.R, 255 - g.G, 255 - g.B);
                    else Expect(shapeX[0] + x, y, g.R, g.G, g.B);
                    // Colour: red over the ground by its alpha.
                    double a = x * 8 / 255.0;
                    Expect(shapeX[1] + x, y, 255 * a + g.R * (1 - a), g.G * (1 - a), g.B * (1 - a));
                    // Masked colour.
                    if (x < 10) Expect(shapeX[2] + x, y, 0, 255, 0);
                    else if (x < 20) Expect(shapeX[2] + x, y, 255 - g.R, 255 - g.G, 255 - g.B);
                    else Expect(shapeX[2] + x, y, g.R, g.G, g.B);
                }
                // Between the shapes nothing is touched.
                Expect(50, y, g.R, g.G, g.B);
                Expect(110, y, g.R, g.G, g.B);
            }
        }
        const int zoom = 8;
        var big = new byte[width * zoom * height * zoom * 4];
        for (int y = 0; y < height * zoom; y++)
        {
            for (int x = 0; x < width * zoom; x++)
            {
                Buffer.BlockCopy(got, ((y / zoom) * width + x / zoom) * 4, big, (y * width * zoom + x) * 4, 4);
            }
        }
        WritePng(outPath, big, width * zoom, height * zoom);
        Console.WriteLine($"{outPath}: pointer shapes on four grounds, {checkedCount} pixels checked, {wrong} wrong, worst difference {worst} of 255.");
        return wrong == 0 ? 0 : 1;
    }

    // MARK: - PNG

    /// <summary>Saves blue, green, red, alpha pixels (top row first) as a PNG, alpha kept.</summary>
    internal static void WritePng(string path, byte[] bgra, int width, int height)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 4 + 1);
            raw[o++] = 0; // no filter
            int i = y * width * 4;
            for (int x = 0; x < width; x++, i += 4, o += 4)
            {
                raw[o] = bgra[i + 2]; raw[o + 1] = bgra[i + 1]; raw[o + 2] = bgra[i]; raw[o + 3] = bgra[i + 3];
            }
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        using FileStream file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBig(header, 0, (uint)width);
        WriteBig(header, 4, (uint)height);
        header[8] = 8; // bits a channel
        header[9] = 6; // red, green, blue, alpha
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteBig(byte[] to, int at, uint value)
    {
        to[at] = (byte)(value >> 24); to[at + 1] = (byte)(value >> 16); to[at + 2] = (byte)(value >> 8); to[at + 3] = (byte)value;
    }

    private static void Chunk(Stream to, string kind, byte[] data)
    {
        var head = new byte[8];
        WriteBig(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)kind[i];
        to.Write(head, 0, 8);
        to.Write(data, 0, data.Length);
        uint crc = 0xFFFFFFFF;
        for (int i = 4; i < 8; i++) crc = Crc(crc, head[i]);
        foreach (byte b in data) crc = Crc(crc, b);
        var tail = new byte[4];
        WriteBig(tail, 0, crc ^ 0xFFFFFFFF);
        to.Write(tail, 0, 4);
    }

    private static uint Crc(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
