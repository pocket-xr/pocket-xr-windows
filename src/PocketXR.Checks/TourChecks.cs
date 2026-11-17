using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

public static class TourChecks
{
    private static bool Near(double a, double b, double tolerance = 1e-6) => Check.Near(a, b, tolerance);

    /// <summary>A number as Swift prints it in a message: 6.0, not 6.</summary>
    private static string Shown(double v)
    {
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    public static void Run()
    {
        // The steps, in order, numbered 1 to 8.
        TourStep[] steps = Tour.Steps;
        Check.That(steps.SequenceEqual(new[] { TourStep.Welcome, TourStep.Setup, TourStep.Screens, TourStep.Look, TourStep.Arrange, TourStep.Depth, TourStep.Menu, TourStep.Done }), "eight steps in the order shown");
        Check.That(Tour.Count == 8, "eight steps");
        Check.That(steps.Select(s => s.Number()).SequenceEqual(Enumerable.Range(1, 8)), "numbered 1 to 8");
        Check.That(new HashSet<string>(steps.Select(s => s.Slug())).Count == steps.Length, "every step has its own file name");

        // Moving stays inside the tour.
        Check.That(Tour.Move(TourStep.Welcome, by: 1) == TourStep.Setup, "Next goes one on");
        Check.That(Tour.Move(TourStep.Setup, by: -1) == TourStep.Welcome, "Back goes one back");
        Check.That(Tour.Move(TourStep.Welcome, by: -1) == TourStep.Welcome, "Back on the first step stays");
        Check.That(Tour.Move(TourStep.Done, by: 1) == TourStep.Done, "Next on the last step stays");
        Check.That(Tour.Move(TourStep.Setup, by: 99) == TourStep.Done && Tour.Move(TourStep.Menu, by: -99) == TourStep.Welcome, "a long move is clamped");
        Check.That(Tour.IsFirst(TourStep.Welcome) && !Tour.IsFirst(TourStep.Setup), "the first step");
        Check.That(Tour.IsLast(TourStep.Done) && !Tour.IsLast(TourStep.Menu), "the last step");

        // It opens by itself only until it has been seen.
        Check.That(Tour.OpensByItself(tourSeen: false), "a new install gets the tour");
        Check.That(!Tour.OpensByItself(tourSeen: true), "never by itself once seen");
        Check.That(!new SpaceSettings().TourSeen, "a fresh settings file has not seen the tour");

        // What is left at the end.
        Check.That(Tour.Remaining(glassesConnected: true, screenRecordingAllowed: true, capturing: 2).Length == 0, "everything ready");
        Check.That(Tour.Ready(glassesConnected: true, screenRecordingAllowed: true, capturing: 1), "ready with one screen showing");
        Check.That(Tour.Remaining(glassesConnected: false, screenRecordingAllowed: false, capturing: 0).SequenceEqual(new[] { TourRemaining.ScreenRecording, TourRemaining.PlugInGlasses }),
                   "nothing done: Screen Recording first, then the glasses");
        Check.That(Tour.Remaining(glassesConnected: false, screenRecordingAllowed: true, capturing: 0).SequenceEqual(new[] { TourRemaining.PlugInGlasses }), "only the glasses left");
        Check.That(Tour.Remaining(glassesConnected: true, screenRecordingAllowed: false, capturing: 0).SequenceEqual(new[] { TourRemaining.ScreenRecording }),
                   "without Screen Recording nothing can show, so only that is listed");
        Check.That(Tour.Remaining(glassesConnected: true, screenRecordingAllowed: true, capturing: 0).SequenceEqual(new[] { TourRemaining.ScreensNotShowing }),
                   "connected glasses with nothing on them are not ready");
        Check.That(!Tour.Ready(glassesConnected: true, screenRecordingAllowed: true, capturing: 0), "never \"look up\" at an empty view");
        Check.That(Tour.Remaining(glassesConnected: false, screenRecordingAllowed: true, capturing: 2).SequenceEqual(new[] { TourRemaining.PlugInGlasses }),
                   "screens captured with no glasses still need the glasses");

        // What is said for each family: only the Aura has been run on a Mac.
        Check.That(Tour.Support(null) == null, "no glasses, no support line");
        Check.That(Tour.Support(GlassesFamily.Aura) == TourSupport.Tested, "the Aura is tested on a Mac");
        Check.That(Tour.Support(GlassesFamily.One) == TourSupport.SameTrackingUntested, "the One family is untested on a Mac");
        Check.That(Tour.Support(GlassesFamily.Air) == TourSupport.ExperimentalTracking && Tour.Support(GlassesFamily.Viture) == TourSupport.ExperimentalTracking,
                   "the Air family and VITURE: experimental head tracking");
        Check.That(Tour.Support(GlassesFamily.Other) == TourSupport.ScreensOnly, "other glasses: screens only");
        GlassesFamily[] tested = new[] { GlassesFamily.Aura, GlassesFamily.One, GlassesFamily.Air, GlassesFamily.Viture, GlassesFamily.Other }
            .Where(f => Tour.Support(f) == TourSupport.Tested).ToArray();
        Check.That(tested.SequenceEqual(new[] { GlassesFamily.Aura }), "nothing but the Aura is called tested");

        // The pictures: every poster and still lies inside its loop and outside the fade, so a still is never blank.
        foreach (TourStep step in steps)
        {
            double poster = Tour.Poster(step);
            Check.That(poster >= 0, $"{step.Slug()}: the poster is a moment of the picture");
            if (Tour.Loop(step) is double length)
            {
                Check.That(length >= 5 && length <= 11, $"{step.Slug()}: loops run 5 to 11 seconds");
                double[] moments = new[] { poster }.Concat(Tour.Stills(step)).ToArray();
                Check.That(Tour.Stills(step).Length == 3, $"{step.Slug()}: three more stills");
                Check.That(new HashSet<double>(moments).Count == moments.Length, $"{step.Slug()}: the stills are different moments");
                foreach (double m in moments)
                {
                    Check.That(m > Tour.LoopFadeIn && m < length - Tour.LoopFadeOut, $"{step.Slug()}: {Shown(m)} s is outside the fade");
                }
                // The words follow the picture the same way on every pass.
                foreach (double m in moments)
                {
                    Check.That(Tour.Phase(step, m) == Tour.Phase(step, m + length * 3), $"{step.Slug()}: the same beat on every loop");
                }
            }
            else
            {
                Check.That(Tour.Stills(step).Length == 0, $"{step.Slug()}: no loop, no extra stills");
                Check.That(Tour.Phase(step, poster) == -1, $"{step.Slug()}: no line is lit by the picture");
            }
        }
        Check.That(Near(Tour.LoopTime(20.5, 9), 2.5), "loop time wraps");
        Check.That(Near(Tour.LoopTime(-1, 9), 8), "loop time is never negative");

        // Each poster shows the picture's point.
        Check.That(Tour.Phase(TourStep.Screens, Tour.Poster(TourStep.Screens)) == 1, "Screens: the poster shows a window carried across");
        Check.That(Tour.Phase(TourStep.Screens, 1.0) == 0, "Screens: first the pointer crosses");
        Check.That(Tour.Phase(TourStep.Screens, 8.0) == -1, "Screens: nothing lit in the rest");
        Check.That(Tour.Phase(TourStep.Depth, Tour.Poster(TourStep.Depth)) == 0, "Depth: the poster shows 3D");
        Check.That(Tour.Phase(TourStep.Depth, Tour.DepthRowAt + 0.1) == 1, "Depth: then Steady mode's row");
        Check.That(Tour.Phase(TourStep.Menu, Tour.Poster(TourStep.Menu)) == 0, "Menu: the poster shows the menu open");
        Check.That(Tour.Phase(TourStep.Menu, 4.0) == 1 && Tour.Phase(TourStep.Menu, 7.0) == 2, "Menu: then Quit, then the cable unplugged");
        Check.That(Tour.Phase(TourStep.Look, 4.0) == -1, "Look: its one line ticks only when Recentre is really used");

        // Arrange's own demonstration: bigger, then nearer, then curved, all three done at the poster.
        (double Bigger, double Nearer, double Curved) before = Tour.ArrangeDemo(0.2);
        Check.That(before.Bigger == 0 && before.Nearer == 0 && before.Curved == 0, "Arrange: starts from the plain arrangement");
        (double Bigger, double Nearer, double Curved) mid = Tour.ArrangeDemo(Tour.ArrangeBeats[0] + Tour.ArrangeBeat / 2);
        Check.That(Near(mid.Bigger, 0.5) && mid.Nearer == 0 && mid.Curved == 0, "Arrange: one change at a time");
        (double Bigger, double Nearer, double Curved) atPoster = Tour.ArrangeDemo(Tour.Poster(TourStep.Arrange));
        Check.That(atPoster.Bigger == 1 && atPoster.Nearer == 1 && atPoster.Curved == 1, "Arrange: the poster shows all three changes");
        Check.That(Tour.Phase(TourStep.Arrange, 1.4) == 0 && Tour.Phase(TourStep.Arrange, 3.6) == 1 && Tour.Phase(TourStep.Arrange, 5.8) == 2,
                   "Arrange: the control being shown is lit");
        Check.That(Tour.Phase(TourStep.Arrange, 8.5) == -1, "Arrange: nothing lit in the rest");
    }
}
