using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PocketXR.Core;

namespace PocketXR.Hands;

// Finds a hand in a camera picture with Google's MediaPipe hand models (the same two the Android app runs, from
// hand_landmarker.task, Apache 2.0), converted to ONNX and run on the CPU with ONNX Runtime. Two steps, as MediaPipe
// does them: a palm detector looks over the whole picture, then a landmark model reads the hand's 21 points from a
// square cut out round the palm and turned upright. While the hand stays in view the next square is worked out from
// its points, and the palm detector is not run again. The numbers (anchors, the squares' size and shift) are
// MediaPipe's own, from its hand landmarker graphs.

/// <summary>One hand as the landmark model found it: its 21 points in the picture (0 to 1, y down), how sure the
/// model is that it is a hand, and which hand MediaPipe calls it.</summary>
public sealed class DetectedHand
{
    public required (double X, double Y)[] Points { get; init; }
    /// <summary>0 to 1: the landmark model's own score that a hand is there.</summary>
    public required double Presence { get; init; }
    /// <summary>MediaPipe's "Right", which it gives as a mirror would see it: in a picture that is not mirrored, a
    /// right label is the person's LEFT hand.</summary>
    public required bool LabelRight { get; init; }
    /// <summary>How sure the model is of the label, 0.5 (no idea) to 1.</summary>
    public required double LabelScore { get; init; }

    public const int Wrist = 0, ThumbTip = 4, IndexKnuckle = 5, IndexTip = 8, MiddleKnuckle = 9, RingKnuckle = 13, LittleKnuckle = 17;

    /// <summary>The seven points hand tracking uses. [mirrored] swaps left and right, for a camera that faces the
    /// person, as the Mac does with Vision's points. The side is the person's own hand.</summary>
    public HandPoints ToHandPoints(double aspect, bool mirrored)
    {
        HandPoint P(int i) => new(mirrored ? 1 - Points[i].X : Points[i].X, Points[i].Y);
        // The label is for a mirror image: in the picture as taken, the person's hand is the other one. Turning the
        // picture round turns the hand round too. An unsure label gives no side, so the palm gestures stay off.
        HandSide? side = LabelScore < 0.7 ? null : (LabelRight ^ mirrored) ? HandSide.Left : HandSide.Right;
        return new HandPoints(P(Wrist), P(ThumbTip), P(IndexTip), P(IndexKnuckle), P(MiddleKnuckle), P(RingKnuckle),
            P(LittleKnuckle), side, aspect);
    }
}

/// <summary>A square of the picture, in pixels, turned by [Rotation] radians (clockwise on screen, y down).</summary>
internal readonly record struct HandRoi(double CentreX, double CentreY, double Width, double Height, double Rotation);

public sealed class HandDetector : IDisposable
{
    private const int PalmSize = 192, MarkSize = 224;
    /// <summary>A palm less sure than this is not taken (MediaPipe: min_detection_confidence 0.5).</summary>
    private const double MinPalmScore = 0.5;
    /// <summary>A hand the landmark model is less sure of than this is let go, and the palm looked for again
    /// (MediaPipe: min_hand_presence_confidence 0.5).</summary>
    private const double MinPresence = 0.5;

    private readonly InferenceSession palm, marks;
    private readonly string palmInput, marksInput;
    private readonly float[] palmPixels = new float[PalmSize * PalmSize * 3];
    private readonly float[] markPixels = new float[MarkSize * MarkSize * 3];
    private static readonly (float X, float Y)[] Anchors = MakeAnchors();
    private HandRoi? tracked;

    /// <summary>Milliseconds the last frame took in each step, for the log.</summary>
    public double PalmMs { get; private set; }
    public double MarksMs { get; private set; }
    /// <summary>The palm detector ran on the last frame (no hand was being followed).</summary>
    public bool RanPalm { get; private set; }

    private HandDetector(InferenceSession palm, InferenceSession marks)
    {
        this.palm = palm;
        this.marks = marks;
        palmInput = palm.InputMetadata.Keys.GetEnumerator() is var a && a.MoveNext() ? a.Current : "input_1";
        marksInput = marks.InputMetadata.Keys.GetEnumerator() is var b && b.MoveNext() ? b.Current : "input_1";
    }

    /// <summary>Loads both models from inside the app. Null, with the reason, when they cannot be loaded.</summary>
    public static HandDetector? Load(out string? error)
    {
        try
        {
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            // Two threads each: the detector should not take the PC from the floating screens.
            options.IntraOpNumThreads = 2;
            options.InterOpNumThreads = 1;
            var palm = new InferenceSession(Model("hand_detector.onnx"), options);
            var marks = new InferenceSession(Model("hand_landmarks_detector.onnx"), options);
            error = null;
            return new HandDetector(palm, marks);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    private static byte[] Model(string name)
    {
        Assembly a = typeof(HandDetector).Assembly;
        using Stream s = a.GetManifestResourceStream("PocketXR.Hands." + name)
            ?? throw new FileNotFoundException($"{name} is not in this build of Pocket XR");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Forgets the hand being followed, so the next frame looks over the whole picture.</summary>
    public void Reset() => tracked = null;

    /// <summary>The hand in a picture of [width] x [height] pixels, 4 bytes each in the order blue, green, red,
    /// alpha, [stride] bytes a row. Null when there is none.</summary>
    public DetectedHand? Detect(byte[] bgra, int width, int height, int stride)
    {
        RanPalm = false;
        PalmMs = 0;
        if (tracked is HandRoi roi)
        {
            DetectedHand? hand = ReadHand(bgra, width, height, stride, roi);
            if (hand != null) return hand;
        }
        RanPalm = true;
        var t = System.Diagnostics.Stopwatch.StartNew();
        HandRoi? found = FindPalm(bgra, width, height, stride);
        PalmMs = t.Elapsed.TotalMilliseconds;
        if (found is not HandRoi palmRoi)
        {
            tracked = null;
            MarksMs = 0;
            return null;
        }
        return ReadHand(bgra, width, height, stride, palmRoi);
    }

    // MARK: the palm detector

    /// <summary>The square to read the hand from, round the surest palm in the picture; null when there is none.</summary>
    private HandRoi? FindPalm(byte[] bgra, int width, int height, int stride)
    {
        // The whole picture made to fit 192 x 192 without stretching, with black either side (MediaPipe:
        // keep_aspect_ratio, zero border).
        double scale = Math.Min((double)PalmSize / width, (double)PalmSize / height);
        double padX = (PalmSize - width * scale) / 2, padY = (PalmSize - height * scale) / 2;
        for (int j = 0; j < PalmSize; j++)
        {
            for (int i = 0; i < PalmSize; i++)
            {
                double sx = (i + 0.5 - padX) / scale - 0.5, sy = (j + 0.5 - padY) / scale - 0.5;
                int o = (j * PalmSize + i) * 3;
                if (sx < -0.5 || sy < -0.5 || sx > width - 0.5 || sy > height - 0.5)
                {
                    palmPixels[o] = palmPixels[o + 1] = palmPixels[o + 2] = 0;
                    continue;
                }
                Sample(bgra, width, height, stride, sx, sy, palmPixels, o);
            }
        }
        var input = new DenseTensor<float>(palmPixels, new[] { 1, PalmSize, PalmSize, 3 });
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outs =
            palm.Run(new[] { NamedOnnxValue.CreateFromTensor(palmInput, input) });
        // Two outputs: the boxes and their points [1, 2016, 18], and the scores [1, 2016, 1]. Told apart by size.
        float[]? boxes = null, scores = null;
        foreach (DisposableNamedOnnxValue o in outs)
        {
            float[] v = o.AsEnumerable<float>().ToArrayFast();
            if (v.Length == Anchors.Length * 18) boxes = v;
            else if (v.Length == Anchors.Length) scores = v;
        }
        if (boxes == null || scores == null) return null;

        // The surest palm, then every palm that overlaps it averaged in by its score (MediaPipe: weighted
        // non-maximum suppression, overlap 0.3), which steadies the box from frame to frame.
        int best = -1;
        double bestScore = 0;
        for (int k = 0; k < scores.Length; k++)
        {
            double s = Sigmoid(scores[k]);
            if (s > bestScore) { bestScore = s; best = k; }
        }
        if (best < 0 || bestScore < MinPalmScore) return null;
        Palm top = Decode(boxes, best);
        double sum = 0, cx = 0, cy = 0, w = 0, h = 0, k0x = 0, k0y = 0, k2x = 0, k2y = 0;
        for (int k = 0; k < scores.Length; k++)
        {
            double s = Sigmoid(scores[k]);
            if (s < MinPalmScore) continue;
            Palm p = Decode(boxes, k);
            if (Overlap(top, p) < 0.3) continue;
            sum += s;
            cx += s * p.Cx; cy += s * p.Cy; w += s * p.W; h += s * p.H;
            k0x += s * p.WristX; k0y += s * p.WristY; k2x += s * p.MiddleX; k2y += s * p.MiddleY;
        }
        cx /= sum; cy /= sum; w /= sum; h /= sum; k0x /= sum; k0y /= sum; k2x /= sum; k2y /= sum;

        // Back from the 192 square to the picture's own pixels.
        double X(double v) => (v * PalmSize - padX) / scale;
        double Y(double v) => (v * PalmSize - padY) / scale;
        double boxW = w * PalmSize / scale, boxH = h * PalmSize / scale;
        double wx = X(k0x), wy = Y(k0y), mx = X(k2x), my = Y(k2y);
        // Turned so the wrist is below the middle finger's knuckle (MediaPipe: rotation from keypoint 0 to 2, 90 degrees).
        double rotation = Normalise(Math.PI / 2 - Math.Atan2(-(my - wy), mx - wx));
        // Made square on its longer side, 2.6 times as large, and moved half its height towards the fingers.
        return Transform(new HandRoi(X(cx), Y(cy), boxW, boxH, rotation), 2.6, -0.5);
    }

    private readonly record struct Palm(double Cx, double Cy, double W, double H, double WristX, double WristY, double MiddleX, double MiddleY);

    /// <summary>Palm [k]'s box and two of its points, 0 to 1 of the 192 square.</summary>
    private static Palm Decode(float[] boxes, int k)
    {
        int o = k * 18;
        (float ax, float ay) = Anchors[k];
        return new Palm(boxes[o] / PalmSize + ax, boxes[o + 1] / PalmSize + ay, boxes[o + 2] / PalmSize, boxes[o + 3] / PalmSize,
            boxes[o + 4] / PalmSize + ax, boxes[o + 5] / PalmSize + ay, boxes[o + 8] / PalmSize + ax, boxes[o + 9] / PalmSize + ay);
    }

    private static double Overlap(Palm a, Palm b)
    {
        double x0 = Math.Max(a.Cx - a.W / 2, b.Cx - b.W / 2), x1 = Math.Min(a.Cx + a.W / 2, b.Cx + b.W / 2);
        double y0 = Math.Max(a.Cy - a.H / 2, b.Cy - b.H / 2), y1 = Math.Min(a.Cy + a.H / 2, b.Cy + b.H / 2);
        double both = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        double either = a.W * a.H + b.W * b.H - both;
        return either <= 0 ? 0 : both / either;
    }

    /// <summary>The palm detector's 2016 anchor points (MediaPipe's SSD anchors for 192 x 192: four layers with
    /// strides 8, 16, 16 and 16, two anchors a cell, all the same size).</summary>
    private static (float X, float Y)[] MakeAnchors()
    {
        var list = new List<(float, float)>();
        int[] strides = { 8, 16, 16, 16 };
        int layer = 0;
        while (layer < strides.Length)
        {
            // Layers with the same stride share their cells: each adds two anchors a cell.
            int same = layer, perCell = 0;
            while (same < strides.Length && strides[same] == strides[layer]) { perCell += 2; same++; }
            int cells = (int)Math.Ceiling((double)PalmSize / strides[layer]);
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                    for (int n = 0; n < perCell; n++)
                        list.Add(((x + 0.5f) / cells, (y + 0.5f) / cells));
            layer = same;
        }
        return list.ToArray();
    }

    // MARK: the landmark model

    /// <summary>Reads the hand's 21 points from [roi]. Keeps the square for the next frame when it is a hand, and
    /// returns null (and forgets it) when it is not.</summary>
    private DetectedHand? ReadHand(byte[] bgra, int width, int height, int stride, HandRoi roi)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        double cos = Math.Cos(roi.Rotation), sin = Math.Sin(roi.Rotation);
        for (int j = 0; j < MarkSize; j++)
        {
            double v = (j + 0.5) / MarkSize - 0.5;
            for (int i = 0; i < MarkSize; i++)
            {
                double u = (i + 0.5) / MarkSize - 0.5;
                double dx = u * roi.Width, dy = v * roi.Height;
                double sx = roi.CentreX + cos * dx - sin * dy - 0.5, sy = roi.CentreY + sin * dx + cos * dy - 0.5;
                Sample(bgra, width, height, stride, sx, sy, markPixels, (j * MarkSize + i) * 3);
            }
        }
        var input = new DenseTensor<float>(markPixels, new[] { 1, MarkSize, MarkSize, 3 });
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outs =
            marks.Run(new[] { NamedOnnxValue.CreateFromTensor(marksInput, input) });
        // Four outputs: the 21 points in the square's pixels [1, 63], the presence [1, 1], the hand's side [1, 1],
        // and the points in metres [1, 63]. The two single numbers come in that order; the points are the first 63.
        float[]? points = null;
        var singles = new List<float>();
        foreach (DisposableNamedOnnxValue o in outs)
        {
            float[] val = o.AsEnumerable<float>().ToArrayFast();
            if (val.Length == 63 && points == null) points = val;
            else if (val.Length == 1) singles.Add(val[0]);
        }
        MarksMs = t.Elapsed.TotalMilliseconds;
        if (points == null || singles.Count < 2)
        {
            tracked = null;
            return null;
        }
        // Both already 0 to 1 (checked 18 Nov 2026: 0.60 for a rough crop of a hand, 0.002 for a plain grey square).
        double presence = singles[0], label = singles[1];
        if (presence < MinPresence)
        {
            tracked = null;
            return null;
        }
        var px = new (double X, double Y)[21];
        var pic = new (double X, double Y)[21];
        for (int n = 0; n < 21; n++)
        {
            double u = points[n * 3] / MarkSize - 0.5, v = points[n * 3 + 1] / MarkSize - 0.5;
            double dx = u * roi.Width, dy = v * roi.Height;
            px[n] = (roi.CentreX + cos * dx - sin * dy, roi.CentreY + sin * dx + cos * dy);
            pic[n] = (px[n].X / width, px[n].Y / height);
        }
        tracked = FromPoints(px);
        return new DetectedHand { Points = pic, Presence = presence, LabelRight = label > 0.5, LabelScore = Math.Max(label, 1 - label) };
    }

    /// <summary>The square for the next frame from this frame's points (MediaPipe: hand landmarks to rect, then
    /// 2 times as large and a tenth of its height towards the fingers).</summary>
    private static HandRoi FromPoints((double X, double Y)[] p)
    {
        // The wrist, and a point between the middle knuckle and the middle of the index and ring knuckles.
        double x1 = ((p[5].X + p[13].X) / 2 + p[9].X) / 2, y1 = ((p[5].Y + p[13].Y) / 2 + p[9].Y) / 2;
        double rotation = Normalise(Math.PI / 2 - Math.Atan2(-(y1 - p[0].Y), x1 - p[0].X));
        // The wrist, the thumb's lower three joints and the two lower joints of each finger: what bends least.
        int[] use = { 0, 1, 2, 3, 5, 6, 9, 10, 13, 14, 17, 18 };
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (int i in use)
        {
            minX = Math.Min(minX, p[i].X); maxX = Math.Max(maxX, p[i].X);
            minY = Math.Min(minY, p[i].Y); maxY = Math.Max(maxY, p[i].Y);
        }
        double ax = (minX + maxX) / 2, ay = (minY + maxY) / 2;
        double cos = Math.Cos(-rotation), sin = Math.Sin(-rotation);
        double pMinX = double.MaxValue, pMaxX = double.MinValue, pMinY = double.MaxValue, pMaxY = double.MinValue;
        foreach (int i in use)
        {
            double ox = p[i].X - ax, oy = p[i].Y - ay;
            double rx = ox * cos - oy * sin, ry = ox * sin + oy * cos;
            pMinX = Math.Min(pMinX, rx); pMaxX = Math.Max(pMaxX, rx);
            pMinY = Math.Min(pMinY, ry); pMaxY = Math.Max(pMaxY, ry);
        }
        double pcx = (pMinX + pMaxX) / 2, pcy = (pMinY + pMaxY) / 2;
        double c = Math.Cos(rotation), s = Math.Sin(rotation);
        var box = new HandRoi(ax + pcx * c - pcy * s, ay + pcx * s + pcy * c, pMaxX - pMinX, pMaxY - pMinY, rotation);
        return Transform(box, 2.0, -0.1);
    }

    /// <summary>MediaPipe's rect transformation: moved [shiftY] of its height along its own up and down, made square
    /// on its longer side, and [scale] times as large.</summary>
    private static HandRoi Transform(HandRoi r, double scale, double shiftY)
    {
        double dx = -r.Height * shiftY * Math.Sin(r.Rotation), dy = r.Height * shiftY * Math.Cos(r.Rotation);
        double side = Math.Max(r.Width, r.Height) * scale;
        return new HandRoi(r.CentreX + dx, r.CentreY + dy, side, side, r.Rotation);
    }

    // MARK: pixels and numbers

    /// <summary>The picture at ([sx], [sy]) pixels, blended from the four nearest, as red, green and blue from 0 to 1;
    /// black outside the picture.</summary>
    private static void Sample(byte[] bgra, int width, int height, int stride, double sx, double sy, float[] to, int at)
    {
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        double fx = sx - x0, fy = sy - y0;
        double r = 0, g = 0, b = 0;
        for (int k = 0; k < 4; k++)
        {
            int x = x0 + (k & 1), y = y0 + (k >> 1);
            double wgt = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fy : fy);
            if (wgt <= 0 || x < 0 || y < 0 || x >= width || y >= height) continue;
            int o = y * stride + x * 4;
            b += wgt * bgra[o];
            g += wgt * bgra[o + 1];
            r += wgt * bgra[o + 2];
        }
        to[at] = (float)(r / 255);
        to[at + 1] = (float)(g / 255);
        to[at + 2] = (float)(b / 255);
    }

    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-Math.Clamp(x, -100, 100)));

    private static double Normalise(double a) => a - 2 * Math.PI * Math.Floor((a + Math.PI) / (2 * Math.PI));

    public void Dispose()
    {
        palm.Dispose();
        marks.Dispose();
    }
}

internal static class EnumerableArray
{
    public static float[] ToArrayFast(this IEnumerable<float> v) => v is float[] a ? a : new List<float>(v).ToArray();
}
