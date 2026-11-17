using System;
using System.Collections.Generic;

namespace PocketXR.Core;

/// <summary>
/// The tour's steps, one idea each, in the order shown (the Android app's Tour.kt, for a PC: screens that are real
/// displays, the PC's pointer, a notification area app).
/// </summary>
public enum TourStep
{
    Welcome,
    Setup,
    Screens,
    Look,
    Arrange,
    Depth,
    Menu,
    Done,
}

public static class TourStepExt
{
    /// <summary>The steps in the order declared (Swift's CaseIterable).</summary>
    public static readonly TourStep[] AllCases =
    {
        TourStep.Welcome, TourStep.Setup, TourStep.Screens, TourStep.Look, TourStep.Arrange, TourStep.Depth,
        TourStep.Menu, TourStep.Done,
    };

    /// <summary>The step's place, 0 to 7 (Swift's Int raw value).</summary>
    public static int RawValue(this TourStep step) => (int)step;

    public static TourStep? FromRawValue(int raw) => raw >= 0 && raw < AllCases.Length ? AllCases[raw] : null;

    /// <summary>As shown and spoken: 1 to 8.</summary>
    public static int Number(this TourStep step) => step.RawValue() + 1;

    /// <summary>A short name for file names (the tour's pictures rendered to files).</summary>
    public static string Slug(this TourStep step)
    {
        switch (step)
        {
            case TourStep.Welcome: return "welcome";
            case TourStep.Setup: return "setup";
            case TourStep.Screens: return "screens";
            case TourStep.Look: return "look";
            case TourStep.Arrange: return "arrange";
            case TourStep.Depth: return "depth";
            case TourStep.Menu: return "menu";
            default: return "done";
        }
    }
}

/// <summary>
/// What is still to do at the end of the tour. (Tour.Remaining on the Mac, an enum inside Tour; C# cannot have a type
/// and a method of one name in a class, so it stands beside it.)
/// </summary>
public enum TourRemaining
{
    /// <summary>
    /// macOS has not been told Pocket XR may capture its own screens. (The Mac's Screen Recording permission; kept so
    /// the two read side by side.)
    /// </summary>
    ScreenRecording,
    PlugInGlasses,
    /// <summary>Glasses connected and Screen Recording allowed, but no screen is showing a picture on them yet.</summary>
    ScreensNotShowing,
}

/// <summary>
/// What is known for these glasses on a Mac. (Tour.Support on the Mac, an enum inside Tour; C# cannot have a type and
/// a method of one name in a class, so it stands beside it. The words on each case are the Mac's record of what was
/// run there.)
/// </summary>
public enum TourSupport
{
    /// <summary>Run on a Mac by the project: the Aura.</summary>
    Tested,
    /// <summary>The One family: the same head tracking as the Aura, not tried on a Mac.</summary>
    SameTrackingUntested,
    /// <summary>The Air family and VITURE: screens show; head tracking is an experimental switch, untested on a Mac.</summary>
    ExperimentalTracking,
    /// <summary>Anything else: screens, without head tracking.</summary>
    ScreensOnly,
}

/// <summary>
/// The tour new users see and can reopen whenever they want: its steps, when it opens by itself, what is still to do
/// at the end, what is said for the glasses plugged in, and the beats of its pictures (so the words can follow the
/// picture). The window only draws what this returns.
/// </summary>
public static class Tour
{
    public static readonly TourStep[] Steps = TourStepExt.AllCases;
    public static int Count => Steps.Length;

    /// <summary>The step [by] steps on from [from], kept inside the tour.</summary>
    public static TourStep Move(TourStep from, int by)
    {
        int i = Math.Min(Math.Max(from.RawValue() + by, 0), Steps.Length - 1);
        return Steps[i];
    }

    public static bool IsFirst(TourStep step) => step == Steps[0];
    public static bool IsLast(TourStep step) => step == Steps[^1];

    // ---- when it opens ----

    /// <summary>The tour opens by itself only until it has been seen once. Closing or skipping it counts as seen.</summary>
    public static bool OpensByItself(bool tourSeen) => !tourSeen;

    // ---- what is still to do at the end ----

    /// <summary>
    /// What is still open, in the order to do it. Glasses that are connected with nothing showing on them are not
    /// ready, so the last step never says "look up" at an empty view. Opening at login is optional and never listed.
    /// </summary>
    public static TourRemaining[] Remaining(bool glassesConnected, bool screenRecordingAllowed, int capturing)
    {
        var list = new List<TourRemaining>();
        if (!screenRecordingAllowed) list.Add(TourRemaining.ScreenRecording);
        if (!glassesConnected)
        {
            list.Add(TourRemaining.PlugInGlasses);
        }
        else if (screenRecordingAllowed && capturing <= 0)
        {
            list.Add(TourRemaining.ScreensNotShowing);
        }
        return list.ToArray();
    }

    public static bool Ready(bool glassesConnected, bool screenRecordingAllowed, int capturing) =>
        Remaining(glassesConnected: glassesConnected, screenRecordingAllowed: screenRecordingAllowed, capturing: capturing).Length == 0;

    // ---- what is known for these glasses on a Mac ----

    /// <summary>What can be said for [family], or null with no glasses plugged in.</summary>
    public static TourSupport? Support(GlassesFamily? family)
    {
        if (family is not GlassesFamily f) return null;
        switch (f)
        {
            case GlassesFamily.Aura: return TourSupport.Tested;
            case GlassesFamily.One: return TourSupport.SameTrackingUntested;
            case GlassesFamily.Air:
            case GlassesFamily.Viture:
                return TourSupport.ExperimentalTracking;
            default: return TourSupport.ScreensOnly;
        }
    }

    // ---- the pictures' beats ----

    public const double ScreensLoop = 9.0;
    public const double LookLoop = 9.0;
    public const double ArrangeLoop = 9.0;
    public const double DepthLoop = 11.0;
    /// <summary>When the Depth picture turns from 3D to Steady mode's row.</summary>
    public const double DepthRowAt = 5.6;
    public const double MenuLoop = 11.0;
    /// <summary>A looping picture fades in over this long and out over [LoopFadeOut] before its end, so a loop never jumps.</summary>
    public const double LoopFadeIn = 0.4;
    public const double LoopFadeOut = 0.45;

    /// <summary>The length of [step]'s loop in seconds, or null for a picture that plays once and then only drifts.</summary>
    public static double? Loop(TourStep step)
    {
        switch (step)
        {
            case TourStep.Screens: return ScreensLoop;
            case TourStep.Look: return LookLoop;
            case TourStep.Arrange: return ArrangeLoop;
            case TourStep.Depth: return DepthLoop;
            case TourStep.Menu: return MenuLoop;
            default: return null;
        }
    }

    /// <summary>[t] seconds since a picture came up, as seconds into its loop of [length].</summary>
    public static double LoopTime(double t, double length)
    {
        if (!(length > 0)) return 0;
        double r = t % length;
        return r < 0 ? r + length : r;
    }

    /// <summary>The second that shows each picture's point, for Reduce Motion (the clock stays there) and for stills.</summary>
    public static double Poster(TourStep step)
    {
        switch (step)
        {
            case TourStep.Welcome: return 3.1;
            case TourStep.Setup: return 1.2;
            case TourStep.Screens: return 4.85;
            case TourStep.Look: return 6.0;
            case TourStep.Arrange: return 6.8;
            case TourStep.Depth: return 3.2;
            case TourStep.Menu: return 2.3;
            default: return 3.0;
        }
    }

    /// <summary>Three more moments of each looping picture, for stills: before, during and after its main beat.</summary>
    public static double[] Stills(TourStep step)
    {
        switch (step)
        {
            case TourStep.Screens: return new[] { 1.2, 2.2, 7.0 };
            case TourStep.Look: return new[] { 0.6, 3.2, 4.4 };
            case TourStep.Arrange: return new[] { 1.6, 3.8, 5.8 };
            case TourStep.Depth: return new[] { 0.7, 2.4, 7.6 };
            case TourStep.Menu: return new[] { 1.0, 4.4, 7.0 };
            default: return Array.Empty<double>();
        }
    }

    /// <summary>
    /// Which of a step's lines the picture is showing [t] seconds after it came up, so the words light the same
    /// line; -1 for none.
    /// </summary>
    public static int Phase(TourStep step, double t)
    {
        switch (step)
        {
            case TourStep.Screens:
            {
                // The pointer crosses to the floating screen, then a window is carried across.
                double lt = LoopTime(t, ScreensLoop);
                if (lt < 2.6) return 0;
                return lt < 6.4 ? 1 : -1;
            }
            case TourStep.Arrange:
            {
                // Bigger, nearer, curved, each in turn (only until the user touches a control).
                double lt = LoopTime(t, ArrangeLoop);
                for (int i = 0; i < ArrangeBeats.Length; i++)
                {
                    double at = ArrangeBeats[i];
                    if (lt >= at - 0.2 && lt < at + ArrangeBeat + 0.6) return i;
                }
                return -1;
            }
            case TourStep.Depth:
                return LoopTime(t, DepthLoop) < DepthRowAt ? 0 : 1;
            case TourStep.Menu:
            {
                // The menu opens; the highlight reaches "Quit and put the glasses back"; the cable is unplugged.
                double lt = LoopTime(t, MenuLoop);
                if (lt >= 0.3 && lt < 3.4) return 0;
                if (lt >= 3.4 && lt < 5.2) return 1;
                if (lt >= 5.5 && lt < 9.5) return 2;
                return -1;
            }
            default:
                return -1;
        }
    }

    /// <summary>When the Arrange picture's own demonstration starts each change (bigger, nearer, curved), and how long one takes.</summary>
    public static readonly double[] ArrangeBeats = { 0.8, 3.0, 5.2 };
    public const double ArrangeBeat = 1.2;

    /// <summary>How far each of the demonstration's changes has got at [t], 0 to 1, not eased.</summary>
    public static (double Bigger, double Nearer, double Curved) ArrangeDemo(double t)
    {
        double lt = LoopTime(t, ArrangeLoop);
        double K(double at) => Math.Min(Math.Max((lt - at) / ArrangeBeat, 0), 1);
        return (K(ArrangeBeats[0]), K(ArrangeBeats[1]), K(ArrangeBeats[2]));
    }
}
