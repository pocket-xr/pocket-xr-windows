using System;
using System.Collections.Generic;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

// Checks for hand tracking's logic (src/PocketXR.Core/Hand*.cs). Everything here runs without a camera: hands
// are made up as points, and the gestures are driven by the clock at 30 frames a second.
public static class HandChecks
{
    /// <summary>
    /// A hand held up with its fingers up, as the camera's picture has it (y down). [palm] true shows the palm to the
    /// camera. [gap] is the thumb tip to index tip distance over the hand's size (the pinch ratio).
    /// </summary>
    private static HandPoints HandMade(double x = 0.5, double y = 0.5, HandSide? side = HandSide.Right, bool palm = true,
                                       double gap = 0.6, double aspect = 1)
    {
        // A right hand showing its palm has its index knuckle (and thumb) on the right of the picture.
        bool indexRight = (side != HandSide.Left) == palm;
        double k = indexRight ? 1 : -1;
        double w = 1 / aspect; // so the hand is the same shape whatever the picture's shape
        var wrist = new HandPoint(x, y + 0.15);
        var middle = new HandPoint(x + k * 0.013 * w, y);
        double size = 0.15; // wrist to middle knuckle, near enough (the sideways 0.013 adds 0.4 %)
        var thumb = new HandPoint(x + k * 0.08 * w, y - 0.05);
        var index = new HandPoint(thumb.X, thumb.Y - gap * size);
        return new HandPoints(
            wrist: wrist, thumbTip: thumb, indexTip: index,
            indexKnuckle: new HandPoint(x + k * 0.04 * w, y),
            middleKnuckle: middle,
            ringKnuckle: new HandPoint(x - k * 0.013 * w, y),
            littleKnuckle: new HandPoint(x - k * 0.04 * w, y),
            side: side, aspect: aspect);
    }

    private const double HandFrameMs = 1000.0 / 30;

    /// <summary>Feeds [gestures] one frame after another from [startMs] and returns each frame's events.</summary>
    private static List<List<HandEvent>> HandRun(HandGestures gestures, double from, IReadOnlyList<HandInput?> frames)
    {
        var result = new List<List<HandEvent>>();
        for (int i = 0; i < frames.Count; i++)
        {
            result.Add(gestures.Update(frames[i], timeMs: from + (double)i * HandFrameMs));
        }
        return result;
    }

    private static HandInput HandFrame(double ratio, bool palm = false, double yaw = 5, double pitch = 2)
    {
        var aim = new HandAngles(yaw, pitch);
        return new HandInput(ratio: ratio, palmFacing: palm, aim: aim, motion: aim);
    }

    private static List<HandAngles> HandPresses(List<List<HandEvent>> events) =>
        events.SelectMany(frame => frame).Where(ev => ev.Kind == HandEventKind.Press).Select(ev => ev.Angles).ToList();

    private static int HandCount(List<List<HandEvent>> events, HandEvent wanted) =>
        events.SelectMany(frame => frame).Count(ev => ev == wanted);

    private static HandAngles? HandLastAim(List<HandEvent> events)
    {
        HandAngles? last = null;
        foreach (HandEvent ev in events)
        {
            if (ev.Kind == HandEventKind.Aim) last = ev.Angles;
        }
        return last;
    }

    // Swift's == between two arrays of events.
    private static bool Same(List<HandEvent> got, params HandEvent[] wanted) => got.SequenceEqual(wanted);

    // Swift's Array(repeating:count:).
    private static List<HandInput?> Repeating(HandInput? frame, int count) => Enumerable.Repeat(frame, count).ToList();

    private static string Show(List<HandEvent> events) => "[" + string.Join(", ", events) + "]";

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    public static void Run()
    {
        HandPointChecks();
        HandFilterChecks();
        HandCalibrationChecks();
        HandPrimaryChecks();
        HandGestureChecks();
        HandAirChecks();
    }

    private static void HandAirChecks()
    {
        // The air trackpad's box: the middle 60 % by 55 % of the picture, resting a little low, is the whole screen.
        var box = new HandAirBox();
        Check.That(HandAirBox.WidthShare == 0.60 && HandAirBox.HeightShare == 0.55, "air trackpad: the box is 60 % by 55 % of the picture");
        Check.That(box.Centre == new HandPoint(0.5, 0.56), "air trackpad: the box rests a little below the middle");
        var mid = box.Place(box.Centre);
        Check.That(Check.Near(mid.U, 0.5) && Check.Near(mid.V, 0.5), "air trackpad: the box's middle is the screen's middle");
        var topLeft = box.Place(new HandPoint(0.2, 0.285));
        Check.That(Check.Near(topLeft.U, 0) && Check.Near(topLeft.V, 0), "air trackpad: the box's top left is the screen's");
        var bottomRight = box.Place(new HandPoint(0.8, 0.835));
        Check.That(Check.Near(bottomRight.U, 1) && Check.Near(bottomRight.V, 1), "air trackpad: the box's bottom right is the screen's");
        var outside = box.Place(new HandPoint(0.95, 0.1));
        Check.That(outside.U > 1 && outside.V < 0, "air trackpad: outside the box is past the screen's edge until it is clamped");
        var back = box.Point(u: 0.25, v: 0.75);
        var again = box.Place(back);
        Check.That(Check.Near(again.U, 0.25) && Check.Near(again.V, 0.75), "air trackpad: a place on the screen and its point in the picture go both ways");

        // Mirroring: the camera faces the person, so its picture is swapped. A hand at the person's right is on the
        // LEFT of what the camera sees (Vision's x small); mirrored, it is on the right, and so is the pointer.
        var personsRight = HandPoint.FromLowerLeft(x: 0.25, y: 0.44, mirrored: true);
        Check.That(box.Place(personsRight).U > 0.5, "air trackpad: a hand to the person's right puts the pointer right of the middle");
        var raised = HandPoint.FromLowerLeft(x: 0.5, y: 0.7, mirrored: true);
        Check.That(box.Place(raised).V < 0.5, "air trackpad: a raised hand puts the pointer above the middle");

        // A reach set off to one side keeps the whole box in the picture.
        var low = new HandAirBox(centre: new HandPoint(0.95, 0.99));
        Check.That(Check.Near(low.Centre.X, 0.7) && Check.Near(low.Centre.Y, 0.725), "air trackpad: the box is kept inside the picture");
        Check.That(new HandAirBox(centre: new HandPoint(0.4, 0.5)).Centre == new HandPoint(0.4, 0.5), "air trackpad: a reach inside the picture is taken as it is");

        // The gestures work in degrees: the screen is given 40 by 22.5 of them, left and up positive.
        Check.That(HandAirBox.Angles(u: 0.5, v: 0.5) == HandAngles.Zero, "air trackpad: the screen's middle is 0, 0");
        var corner = HandAirBox.Angles(u: 0, v: 0);
        Check.That(Check.Near(corner.YawDeg, 20) && Check.Near(corner.PitchDeg, 11.25), "air trackpad: the top left is left and up");
        var there = HandAirBox.Shares(HandAirBox.Angles(u: 0.3, v: 0.9));
        Check.That(Check.Near(there.U, 0.3) && Check.Near(there.V, 0.9), "air trackpad: angles and back");
        var clamped = HandAirBox.Shares(new HandAngles(yawDeg: -50, pitchDeg: 30));
        Check.That(clamped.U == 1 && clamped.V == 0, "air trackpad: the pointer is kept on the screen");
        var open = HandAirBox.OpenShares(new HandAngles(yawDeg: -50, pitchDeg: 30));
        Check.That(open.U > 1 && open.V < 0, "air trackpad: where the hand was is not clamped");

        // Which camera, as it is kept.
        Check.That(new HandCameraChoice(stored: null) == HandCameraChoice.Glasses && new HandCameraChoice(stored: "") == HandCameraChoice.Glasses, "hand camera: nothing kept means the glasses' camera");
        Check.That(new HandCameraChoice(stored: "mac") == HandCameraChoice.Mac && new HandCameraChoice(stored: "glasses") == HandCameraChoice.Glasses, "hand camera: the two named choices");
        Check.That(new HandCameraChoice(stored: "0x1420000005ac8514") == HandCameraChoice.Device("0x1420000005ac8514"), "hand camera: anything else is one device's id");
        Check.That(new HandCameraChoice(stored: HandCameraChoice.Device("abc").Stored) == HandCameraChoice.Device("abc") && HandCameraChoice.Mac.Stored == "mac", "hand camera: kept and read back");
    }

    private static void HandPointChecks()
    {
        // Vision's origin is the lower left; ours is the upper left. The flip is in one place.
        var flipped = HandPoint.FromLowerLeft(x: 0.2, y: 0.9);
        Check.That(Check.Near(flipped.X, 0.2) && Check.Near(flipped.Y, 0.1), "hands: a point from Vision is turned the right way up");
        var mirrored = HandPoint.FromLowerLeft(x: 0.2, y: 0.9, mirrored: true);
        Check.That(Check.Near(mirrored.X, 0.8) && Check.Near(mirrored.Y, 0.1), "hands: a mirrored point also swaps left and right");

        // A right hand, palm to the camera, as Vision would give it (y up): wrist low, index knuckle to the right.
        var vision = new (double X, double Y)[]
        {
            (0.50, 0.35), (0.58, 0.55), (0.58, 0.64), (0.54, 0.50), (0.513, 0.50), (0.487, 0.50), (0.46, 0.50),
        };
        HandPoints? seen = HandPoints.FromLowerLeft(vision, side: HandSide.Right, aspect: 4.0 / 3);
        Check.That(seen != null, "hands: seven points make a hand");
        Check.That(HandPoints.FromLowerLeft(vision.SkipLast(1).ToArray(), side: HandSide.Right, aspect: 1) == null, "hands: six points do not");
        if (seen is HandPoints seenHand)
        {
            Check.That(Check.Near(seenHand.Wrist.Y, 0.65) && Check.Near(seenHand.IndexKnuckle.Y, 0.5), "hands: the wrist is below the knuckles after the flip");
            Check.That(seenHand.Wrist.Y > seenHand.IndexTip.Y, "hands: fingers up means a smaller y");
            Check.That(HandPalm.FacesCamera(seenHand), "hands: a right hand from Vision with its index knuckle on the right shows its palm");
        }
        // Mirrored (the computer's own camera): the picture is swapped and the hand's side with it, so the answer for
        // the camera stays the same.
        if (HandPoints.FromLowerLeft(vision, side: HandSide.Right, aspect: 4.0 / 3, mirrored: true) is HandPoints swapped)
        {
            Check.That(swapped.Side == HandSide.Left, "hands: a mirrored right hand is handled as a left one");
            Check.That(Check.Near(swapped.IndexKnuckle.X, 0.46), "hands: a mirrored hand's points are swapped left to right");
            Check.That(HandPalm.FacesCamera(swapped), "hands: mirroring does not change whether the palm faces the camera");
        }

        // Palm facing: each hand, each way, edge on, and a hand of unknown side.
        Check.That(HandPalm.FacesCamera(HandMade(side: HandSide.Right, palm: true)), "hands: right palm faces the camera");
        Check.That(!HandPalm.FacesCamera(HandMade(side: HandSide.Right, palm: false)), "hands: the back of a right hand does not");
        Check.That(HandPalm.FacesCamera(HandMade(side: HandSide.Left, palm: true)), "hands: left palm faces the camera");
        Check.That(!HandPalm.FacesCamera(HandMade(side: HandSide.Left, palm: false)), "hands: the back of a left hand does not");
        Check.That(HandPalm.Turn(HandMade(side: HandSide.Right, palm: true)) < -HandPalm.MinOpen, "hands: a right palm turns against the clock with y down");
        var unknown = HandMade(side: HandSide.Right, palm: true);
        unknown.Side = null;
        Check.That(!HandPalm.FacesCamera(unknown), "hands: a hand of unknown side never counts as a palm");
        var edge = HandMade(side: HandSide.Right, palm: true);
        edge.IndexKnuckle = new HandPoint(0.501, 0.5);
        edge.LittleKnuckle = new HandPoint(0.499, 0.5);
        Check.That(!HandPalm.FacesCamera(edge), "hands: a hand seen edge on counts neither way");
        Check.That(HandPalm.FacesCamera(HandMade(side: HandSide.Right, palm: true, aspect: 16.0 / 9)), "hands: the palm test holds in a wide picture");

        // The pinch measure is the same whatever the picture's shape, near or far.
        Check.That(Check.Near(HandMade(gap: 0.6).PinchRatio, 0.6, 0.01), "hands: an open hand reads about 0.6");
        Check.That(Check.Near(HandMade(gap: 0.2, aspect: 16.0 / 9).PinchRatio, 0.2, 0.01), "hands: a pinch reads the same in a wide picture");
        var sideways = HandMade(gap: 0.6, aspect: 2);
        sideways.ThumbTip = new HandPoint(0.5, 0.4);
        sideways.IndexTip = new HandPoint(0.55, 0.4);
        // 0.05 of the width in a picture twice as wide as high is 0.1 of its height.
        Check.That(Check.Near(sideways.Distance(sideways.ThumbTip, sideways.IndexTip), 0.1), "hands: sideways distances are scaled by the picture's shape");

        // The steady point: knuckles' mean + 0.45 of wrist to knuckles + 0.88 of little to index knuckle.
        var anchor = new HandAnchor();
        var openHand = HandMade(gap: 0.6);
        var first = anchor.Point(openHand, timeMs: 0);
        Check.That(Check.Near(first.X, 0.5 + 0.88 * 0.08, 1e-9) && Check.Near(first.Y, 0.5 - 0.45 * 0.15, 1e-9), "hands: the steady point is where a pinch happens");
        var closed = HandMade(gap: 0.05);
        var second = anchor.Point(closed, timeMs: HandFrameMs);
        Check.That(Check.Near(first.X, second.X, 1e-9) && Check.Near(first.Y, second.Y, 1e-9), "hands: closing the fingers does not move the steady point");
        var moved = openHand;
        // Swift walks these five by key path.
        moved.Wrist.X += 0.1;
        moved.IndexKnuckle.X += 0.1;
        moved.MiddleKnuckle.X += 0.1;
        moved.RingKnuckle.X += 0.1;
        moved.LittleKnuckle.X += 0.1;
        var third = anchor.Point(moved, timeMs: 2 * HandFrameMs);
        Check.That(Check.Near(third.X - first.X, 0.1, 1e-9), "hands: moving the hand moves the steady point as far");
        // A sudden turn of the hand (the knuckles swapped over) comes through smoothed, not at once.
        var turnAnchor = new HandAnchor();
        _ = turnAnchor.Point(HandMade(palm: true), timeMs: 0);
        _ = turnAnchor.Point(HandMade(palm: true), timeMs: 33);
        _ = turnAnchor.Point(HandMade(palm: true), timeMs: 66);
        var turned = turnAnchor.Point(HandMade(palm: false), timeMs: 99);
        Check.That(turned.X > 0.5 && turned.X < first.X, "hands: the steady point's offsets are smoothed when the hand turns");
        turnAnchor.Reset();
        var afresh = turnAnchor.Point(HandMade(palm: false), timeMs: 132);
        Check.That(Check.Near(afresh.X, 0.5 - 0.88 * 0.08, 1e-9), "hands: after a reset the steady point starts from the frame it is given");

        // The pinhole: the middle is straight ahead, the edges are half the field of view out, left and up positive.
        var camera = new HandCameraModel(fovDeg: 68, aspect: 16.0 / 9);
        Check.That(Check.Near(camera.VerticalFovDeg, 41.6, 0.1), "hands: 68 degrees across a 16:9 picture is 41.6 up and down");
        var middle = camera.Ray(new HandPoint(0.5, 0.5));
        Check.That(Check.Near(middle.YawDeg, 0) && Check.Near(middle.PitchDeg, 0), "hands: the picture's middle is straight ahead");
        Check.That(Check.Near(camera.Ray(new HandPoint(1, 0.5)).YawDeg, -34), "hands: the right edge is half the view to the right (negative)");
        Check.That(Check.Near(camera.Ray(new HandPoint(0, 0.5)).YawDeg, 34), "hands: the left edge is half the view to the left");
        Check.That(Check.Near(camera.Ray(new HandPoint(0.5, 0)).PitchDeg, camera.VerticalFovDeg / 2), "hands: the top edge is half the view up");
        Check.That(camera.Ray(new HandPoint(0.5, 1)).PitchDeg < 0, "hands: the bottom is down");
        // A corner, taken back through the app's own turn (yaw about up, then pitch): it must land on the same point.
        var corner = new HandPoint(0.9, 0.2);
        var ray = camera.Ray(corner);
        double yaw = ray.YawDeg * Math.PI / 180, pitch = ray.PitchDeg * Math.PI / 180;
        double dx = -Math.Sin(yaw) * Math.Cos(pitch), dy = Math.Sin(pitch), dz = -Math.Cos(yaw) * Math.Cos(pitch);
        double halfX = Math.Tan(34 * Math.PI / 180);
        double backX = 0.5 + (dx / -dz) / (2 * halfX);
        double backY = 0.5 - (dy / -dz) / (2 * halfX / (16.0 / 9));
        Check.That(Check.Near(backX, corner.X, 1e-9) && Check.Near(backY, corner.Y, 1e-9), "hands: a ray's angles are the app's own (yaw, then pitch)");
        Check.That(new HandCameraModel(fovDeg: 500, aspect: 0).FovDeg == 170, "hands: an absurd field of view is brought into range");
    }

    private static void HandFilterChecks()
    {
        // The pinch detector: 2 frames below 0.25, or 1 below 0.15; out only after 2 frames above 0.42.
        var d = new HandPinchDetector();
        var steps = new (double, bool)[] { (0.6, false), (0.24, false), (0.3, false), (0.24, false), (0.24, true) };
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            Check.That(d.Update(s.Item1) == s.Item2, Inv($"hands: pinch detector step {i} at {s.Item1}"));
        }
        var releases = new (double, bool)[] { (0.3, true), (0.41, true), (0.5, true), (0.3, true), (0.5, true), (0.5, false) };
        for (int i = 0; i < releases.Length; i++)
        {
            var s = releases[i];
            Check.That(d.Update(s.Item1) == s.Item2, Inv($"hands: pinch release step {i} at {s.Item1}"));
        }
        Check.That(d.Update(0.14), "hands: fingers clearly touching pinch on the first frame");
        d.Reset();
        Check.That(!d.Pinched, "hands: a reset pinch detector is open");
        Check.That(!d.Update(0.2) && d.Update(0.2), "hands: after a reset it needs its two frames again");

        // One Euro: a still value stays, jitter is cut, a sweep is followed closely, time must move forward.
        var still = new HandOneEuro();
        double @out = 0.0;
        for (int i = 0; i < 60; i++) @out = still.Filter(5, timeMs: (double)i * HandFrameMs);
        Check.That(Check.Near(@out, 5), "hands: a still value stays where it is");
        var noisy = new HandOneEuro();
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        for (int i = 0; i < 120; i++)
        {
            double v = noisy.Filter(10 + (i % 2 == 0 ? 0.15 : -0.15), timeMs: (double)i * HandFrameMs);
            if (i >= 60) { low = Math.Min(low, v); high = Math.Max(high, v); }
        }
        Check.That(high - low < 0.06, Inv($"hands: 0.3 degrees of jitter comes out under 0.06 (got {high - low})"));
        Check.That(Math.Abs((high + low) / 2 - 10) < 0.02, "hands: jitter does not shift the value");
        var sweep = new HandOneEuro();
        double lag = 0.0;
        for (int i = 0; i < 30; i++)
        {
            double x = 2.0 * (double)i; // 60 degrees a second
            lag = x - sweep.Filter(x, timeMs: (double)i * HandFrameMs);
        }
        Check.That(lag >= 0 && lag < 0.5, Inv($"hands: a 60 degrees a second sweep lags under half a degree (got {lag})"));
        var stuck = new HandOneEuro();
        _ = stuck.Filter(1, timeMs: 100);
        Check.That(Check.Near(stuck.Filter(9, timeMs: 100), 1) && Check.Near(stuck.Filter(9, timeMs: 50), 1), "hands: a frame whose time has not moved on changes nothing");
        stuck.Reset();
        Check.That(Check.Near(stuck.Filter(9, timeMs: 10), 9), "hands: a reset filter starts from the value it is given");
        var both = new HandRayFilter();
        _ = both.Filter(new HandAngles(yawDeg: 0, pitchDeg: 3), timeMs: 0);
        HandAngles swept = HandAngles.Zero;
        for (int i = 1; i < 30; i++) swept = both.Filter(new HandAngles(yawDeg: 2.0 * (double)i, pitchDeg: 3), timeMs: (double)i * HandFrameMs);
        Check.That(Check.Near(swept.PitchDeg, 3) && swept.YawDeg > 57, "hands: a sideways sweep leaves the pitch still");
    }

    private static void HandCalibrationChecks()
    {
        var rays = new HandAngles[]
        {
            new HandAngles(yawDeg: 0, pitchDeg: 0), new HandAngles(yawDeg: 12, pitchDeg: 7), new HandAngles(yawDeg: -12, pitchDeg: 7),
            new HandAngles(yawDeg: -12, pitchDeg: -7), new HandAngles(yawDeg: 12, pitchDeg: -7),
        };
        var truth = new HandCalibration(a: 1.1, b: 0.05, c: 2, d: -0.03, e: 0.9, f: -1.5);
        HandAngles[] wanted = rays.Select(r => truth.Apply(r)).ToArray();
        var fit = HandCalibration.Fit(measured: rays, wanted: wanted);
        bool close = fit.Numbers.Zip(truth.Numbers).All(pair => Check.Near(pair.First, pair.Second, 1e-6));
        Check.That(close, "hand calibration: five dots give back the correction that made them");
        Check.That(fit.RmsDeg(measured: rays, wanted: wanted) < 1e-6, "hand calibration: an exact fit is 0 degrees off");
        var applied = truth.Apply(new HandAngles(yawDeg: 10, pitchDeg: 4));
        Check.That(Check.Near(applied.YawDeg, 1.1 * 10 + 0.05 * 4 + 2) && Check.Near(applied.PitchDeg, -0.03 * 10 + 0.9 * 4 - 1.5), "hand calibration: apply is the two lines");
        Check.That(HandCalibration.Identity.Apply(new HandAngles(yawDeg: 3, pitchDeg: -4)) == new HandAngles(yawDeg: 3, pitchDeg: -4), "hand calibration: none changes nothing");
        Check.That(HandCalibration.Fit(measured: Array.Empty<HandAngles>(), wanted: Array.Empty<HandAngles>()).IsIdentity, "hand calibration: no dots, no correction");

        // One or two dots: only the offset.
        var two = HandCalibration.Fit(measured: rays.Take(2).ToArray(), wanted: wanted.Take(2).ToArray());
        Check.That(two.A == 1 && two.E == 1 && two.B == 0 && two.D == 0, "hand calibration: two dots give an offset only");
        double meanYaw = ((wanted[0].YawDeg - rays[0].YawDeg) + (wanted[1].YawDeg - rays[1].YawDeg)) / 2;
        Check.That(Check.Near(two.C, meanYaw), "hand calibration: the offset is the mean difference");

        // A fit no camera could give (a pinch on the wrong dot) falls back to the offset.
        HandAngles[] wild = rays.Select(r => new HandAngles(yawDeg: 2.5 * r.YawDeg + 1, pitchDeg: r.PitchDeg)).ToArray();
        var fallback = HandCalibration.Fit(measured: rays, wanted: wild);
        Check.That(fallback.A == 1 && Check.Near(fallback.C, 1), "hand calibration: a gain of 2.5 is refused and the offset kept");
        Check.That(fallback.RmsDeg(measured: rays, wanted: wild) > HandCalibration.MaxRmsDeg, "hand calibration: and it reads as too far off");
        // Dots in a line cannot pin the fit down.
        var line = new HandAngles[] { new HandAngles(yawDeg: -10, pitchDeg: 0), new HandAngles(yawDeg: 0, pitchDeg: 0), new HandAngles(yawDeg: 10, pitchDeg: 0) };
        var flat = HandCalibration.Fit(measured: line, wanted: line.Select(r => r + new HandAngles(yawDeg: 1, pitchDeg: 2)).ToArray());
        Check.That(flat.A == 1 && Check.Near(flat.C, 1) && Check.Near(flat.F, 2), "hand calibration: dots in a line give an offset only");

        // Kept as six numbers.
        Check.That(HandCalibration.FromNumbers(truth.Numbers) == truth, "hand calibration: six numbers and back");
        Check.That(HandCalibration.FromNumbers(new double[] { 1, 0, 0, 0, 1 }) == null, "hand calibration: five numbers are not one");
        Check.That(HandCalibration.FromNumbers(new double[] { 1, 0, double.NaN, 0, 1, 0 }) == null, "hand calibration: a number that is not one is refused");

        // Where a dot is, from the screen's place in the Space and the head's turn.
        var panel = new PanelPlacement(yawDeg: 21, pitchDeg: 0, widthDeg: 40, aspect: 16.0 / 9, distance: 1.4);
        var centre = HandCalibrationRun.Target(panel: panel, u: 0.5, v: 0.5, headYawDeg: 9, headPitchDeg: -2);
        Check.That(Check.Near(centre.YawDeg, 12) && Check.Near(centre.PitchDeg, 2), "hand calibration: the middle dot is the screen's middle less the head's turn");
        var corner = HandCalibrationRun.Target(panel: panel, u: 0.2, v: 0.2, headYawDeg: 9, headPitchDeg: -2);
        Check.That(Check.Near(corner.YawDeg, 12 + 0.3 * 40) && Check.Near(corner.PitchDeg, 2 + 0.3 * panel.HeightDeg), "hand calibration: the upper left dot is to the left and up");

        // A run: the middle, then four corners 20 % in; five pinches and it is done.
        Check.That(HandCalibrationRun.Dots.Length == 5, "hand calibration: five dots");
        Check.That(HandCalibrationRun.Dots[0].U == 0.5 && HandCalibrationRun.Dots[0].V == 0.5, "hand calibration: the middle first");
        Check.That(HandCalibrationRun.Dots.Skip(1).All(p => new[] { 0.2, 0.8 }.Contains(p.U) && new[] { 0.2, 0.8 }.Contains(p.V)), "hand calibration: then corners 20 % in");
        var run = new HandCalibrationRun();
        Check.That(run.Step == 0 && !run.Done && !run.Result().Ok, "hand calibration: a run starts at the first dot with nothing to keep");
        for (int i = 0; i < HandCalibrationRun.Dots.Length; i++)
        {
            var dot = HandCalibrationRun.Dots[i];
            Check.That(run.Dot is { } now ? now.U == dot.U && now.V == dot.V : false,Inv($"hand calibration: dot {i + 1} in its turn"));
            var target = HandCalibrationRun.Target(panel: panel, u: dot.U, v: dot.V, headYawDeg: 9, headPitchDeg: -2);
            // A camera that points 3 degrees right and 2 down of where the pinhole guess has it.
            run.Record(ray: target + new HandAngles(yawDeg: 3, pitchDeg: 2), target: target);
        }
        Check.That(run.Done && run.Dot == null && run.Step == 5, "hand calibration: five pinches finish the run");
        var result = run.Result();
        Check.That(result.Ok && result.RmsDeg < 1e-6, "hand calibration: a steady camera error is taken up whole");
        Check.That(Check.Near(result.Calibration.C, -3, 1e-6) && Check.Near(result.Calibration.F, -2, 1e-6), "hand calibration: 3 right and 2 down are taken off");
        run.Record(ray: HandAngles.Zero, target: HandAngles.Zero);
        Check.That(run.Step == 5, "hand calibration: a sixth pinch is not counted");
    }

    private static void HandPrimaryChecks()
    {
        HandPoint a = new HandPoint(0.3, 0.5), b = new HandPoint(0.7, 0.5);
        var p = new HandPrimary();
        Check.That(p.Choose(Array.Empty<HandPoint>(), timeMs: 0) == null, "hands: no hands, no pointer hand");
        Check.That(p.Choose(new[] { a }, timeMs: 0) == 0, "hands: the first hand seen has the pointer");
        Check.That(p.Choose(new[] { b, new HandPoint(0.31, 0.5) }, timeMs: 33) == 1, "hands: it keeps the pointer when a second hand comes, whatever the order");
        Check.That(p.Choose(new[] { b }, timeMs: 66) == null, "hands: the second hand alone is not given the pointer");
        Check.That(p.Choose(new[] { b }, timeMs: 400) == null, "hands: not while the first has been gone under 500 ms");
        Check.That(p.Choose(new[] { new HandPoint(0.33, 0.52), b }, timeMs: 433) == 0, "hands: the first hand back takes up where it left off");
        Check.That(p.Choose(new[] { b }, timeMs: 466) == null, "hands: and is lost again");
        Check.That(p.Choose(new[] { b }, timeMs: 940) == 0, "hands: after 500 ms without it the other hand takes the pointer");
        Check.That(p.Choose(new[] { a, new HandPoint(0.69, 0.5) }, timeMs: 973) == 1, "hands: and keeps it when the first comes back");

        // One hand moving fast is still the same hand.
        var q = new HandPrimary();
        _ = q.Choose(new[] { a }, timeMs: 0);
        Check.That(q.Choose(new[] { new HandPoint(0.6, 0.4) }, timeMs: 33) == 0, "hands: a lone hand that jumps is still the pointer's hand");
        Check.That(q.Choose(Array.Empty<HandPoint>(), timeMs: 66) == null && q.Choose(new[] { b }, timeMs: 99) == 0, "hands: a lone hand back after a dropped frame keeps the pointer");
        q.Reset();
        Check.That(q.Choose(new[] { b, a }, timeMs: 200) == 0, "hands: after a reset the first listed hand takes the pointer");
    }

    private static void HandGestureChecks()
    {
        // Aiming: every frame of an open hand aims the pointer where the hand does.
        var aimG = new HandGestures();
        var aiming = HandRun(aimG, from: 0, new HandInput?[] { HandFrame(0.6), HandFrame(0.6, yaw: 6), HandFrame(0.62, yaw: 7) });
        Check.That(aiming.Select(frame => frame.Count).SequenceEqual(new[] { 1, 1, 1 }), "hand gestures: one aim a frame");
        Check.That(HandLastAim(aiming[2]) == new HandAngles(yawDeg: 7, pitchDeg: 2), "hand gestures: the aim is the hand's");

        // A tap: the fingers close while the hand drifts. The aim is locked where it was before they began to close,
        // the press lands there, and nothing moves until they open.
        var tapG = new HandGestures();
        List<HandInput?> frames = Repeating(HandFrame(0.6), count: 10);
        frames.AddRange(new HandInput?[] { HandFrame(0.40, yaw: 5.3, pitch: 1.8), HandFrame(0.20, yaw: 5.6, pitch: 1.6), HandFrame(0.10, yaw: 5.8, pitch: 1.5) });
        frames.AddRange(new HandInput?[] { HandFrame(0.10, yaw: 5.8, pitch: 1.5), HandFrame(0.50, yaw: 5.7, pitch: 1.5), HandFrame(0.60, yaw: 5.6, pitch: 1.6) });
        var tap = HandRun(tapG, from: 0, frames);
        var rest = new HandAngles(yawDeg: 5, pitchDeg: 2);
        Check.That(Same(tap[10], HandEvent.Aim(rest)), $"hand gestures: the aim locks as the fingers start to close (got {Show(tap[10])})");
        Check.That(tap[11].Count == 0, "hand gestures: a locked aim does not follow the hand");
        Check.That(Same(tap[12], HandEvent.Aim(rest), HandEvent.Press(rest)), $"hand gestures: the press lands where the aim was before the pinch (got {Show(tap[12])})");
        Check.That(tap[13].Count == 0 && tap[14].Count == 0, "hand gestures: a held pinch does not move the pointer, and one open frame does not let go");
        Check.That(Same(tap[15], HandEvent.Release), "hand gestures: two open frames let go");
        Check.That(HandPresses(tap).Count == 1 && HandCount(tap, HandEvent.Release) == 1, "hand gestures: a tap is one press and one release");
        Check.That(tapG.Phase == HandGesturesPhase.Idle, "hand gestures: back to aiming after a tap");
        // Afterwards the pointer glides from the locked spot onto the hand; it does not jump.
        var after = HandRun(tapG, from: 16 * HandFrameMs, Repeating(HandFrame(0.6, yaw: 5.6, pitch: 1.6), count: 12));
        HandAngles firstAfter = HandLastAim(after[0]) ?? HandAngles.Zero;
        HandAngles lastAfter = HandLastAim(after[11]) ?? HandAngles.Zero;
        Check.That(firstAfter.Distance(to: rest) < 0.5, "hand gestures: the first aim after a tap is still near the locked spot");
        Check.That(lastAfter.Distance(to: new HandAngles(yawDeg: 5.6, pitchDeg: 1.6)) < 0.02, "hand gestures: and it ends on the hand");

        // A drag: pinched, held 250 ms and moved 2 degrees. The pointer then follows the hand from the locked spot.
        var dragG = new HandGestures();
        List<HandInput?> drag = Repeating(HandFrame(0.6), count: 6);
        drag.Add(HandFrame(0.10)); // touching: pinched at once
        for (int i = 1; i <= 12; i++) drag.Add(HandFrame(0.10, yaw: 5 + 0.5 * (double)i));
        drag.AddRange(new HandInput?[] { HandFrame(0.6, yaw: 11), HandFrame(0.6, yaw: 11) });
        var dragged = HandRun(dragG, from: 0, drag);
        Check.That(Same(dragged[6], HandEvent.Aim(rest), HandEvent.Press(rest)), "hand gestures: a quick pinch presses where the hand aimed");
        Check.That(Enumerable.Range(7, 7).All(n => dragged[n].Count == 0), "hand gestures: under 250 ms a moving pinch is not yet a drag");
        Check.That(Same(dragged[14], HandEvent.Aim(rest)), $"hand gestures: the drag starts from the pressed spot (got {Show(dragged[14])})");
        // Frame 14 is 8 frames after the press (4 degrees on); each frame after it moves the pointer 0.5 further.
        Check.That(Same(dragged[15], HandEvent.Aim(new HandAngles(yawDeg: 5.5, pitchDeg: 2))), "hand gestures: a drag follows the hand, one for one");
        Check.That(Same(dragged[18], HandEvent.Aim(new HandAngles(yawDeg: 7, pitchDeg: 2))), "hand gestures: and keeps following");
        Check.That(dragged[19].Count == 1 && dragged[20].Count > 0 && dragged[20][^1] == HandEvent.Release, "hand gestures: opening the fingers drops it");
        Check.That(HandPresses(dragged).Count == 1 && HandCount(dragged, HandEvent.Release) == 1, "hand gestures: a drag is one press and one release");

        // A big move at once is a drag whatever the clock says.
        var farG = new HandGestures();
        var far = HandRun(farG, from: 0, new HandInput?[] { HandFrame(0.6), HandFrame(0.6), HandFrame(0.1), HandFrame(0.1, yaw: 12) });
        Check.That(Same(far[3], HandEvent.Aim(rest)) && farG.Phase == HandGesturesPhase.Dragging, "hand gestures: 6 degrees at once is a drag");

        // Palm, then a short pinch: the launcher, and never a click.
        var palmG = new HandGestures();
        List<HandInput?> palm = Repeating(HandFrame(0.6, palm: true), count: 8); // 233 ms facing
        palm.AddRange(new HandInput?[] { HandFrame(0.1, palm: true), HandFrame(0.1, palm: true), HandFrame(0.6, palm: true), HandFrame(0.6, palm: true) });
        var palmed = HandRun(palmG, from: 0, palm);
        Check.That(palmG.Phase == HandGesturesPhase.Idle && HandCount(palmed, HandEvent.ToggleLauncher) == 1, "hand gestures: palm then a short pinch toggles the launcher");
        Check.That(Same(palmed[11], HandEvent.ToggleLauncher), "hand gestures: the launcher toggles as the fingers open");
        Check.That(HandPresses(palmed).Count == 0 && HandCount(palmed, HandEvent.Release) == 0, "hand gestures: a palm pinch never clicks");
        Check.That(palmed[8].Count == 0 && palmed[9].Count == 0, "hand gestures: the pointer is held during a palm pinch");

        // The same pinch held 650 ms: recentre, once, and no launcher on letting go.
        var holdG = new HandGestures();
        List<HandInput?> hold = Repeating(HandFrame(0.6, palm: true), count: 8);
        hold.AddRange(Repeating(HandFrame(0.1, palm: true), count: 24)); // 800 ms
        hold.AddRange(new HandInput?[] { HandFrame(0.6, palm: true), HandFrame(0.6, palm: true) });
        var held = HandRun(holdG, from: 0, hold);
        Check.That(HandCount(held, HandEvent.Recentre) == 1 && HandCount(held, HandEvent.ToggleLauncher) == 0, "hand gestures: a palm pinch held recentres and does not toggle the launcher");
        int recentredAt = held.FindIndex(frame => frame.Contains(HandEvent.Recentre));
        // Pinched at frame 8; 650 ms later is 19.5 frames on.
        Check.That(recentredAt == 28, $"hand gestures: recentre comes at 650 ms (frame {recentredAt})");
        Check.That(HandPresses(held).Count == 0, "hand gestures: a held palm pinch never clicks");

        // A palm only just turned (under 200 ms) does not count: the pinch is a click.
        var briefG = new HandGestures();
        List<HandInput?> brief = Repeating(HandFrame(0.6), count: 6);
        brief.AddRange(Repeating(HandFrame(0.6, palm: true), count: 4)); // 100 ms facing
        brief.AddRange(new HandInput?[] { HandFrame(0.1, palm: true) });
        var briefly = HandRun(briefG, from: 0, brief);
        Check.That(HandPresses(briefly).Count == 1, "hand gestures: a palm turned for under 200 ms pinches as a click");
        Check.That(!new HandGestures().PalmReady(timeMs: 0), "hand gestures: no palm, not ready");

        // A hand that turns its palm away loses the palm's 200 ms.
        var awayG = new HandGestures();
        List<HandInput?> away = Repeating(HandFrame(0.6, palm: true), count: 8);
        away.AddRange(new HandInput?[] { HandFrame(0.6, palm: false), HandFrame(0.6, palm: true), HandFrame(0.1, palm: true) });
        Check.That(HandPresses(HandRun(awayG, from: 0, away)).Count == 1, "hand gestures: the palm's 200 ms start again after it turns away");

        // The hand lost while pinched: held through a short gap, let go at 500 ms, then quiet.
        var lostG = new HandGestures();
        var lost = new List<HandInput?> { HandFrame(0.6), HandFrame(0.6), HandFrame(0.1) };
        lost.AddRange(Repeating(null, count: 20));
        var gone = HandRun(lostG, from: 0, lost);
        Check.That(Enumerable.Range(3, 14).All(n => gone[n].Count == 0), "hand gestures: a pinch is held through a gap under 500 ms");
        // Last seen at frame 2; 500 ms later is frame 17.
        Check.That(Same(gone[17], HandEvent.Release, HandEvent.HandLost), $"hand gestures: a lost hand lets go at 500 ms (got {Show(gone[17])})");
        Check.That(Enumerable.Range(18, 5).All(n => gone[n].Count == 0), "hand gestures: and says so once");
        Check.That(lostG.Phase == HandGesturesPhase.Idle, "hand gestures: a lost hand starts afresh");
        var backG = new HandGestures();
        var back = new List<HandInput?> { HandFrame(0.6), HandFrame(0.6), HandFrame(0.1) };
        back.AddRange(Repeating(null, count: 6)); // 200 ms gap
        back.AddRange(new HandInput?[] { HandFrame(0.1), HandFrame(0.6), HandFrame(0.6) });
        var returned = HandRun(backG, from: 0, back);
        Check.That(HandPresses(returned).Count == 1 && Same(returned[11], HandEvent.Release), "hand gestures: a pinch survives dropped frames and ends when the fingers open");
        var idleG = new HandGestures();
        var idle = new List<HandInput?> { HandFrame(0.6) };
        idle.AddRange(Repeating(null, count: 18));
        Check.That(HandRun(idleG, from: 0, idle).SelectMany(frame => frame).Where(ev => ev != HandEvent.Aim(rest)).SequenceEqual(new[] { HandEvent.HandLost }), "hand gestures: an open hand lost is lost without a release");
        Check.That(new HandGestures().Update(null, timeMs: 5000).Count == 0, "hand gestures: no hand yet, nothing to do");

        // A lock that never becomes a pinch lets go when the fingers reopen, and the pointer glides on.
        var abortG = new HandGestures();
        List<HandInput?> abort = Repeating(HandFrame(0.6), count: 6);
        abort.AddRange(new HandInput?[] { HandFrame(0.40), HandFrame(0.40, yaw: 5.2), HandFrame(0.55, yaw: 5.4), HandFrame(0.6, yaw: 5.4) });
        var aborted = HandRun(abortG, from: 0, abort);
        Check.That(Same(aborted[6], HandEvent.Aim(rest)) && aborted[7].Count == 0, "hand gestures: half closed fingers lock the aim");
        Check.That(abortG.Phase == HandGesturesPhase.Idle && HandPresses(aborted).Count == 0, "hand gestures: reopened fingers end the lock without a click");
        Check.That((HandLastAim(aborted[9]) ?? HandAngles.Zero).Distance(to: rest) < 0.4, "hand gestures: and the pointer starts again from the locked spot");

        // A hand that moves on half closed is aiming: the lock lets go after 250 ms and 2.5 degrees.
        var moveG = new HandGestures();
        List<HandInput?> move = Repeating(HandFrame(0.6), count: 6);
        move.Add(HandFrame(0.40));
        for (int i = 1; i <= 12; i++) move.Add(HandFrame(0.40, yaw: 5 + 0.5 * (double)i));
        var movedOn = HandRun(moveG, from: 0, move);
        Check.That(moveG.Phase == HandGesturesPhase.Idle, "hand gestures: a half closed hand that moves on is let go");
        Check.That(Enumerable.Range(7, 7).All(n => movedOn[n].Count == 0) && movedOn[15].Count != 0, "hand gestures: after 250 ms, not before");

        // A resting half closed hand cannot hold the pointer for ever.
        var restG = new HandGestures();
        List<HandInput?> resting = Repeating(HandFrame(0.6), count: 6);
        resting.AddRange(Repeating(HandFrame(0.40), count: 32)); // over 900 ms
        var rested = HandRun(restG, from: 0, resting);
        Check.That(restG.Phase == HandGesturesPhase.Idle && rested.Count > 0 && rested[^1].Count == 1, "hand gestures: a lock lets go after 900 ms");

        // When the head aims (look and pinch) the aim is always straight ahead and a drag is measured on the head.
        var lookG = new HandGestures();
        HandInput Look(double ratio, double head) =>
            new HandInput(ratio: ratio, palmFacing: false, aim: HandAngles.Zero, motion: new HandAngles(yawDeg: head, pitchDeg: 0));
        var looking = new List<HandInput?> { Look(0.6, head: 0), Look(0.6, head: 0), Look(0.1, head: 0) };
        for (int i = 1; i <= 10; i++) looking.Add(Look(0.1, head: (double)i));
        var looked = HandRun(lookG, from: 0, looking);
        Check.That(Same(looked[2], HandEvent.Aim(HandAngles.Zero), HandEvent.Press(HandAngles.Zero)), "hand gestures: with the head aiming, a pinch presses straight ahead");
        Check.That(lookG.Phase == HandGesturesPhase.Dragging, "hand gestures: turning the head while pinched is a drag");
        Check.That(looked.SelectMany(frame => frame).All(ev =>
        {
            if (ev.Kind == HandEventKind.Aim) return ev.Angles == HandAngles.Zero;
            return true;
        }), "hand gestures: with the head aiming, every aim is straight ahead");
        Check.That(!new HandGestures().HoldsPointer, "hand gestures: an idle hand does not hold the pointer");

        // Started afresh with the fingers closed (a calibration's last pinch): nothing until they have opened, then
        // pinches count again.
        var freshG = new HandGestures();
        freshG.Reset(waitForOpen: true);
        List<HandInput?> fresh = Repeating(HandFrame(0.1), count: 6);
        var closedStill = HandRun(freshG, from: 0, fresh);
        Check.That(HandPresses(closedStill).Count == 0 && freshG.Phase == HandGesturesPhase.Idle, "hand gestures: fingers closed at a fresh start do not click");
        fresh = new List<HandInput?> { HandFrame(0.6), HandFrame(0.6), HandFrame(0.1), HandFrame(0.1) };
        Check.That(HandPresses(HandRun(freshG, from: 6 * HandFrameMs, fresh)).Count == 1, "hand gestures: once they have opened, a pinch clicks again");
    }
}
