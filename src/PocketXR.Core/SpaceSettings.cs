using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PocketXR.Core;

/// <summary>How the screens follow the head, as Pocket XR's Tracking choice has it.</summary>
public enum TrackingMode
{
    /// <summary>The screens stay where they are in the room as the head turns (Pocket XR's default).</summary>
    BodyAnchor,
    /// <summary>The screens drift after the head, so they are always nearly in front.</summary>
    SmoothFollow,
}

public static partial class TrackingModeExt
{
    public static readonly TrackingMode[] AllCases = { TrackingMode.BodyAnchor, TrackingMode.SmoothFollow };

    public static string RawValue(this TrackingMode value) => value switch
    {
        TrackingMode.BodyAnchor => "bodyAnchor",
        _ => "smoothFollow",
    };

    public static TrackingMode? FromRawValue(string? raw) => raw switch
    {
        "bodyAnchor" => TrackingMode.BodyAnchor,
        "smoothFollow" => TrackingMode.SmoothFollow,
        _ => null,
    };
}

/// <summary>
/// How 1 to 3 screens are placed. Every case works for any count: with one screen `row` is "Center" and the two
/// "main" layouts fall back to it.
/// </summary>
public enum ArrangeLayout
{
    /// <summary>Side by side on an arc (Center, Side by side, Three across).</summary>
    Row,
    /// <summary>The first screen large in front, the others smaller beside it (Main + side, Main + two stacked).</summary>
    MainSide,
    /// <summary>One above the other.</summary>
    Stacked,
    /// <summary>Far away and large, like a cinema screen (the others beside it).</summary>
    Theater,
}

public static partial class ArrangeLayoutExt
{
    public static readonly ArrangeLayout[] AllCases =
    {
        ArrangeLayout.Row, ArrangeLayout.MainSide, ArrangeLayout.Stacked, ArrangeLayout.Theater,
    };

    public static string RawValue(this ArrangeLayout value) => value switch
    {
        ArrangeLayout.Row => "row",
        ArrangeLayout.MainSide => "mainSide",
        ArrangeLayout.Stacked => "stacked",
        _ => "theater",
    };

    public static ArrangeLayout? FromRawValue(string? raw) => raw switch
    {
        "row" => ArrangeLayout.Row,
        "mainSide" => ArrangeLayout.MainSide,
        "stacked" => ArrangeLayout.Stacked,
        "theater" => ArrangeLayout.Theater,
        _ => null,
    };
}

/// <summary>The focused screen's outline colour, Pocket XR's Glow colour list.</summary>
public enum GlowColor
{
    Blue,
    White,
    Purple,
    Pink,
    Green,
    Amber,
    Red,
}

public static partial class GlowColorExt
{
    public static readonly GlowColor[] AllCases =
    {
        GlowColor.Blue, GlowColor.White, GlowColor.Purple, GlowColor.Pink, GlowColor.Green, GlowColor.Amber,
        GlowColor.Red,
    };

    public static string RawValue(this GlowColor value) => value switch
    {
        GlowColor.Blue => "blue",
        GlowColor.White => "white",
        GlowColor.Purple => "purple",
        GlowColor.Pink => "pink",
        GlowColor.Green => "green",
        GlowColor.Amber => "amber",
        _ => "red",
    };

    public static GlowColor? FromRawValue(string? raw) => raw switch
    {
        "blue" => GlowColor.Blue,
        "white" => GlowColor.White,
        "purple" => GlowColor.Purple,
        "pink" => GlowColor.Pink,
        "green" => GlowColor.Green,
        "amber" => GlowColor.Amber,
        "red" => GlowColor.Red,
        _ => null,
    };
}

/// <summary>The fade to black at each eye's edges.</summary>
public enum EdgeFade
{
    Off,
    Soft,
    Wide,
}

public static partial class EdgeFadeExt
{
    public static readonly EdgeFade[] AllCases = { EdgeFade.Off, EdgeFade.Soft, EdgeFade.Wide };

    public static string RawValue(this EdgeFade value) => value switch
    {
        EdgeFade.Off => "off",
        EdgeFade.Soft => "soft",
        _ => "wide",
    };

    public static EdgeFade? FromRawValue(string? raw) => raw switch
    {
        "off" => EdgeFade.Off,
        "soft" => EdgeFade.Soft,
        "wide" => EdgeFade.Wide,
        _ => null,
    };
}

/// <summary>When a game controller's buttons and sticks change the Space.</summary>
public enum ControllerMode
{
    /// <summary>Never.</summary>
    Off,
    /// <summary>Only while the left stick is held pressed in, so a game on a floating screen keeps the controller to itself.</summary>
    Held,
    /// <summary>Always.</summary>
    Always,
}

public static partial class ControllerModeExt
{
    public static readonly ControllerMode[] AllCases = { ControllerMode.Off, ControllerMode.Held, ControllerMode.Always };

    public static string RawValue(this ControllerMode value) => value switch
    {
        ControllerMode.Off => "off",
        ControllerMode.Held => "held",
        _ => "always",
    };

    public static ControllerMode? FromRawValue(string? raw) => raw switch
    {
        "off" => ControllerMode.Off,
        "held" => ControllerMode.Held,
        "always" => ControllerMode.Always,
        _ => null,
    };
}

/// <summary>How the hands aim the pointer.</summary>
public enum HandAim
{
    /// <summary>The hand aims, a pinch clicks.</summary>
    HandRay,
    /// <summary>The head aims (where you look), a pinch anywhere clicks.</summary>
    LookAndPinch,
}

public static partial class HandAimExt
{
    public static readonly HandAim[] AllCases = { HandAim.HandRay, HandAim.LookAndPinch };

    public static string RawValue(this HandAim value) => value switch
    {
        HandAim.HandRay => "handRay",
        _ => "lookAndPinch",
    };

    public static HandAim? FromRawValue(string? raw) => raw switch
    {
        "handRay" => HandAim.HandRay,
        "lookAndPinch" => HandAim.LookAndPinch,
        _ => null,
    };
}

/// <summary>
/// A saved arrangement of the screens. A class here (it holds lists): compares by value, and <see cref="Copy"/> gives
/// the copy a Swift struct makes on assignment.
/// </summary>
public sealed class SpacePreset : IEquatable<SpacePreset>
{
    public Guid Id;
    public string Name;
    public ArrangeLayout Layout;
    public double WidthDeg;
    public double Distance;
    public double Curve;
    /// <summary>Where the Space had been moved to by hand when this was saved (null in one saved before that could be done).</summary>
    public double? OffsetYawDeg;
    public double? OffsetPitchDeg;
    /// <summary>Single screens moved by hand when this was saved (null in one saved before that could be done).</summary>
    public List<double>? ScreenYawDeg;
    public List<double>? ScreenPitchDeg;

    /// <summary>[id] left out (or null) makes a new one, as the Mac's UUID() default does.</summary>
    public SpacePreset(
        [Optional] Guid? id, string name, ArrangeLayout layout, double widthDeg, double distance, double curve,
        double? offsetYawDeg = null, double? offsetPitchDeg = null, IReadOnlyList<double>? screenYawDeg = null,
        IReadOnlyList<double>? screenPitchDeg = null)
    {
        ScreenYawDeg = screenYawDeg != null ? new List<double>(screenYawDeg) : null;
        ScreenPitchDeg = screenPitchDeg != null ? new List<double>(screenPitchDeg) : null;
        Id = id ?? Guid.NewGuid();
        Name = name;
        Layout = layout;
        WidthDeg = widthDeg;
        Distance = distance;
        Curve = curve;
        OffsetYawDeg = offsetYawDeg;
        OffsetPitchDeg = offsetPitchDeg;
    }

    /// <summary>A copy that shares nothing with this one.</summary>
    public SpacePreset Copy() =>
        new(Id, Name, Layout, WidthDeg, Distance, Curve, OffsetYawDeg, OffsetPitchDeg, ScreenYawDeg, ScreenPitchDeg);

    public bool Equals(SpacePreset? other)
    {
        if (other is null) return false;
        return Id == other.Id && Name == other.Name && Layout == other.Layout && WidthDeg == other.WidthDeg
            && Distance == other.Distance && Curve == other.Curve && OffsetYawDeg == other.OffsetYawDeg
            && OffsetPitchDeg == other.OffsetPitchDeg && SpaceSettingsJson.SameList(ScreenYawDeg, other.ScreenYawDeg)
            && SpaceSettingsJson.SameList(ScreenPitchDeg, other.ScreenPitchDeg);
    }

    public override bool Equals(object? obj) => obj is SpacePreset p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Layout, WidthDeg, Distance, Curve);
    public static bool operator ==(SpacePreset? a, SpacePreset? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SpacePreset? a, SpacePreset? b) => !(a == b);

    // The same keys as the Mac's Codable: the four optional ones are left out when null.
    internal void WriteJson(Utf8JsonWriter w, bool sorted)
    {
        var entries = new List<(string Key, Action<Utf8JsonWriter> Write)>
        {
            // The Mac writes a UUID in upper case.
            ("id", x => x.WriteStringValue(Id.ToString().ToUpperInvariant())),
            ("name", x => x.WriteStringValue(Name)),
            ("layout", x => x.WriteStringValue(Layout.RawValue())),
            ("widthDeg", x => x.WriteNumberValue(WidthDeg)),
            ("distance", x => x.WriteNumberValue(Distance)),
            ("curve", x => x.WriteNumberValue(Curve)),
        };
        if (OffsetYawDeg is double yaw) entries.Add(("offsetYawDeg", x => x.WriteNumberValue(yaw)));
        if (OffsetPitchDeg is double pitch) entries.Add(("offsetPitchDeg", x => x.WriteNumberValue(pitch)));
        if (ScreenYawDeg is List<double> yaws) entries.Add(("screenYawDeg", x => SpaceSettingsJson.WriteDoubles(x, yaws)));
        if (ScreenPitchDeg is List<double> pitches) entries.Add(("screenPitchDeg", x => SpaceSettingsJson.WriteDoubles(x, pitches)));
        SpaceSettingsJson.WriteObject(w, entries, sorted);
    }

    // id, name, layout, widthDeg, distance and curve must be there, as on the Mac; the rest may be missing.
    internal static SpacePreset ReadJson(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("a saved arrangement is not an object");
        string idText = SpaceSettingsJson.String(SpaceSettingsJson.Required(o, "id"));
        if (!Guid.TryParse(idText, out Guid id)) throw new JsonException("id is not a UUID");
        string name = SpaceSettingsJson.String(SpaceSettingsJson.Required(o, "name"));
        ArrangeLayout layout = ArrangeLayoutExt.FromRawValue(SpaceSettingsJson.String(SpaceSettingsJson.Required(o, "layout")))
            ?? throw new JsonException("layout is not a known layout");
        double widthDeg = SpaceSettingsJson.Double(SpaceSettingsJson.Required(o, "widthDeg"));
        double distance = SpaceSettingsJson.Double(SpaceSettingsJson.Required(o, "distance"));
        double curve = SpaceSettingsJson.Double(SpaceSettingsJson.Required(o, "curve"));
        double? offsetYawDeg = SpaceSettingsJson.Has(o, "offsetYawDeg", out JsonElement a) ? SpaceSettingsJson.Double(a) : null;
        double? offsetPitchDeg = SpaceSettingsJson.Has(o, "offsetPitchDeg", out JsonElement b) ? SpaceSettingsJson.Double(b) : null;
        List<double>? screenYawDeg = SpaceSettingsJson.Has(o, "screenYawDeg", out JsonElement c) ? SpaceSettingsJson.Doubles(c) : null;
        List<double>? screenPitchDeg = SpaceSettingsJson.Has(o, "screenPitchDeg", out JsonElement d) ? SpaceSettingsJson.Doubles(d) : null;
        return new SpacePreset(id, name, layout, widthDeg, distance, curve, offsetYawDeg, offsetPitchDeg, screenYawDeg, screenPitchDeg);
    }
}

/// <summary>
/// Every setting Pocket XR for Windows keeps between runs, the same ones as Pocket XR for Mac. One value written as
/// JSON with the Mac's key names: it is the settings file (Save settings to a file, Restore) and what
/// %LOCALAPPDATA%\Pocket XR\settings.json holds (UserDefaults on the Mac). A missing field in an older file takes its
/// default.
///
/// A class here (it holds lists): it compares by value, and <see cref="Copy"/> gives the copy a Swift struct makes on
/// assignment. Copy before changing a value someone else still holds.
/// </summary>
public sealed class SpaceSettings : IEquatable<SpaceSettings>
{
    // The Space
    /// <summary>Virtual displays, 1 to 3. Takes effect when Pocket XR starts.</summary>
    public int Screens = 2;
    public ArrangeLayout Layout = ArrangeLayout.Row;
    /// <summary>Each screen's width as seen, degrees.</summary>
    public double WidthDeg = 40.0;
    /// <summary>Metres from the head.</summary>
    public double Distance = 1.4;
    /// <summary>0 flat, 1 bent round the head like a curved monitor, up to 2.5.</summary>
    public double Curve = 0.0;
    /// <summary>HiDPI virtual displays (twice the pixels). Takes effect when Pocket XR starts.</summary>
    public bool SharperText = false;
    public List<SpacePreset> Presets = new();
    /// <summary>
    /// Where the whole Space has been moved to by hand, degrees from where Recentre put it (left and up positive).
    /// Recentre brings it back to 0.
    /// </summary>
    public double OffsetYawDeg = 0.0;
    public double OffsetPitchDeg = 0.0;
    /// <summary>
    /// Single screens moved by hand, on top of the layout: degrees for each screen in order (left and up positive).
    /// A screen with no entry has not been moved. A change of layout clears them.
    /// </summary>
    public List<double> ScreenYawDeg = new();
    public List<double> ScreenPitchDeg = new();
    /// <summary>
    /// Steady mode: how large the row of screens is drawn on the glasses' flat picture, 1 filling it. Smaller looks
    /// further away; larger runs off the sides (Head scroll slides it).
    /// </summary>
    public double SteadyZoom = 1.0;

    // The picture
    /// <summary>3D side by side: each eye its own picture.</summary>
    public bool Want3D = true;
    /// <summary>Eye distance, millimetres.</summary>
    public double IpdMm = 63.0;
    /// <summary>The focused screen's outline, 0 off to 1.</summary>
    public double Glow = 1.0;
    public GlowColor GlowColor = GlowColor.Blue;
    public EdgeFade EdgeFade = EdgeFade.Soft;
    /// <summary>The screens' brightness in the glasses, 0.3 to 1.</summary>
    public double Brightness = 1.0;

    // Head tracking
    public bool HeadTracking = true;
    public TrackingMode TrackingMode = TrackingMode.BodyAnchor;
    /// <summary>Recentre with the head more than 15 degrees up or down puts the screens where you look.</summary>
    public bool RecentreFollowsTilt = true;
    public bool DrawSmoothing = true;
    public bool StillLock = true;
    /// <summary>The glasses' display delay the pose is predicted across, milliseconds.</summary>
    public double DisplayDelayMs = 8.0;
    /// <summary>Turning the head slides the row of screens (Smooth Follow and Steady mode).</summary>
    public bool HeadScroll = false;
    /// <summary>The pointer jumps to the screen you look at when it is on another screen.</summary>
    public bool PointerFollowsHead = false;

    // Steady mode
    /// <summary>Steady mode uses the glasses' Spatial Anchor (6DoF) where they have it.</summary>
    public bool SteadySixDof = true;
    /// <summary>Head tracking over USB HID for the XREAL Air family and VITURE (untested on a Mac).</summary>
    public bool HidTracking = false;

    // The app
    public bool Shortcuts = true;
    public bool LaunchAtLogin = false;
    public bool TourSeen = false;
    /// <summary>The control panel opens on the PC's screen when Pocket XR starts.</summary>
    public bool PanelAtStart = true;
    /// <summary>
    /// The launcher's pinned apps, as paths to the apps (the .app bundles on a Mac), 10 at most. Empty until the first
    /// run picks some.
    /// </summary>
    public List<string> PinnedApps = new();
    /// <summary>The first run has picked the pinned apps (so an emptied list stays empty).</summary>
    public bool PinsChosen = false;
    public ControllerMode ControllerMode = ControllerMode.Held;
    /// <summary>Hand tracking starts with the glasses.</summary>
    public bool HandsOn = false;
    public HandAim HandAim = HandAim.HandRay;
    /// <summary>
    /// Holding the grab keys (Control and Option on a Mac), moving the mouse moves the Space and scrolling resizes it.
    /// </summary>
    public bool GrabKeys = true;

    public SpaceSettings() { }

    public static readonly (int LowerBound, int UpperBound) ScreensRange = (1, 3);
    public static readonly (double LowerBound, double UpperBound) WidthRange = (15.0, 100.0);
    public static readonly (double LowerBound, double UpperBound) DistanceRange = (0.5, 6.0);
    public static readonly (double LowerBound, double UpperBound) CurveRange = (0.0, 2.5);
    public static readonly (double LowerBound, double UpperBound) IpdRange = (54.0, 74.0);
    public static readonly (double LowerBound, double UpperBound) BrightnessRange = (0.3, 1.0);
    public static readonly (double LowerBound, double UpperBound) DelayRange = (0.0, 40.0);
    public static readonly (double LowerBound, double UpperBound) OffsetYawRange = (-90.0, 90.0);
    public const int MaxPinnedApps = 10;
    public static readonly (double LowerBound, double UpperBound) SteadyZoomRange = (0.4, 1.6);
    public static readonly (double LowerBound, double UpperBound) OffsetPitchRange = (-40.0, 40.0);

    /// <summary>A copy that shares nothing with this one (what assigning a Swift struct gives).</summary>
    public SpaceSettings Copy()
    {
        var s = (SpaceSettings)MemberwiseClone();
        s.Presets = Presets.Select(p => p.Copy()).ToList();
        s.ScreenYawDeg = new List<double>(ScreenYawDeg);
        s.ScreenPitchDeg = new List<double>(ScreenPitchDeg);
        s.PinnedApps = new List<string>(PinnedApps);
        return s;
    }

    /// <summary>Every value brought inside its range (a hand-edited or older file).</summary>
    public SpaceSettings Clamped
    {
        get
        {
            SpaceSettings s = Copy();
            s.Screens = Math.Min(Math.Max(s.Screens, ScreensRange.LowerBound), ScreensRange.UpperBound);
            s.WidthDeg = Held(s.WidthDeg, WidthRange);
            s.Distance = Held(s.Distance, DistanceRange);
            s.Curve = Held(s.Curve, CurveRange);
            s.IpdMm = Held(s.IpdMm, IpdRange);
            s.Glow = Held(s.Glow, (0, 1));
            s.Brightness = Held(s.Brightness, BrightnessRange);
            s.DisplayDelayMs = Held(s.DisplayDelayMs, DelayRange);
            s.OffsetYawDeg = Held(s.OffsetYawDeg, OffsetYawRange);
            s.OffsetPitchDeg = Held(s.OffsetPitchDeg, OffsetPitchRange);
            s.ScreenYawDeg = s.ScreenYawDeg.Take(ScreensRange.UpperBound).Select(v => Held(v, OffsetYawRange)).ToList();
            s.ScreenPitchDeg = s.ScreenPitchDeg.Take(ScreensRange.UpperBound).Select(v => Held(v, OffsetPitchRange)).ToList();
            s.SteadyZoom = Held(s.SteadyZoom, SteadyZoomRange);
            if (s.PinnedApps.Count > MaxPinnedApps) s.PinnedApps = s.PinnedApps.Take(MaxPinnedApps).ToList();
            return s;
        }
    }

    /// <summary>min(max(value, lowerBound), upperBound), as the Mac's clamped has it.</summary>
    private static double Held(double value, (double LowerBound, double UpperBound) range) =>
        HeadMath.Min(HeadMath.Max(value, range.LowerBound), range.UpperBound);

    /// <summary>The Space is as this saved arrangement has it.</summary>
    public bool Matches(SpacePreset p)
    {
        bool sameSize = Math.Abs(p.WidthDeg - WidthDeg) < 0.01 && Math.Abs(p.Distance - Distance) < 0.001;
        bool sameShape = Math.Abs(p.Curve - Curve) < 0.001 && p.Layout == Layout;
        bool samePlace = Math.Abs((p.OffsetYawDeg ?? 0) - OffsetYawDeg) < 0.01
            && Math.Abs((p.OffsetPitchDeg ?? 0) - OffsetPitchDeg) < 0.01;
        bool sameScreens = SameMoves(p.ScreenYawDeg ?? (IReadOnlyList<double>)Array.Empty<double>(), ScreenYawDeg)
            && SameMoves(p.ScreenPitchDeg ?? (IReadOnlyList<double>)Array.Empty<double>(), ScreenPitchDeg);
        return sameSize && sameShape && samePlace && sameScreens;
    }

    /// <summary>Two lists of screen moves are the same when every screen's move is, a missing entry counting as none.</summary>
    internal static bool SameMoves(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            double x = i < a.Count ? a[i] : 0;
            double y = i < b.Count ? b[i] : 0;
            if (Math.Abs(x - y) >= 0.01) return false;
        }
        return true;
    }

    /// <summary>A screen has been moved by hand.</summary>
    public bool ScreensMoved =>
        ScreenYawDeg.Any(v => Math.Abs(v) >= 0.01) || ScreenPitchDeg.Any(v => Math.Abs(v) >= 0.01);

    /// <summary>Moves one screen by hand, added to where it already is.</summary>
    public void MoveScreen(int index, double yawDeg, double pitchDeg)
    {
        if (index < 0 || index >= ScreensRange.UpperBound) return;
        while (ScreenYawDeg.Count <= index) ScreenYawDeg.Add(0);
        while (ScreenPitchDeg.Count <= index) ScreenPitchDeg.Add(0);
        ScreenYawDeg[index] += yawDeg;
        ScreenPitchDeg[index] += pitchDeg;
        Assign(Clamped);
    }

    /// <summary>Puts the Space as a saved arrangement has it.</summary>
    public void Apply(SpacePreset p)
    {
        Layout = p.Layout;
        WidthDeg = p.WidthDeg;
        Distance = p.Distance;
        Curve = p.Curve;
        OffsetYawDeg = p.OffsetYawDeg ?? 0;
        OffsetPitchDeg = p.OffsetPitchDeg ?? 0;
        ScreenYawDeg = p.ScreenYawDeg != null ? new List<double>(p.ScreenYawDeg) : new List<double>();
        ScreenPitchDeg = p.ScreenPitchDeg != null ? new List<double>(p.ScreenPitchDeg) : new List<double>();
        Assign(Clamped);
    }

    /// <summary>Saves the Space as it is now under the next free name ("Arrangement 3"), and says the name.</summary>
    public string SaveArrangement()
    {
        int number = Presets.Count + 1;
        string name = $"Arrangement {number}";
        while (Presets.Any(p => p.Name == name))
        {
            number += 1;
            name = $"Arrangement {number}";
        }
        bool moved = ScreensMoved;
        Presets.Add(new SpacePreset(
            name: name, layout: Layout, widthDeg: WidthDeg, distance: Distance, curve: Curve,
            offsetYawDeg: OffsetYawDeg, offsetPitchDeg: OffsetPitchDeg,
            screenYawDeg: moved ? ScreenYawDeg : null, screenPitchDeg: moved ? ScreenPitchDeg : null));
        return name;
    }

    /// <summary>Swift's `self = other`: every value taken from [other], which must be a copy nobody else holds.</summary>
    private void Assign(SpaceSettings other)
    {
        Screens = other.Screens;
        Layout = other.Layout;
        WidthDeg = other.WidthDeg;
        Distance = other.Distance;
        Curve = other.Curve;
        SharperText = other.SharperText;
        Presets = other.Presets;
        OffsetYawDeg = other.OffsetYawDeg;
        OffsetPitchDeg = other.OffsetPitchDeg;
        ScreenYawDeg = other.ScreenYawDeg;
        ScreenPitchDeg = other.ScreenPitchDeg;
        SteadyZoom = other.SteadyZoom;
        Want3D = other.Want3D;
        IpdMm = other.IpdMm;
        Glow = other.Glow;
        GlowColor = other.GlowColor;
        EdgeFade = other.EdgeFade;
        Brightness = other.Brightness;
        HeadTracking = other.HeadTracking;
        TrackingMode = other.TrackingMode;
        RecentreFollowsTilt = other.RecentreFollowsTilt;
        DrawSmoothing = other.DrawSmoothing;
        StillLock = other.StillLock;
        DisplayDelayMs = other.DisplayDelayMs;
        HeadScroll = other.HeadScroll;
        PointerFollowsHead = other.PointerFollowsHead;
        SteadySixDof = other.SteadySixDof;
        HidTracking = other.HidTracking;
        Shortcuts = other.Shortcuts;
        LaunchAtLogin = other.LaunchAtLogin;
        TourSeen = other.TourSeen;
        PanelAtStart = other.PanelAtStart;
        PinnedApps = other.PinnedApps;
        PinsChosen = other.PinsChosen;
        ControllerMode = other.ControllerMode;
        HandsOn = other.HandsOn;
        HandAim = other.HandAim;
        GrabKeys = other.GrabKeys;
    }

    public bool Equals(SpaceSettings? other)
    {
        if (other is null) return false;
        return Screens == other.Screens && Layout == other.Layout && WidthDeg == other.WidthDeg
            && Distance == other.Distance && Curve == other.Curve && SharperText == other.SharperText
            && Presets.SequenceEqual(other.Presets) && OffsetYawDeg == other.OffsetYawDeg
            && OffsetPitchDeg == other.OffsetPitchDeg && SpaceSettingsJson.SameList(ScreenYawDeg, other.ScreenYawDeg)
            && SpaceSettingsJson.SameList(ScreenPitchDeg, other.ScreenPitchDeg) && SteadyZoom == other.SteadyZoom
            && Want3D == other.Want3D && IpdMm == other.IpdMm && Glow == other.Glow && GlowColor == other.GlowColor
            && EdgeFade == other.EdgeFade && Brightness == other.Brightness && HeadTracking == other.HeadTracking
            && TrackingMode == other.TrackingMode && RecentreFollowsTilt == other.RecentreFollowsTilt
            && DrawSmoothing == other.DrawSmoothing && StillLock == other.StillLock
            && DisplayDelayMs == other.DisplayDelayMs && HeadScroll == other.HeadScroll
            && PointerFollowsHead == other.PointerFollowsHead && SteadySixDof == other.SteadySixDof
            && HidTracking == other.HidTracking && Shortcuts == other.Shortcuts && LaunchAtLogin == other.LaunchAtLogin
            && TourSeen == other.TourSeen && PanelAtStart == other.PanelAtStart
            && PinnedApps.SequenceEqual(other.PinnedApps) && PinsChosen == other.PinsChosen
            && ControllerMode == other.ControllerMode && HandsOn == other.HandsOn && HandAim == other.HandAim
            && GrabKeys == other.GrabKeys;
    }

    public override bool Equals(object? obj) => obj is SpaceSettings s && Equals(s);
    public override int GetHashCode() =>
        HashCode.Combine(Screens, Layout, WidthDeg, Distance, Curve, TrackingMode, Presets.Count, PinnedApps.Count);
    public static bool operator ==(SpaceSettings? a, SpaceSettings? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SpaceSettings? a, SpaceSettings? b) => !(a == b);

    // MARK: - JSON (the Mac's Codable)

    /// <summary>
    /// The settings as JSON (UTF-8), with the Mac's key names. [pretty] is the form a settings file is saved in:
    /// indented, the keys in order.
    /// </summary>
    public byte[] Encode(bool pretty = false)
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = pretty, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var w = new Utf8JsonWriter(stream, options))
        {
            WriteJson(w, pretty);
        }
        return stream.ToArray();
    }

    private void WriteJson(Utf8JsonWriter w, bool sorted)
    {
        var entries = new List<(string Key, Action<Utf8JsonWriter> Write)>
        {
            ("screens", x => x.WriteNumberValue(Screens)),
            ("layout", x => x.WriteStringValue(Layout.RawValue())),
            ("widthDeg", x => x.WriteNumberValue(WidthDeg)),
            ("distance", x => x.WriteNumberValue(Distance)),
            ("curve", x => x.WriteNumberValue(Curve)),
            ("sharperText", x => x.WriteBooleanValue(SharperText)),
            ("presets", x =>
            {
                x.WriteStartArray();
                foreach (SpacePreset p in Presets) p.WriteJson(x, sorted);
                x.WriteEndArray();
            }),
            ("offsetYawDeg", x => x.WriteNumberValue(OffsetYawDeg)),
            ("offsetPitchDeg", x => x.WriteNumberValue(OffsetPitchDeg)),
            ("screenYawDeg", x => SpaceSettingsJson.WriteDoubles(x, ScreenYawDeg)),
            ("screenPitchDeg", x => SpaceSettingsJson.WriteDoubles(x, ScreenPitchDeg)),
            ("steadyZoom", x => x.WriteNumberValue(SteadyZoom)),
            ("want3D", x => x.WriteBooleanValue(Want3D)),
            ("ipdMm", x => x.WriteNumberValue(IpdMm)),
            ("glow", x => x.WriteNumberValue(Glow)),
            ("glowColor", x => x.WriteStringValue(GlowColor.RawValue())),
            ("edgeFade", x => x.WriteStringValue(EdgeFade.RawValue())),
            ("brightness", x => x.WriteNumberValue(Brightness)),
            ("headTracking", x => x.WriteBooleanValue(HeadTracking)),
            ("trackingMode", x => x.WriteStringValue(TrackingMode.RawValue())),
            ("recentreFollowsTilt", x => x.WriteBooleanValue(RecentreFollowsTilt)),
            ("drawSmoothing", x => x.WriteBooleanValue(DrawSmoothing)),
            ("stillLock", x => x.WriteBooleanValue(StillLock)),
            ("displayDelayMs", x => x.WriteNumberValue(DisplayDelayMs)),
            ("headScroll", x => x.WriteBooleanValue(HeadScroll)),
            ("pointerFollowsHead", x => x.WriteBooleanValue(PointerFollowsHead)),
            ("steadySixDof", x => x.WriteBooleanValue(SteadySixDof)),
            ("hidTracking", x => x.WriteBooleanValue(HidTracking)),
            ("shortcuts", x => x.WriteBooleanValue(Shortcuts)),
            ("launchAtLogin", x => x.WriteBooleanValue(LaunchAtLogin)),
            ("tourSeen", x => x.WriteBooleanValue(TourSeen)),
            ("panelAtStart", x => x.WriteBooleanValue(PanelAtStart)),
            ("pinnedApps", x =>
            {
                x.WriteStartArray();
                foreach (string app in PinnedApps) x.WriteStringValue(app);
                x.WriteEndArray();
            }),
            ("pinsChosen", x => x.WriteBooleanValue(PinsChosen)),
            ("controllerMode", x => x.WriteStringValue(ControllerMode.RawValue())),
            ("handsOn", x => x.WriteBooleanValue(HandsOn)),
            ("handAim", x => x.WriteStringValue(HandAim.RawValue())),
            ("grabKeys", x => x.WriteBooleanValue(GrabKeys)),
        };
        SpaceSettingsJson.WriteObject(w, entries, sorted);
    }

    /// <summary>
    /// The settings from JSON (UTF-8). A missing key (or a null) takes the default, so older files and newer apps get
    /// along. Throws a JsonException when [data] is not a settings value: not JSON, not an object, or a key holding
    /// the wrong kind of value (as the Mac's decoder throws).
    /// </summary>
    public static SpaceSettings Decode(byte[] data)
    {
        using JsonDocument doc = JsonDocument.Parse(data);
        JsonElement c = doc.RootElement;
        if (c.ValueKind != JsonValueKind.Object) throw new JsonException("the settings are not an object");
        var s = new SpaceSettings();
        JsonElement v;
        if (SpaceSettingsJson.Has(c, "screens", out v)) s.Screens = SpaceSettingsJson.Int(v);
        if (SpaceSettingsJson.Has(c, "layout", out v))
        {
            s.Layout = ArrangeLayoutExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("layout");
        }
        if (SpaceSettingsJson.Has(c, "widthDeg", out v)) s.WidthDeg = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "distance", out v)) s.Distance = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "curve", out v)) s.Curve = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "sharperText", out v)) s.SharperText = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "presets", out v))
        {
            if (v.ValueKind != JsonValueKind.Array) throw new JsonException("presets is not a list");
            s.Presets = v.EnumerateArray().Select(SpacePreset.ReadJson).ToList();
        }
        if (SpaceSettingsJson.Has(c, "want3D", out v)) s.Want3D = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "ipdMm", out v)) s.IpdMm = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "glow", out v)) s.Glow = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "glowColor", out v))
        {
            s.GlowColor = GlowColorExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("glowColor");
        }
        if (SpaceSettingsJson.Has(c, "edgeFade", out v))
        {
            s.EdgeFade = EdgeFadeExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("edgeFade");
        }
        if (SpaceSettingsJson.Has(c, "brightness", out v)) s.Brightness = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "headTracking", out v)) s.HeadTracking = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "trackingMode", out v))
        {
            s.TrackingMode = TrackingModeExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("trackingMode");
        }
        if (SpaceSettingsJson.Has(c, "recentreFollowsTilt", out v)) s.RecentreFollowsTilt = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "drawSmoothing", out v)) s.DrawSmoothing = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "stillLock", out v)) s.StillLock = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "displayDelayMs", out v)) s.DisplayDelayMs = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "headScroll", out v)) s.HeadScroll = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "pointerFollowsHead", out v)) s.PointerFollowsHead = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "steadySixDof", out v)) s.SteadySixDof = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "hidTracking", out v)) s.HidTracking = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "shortcuts", out v)) s.Shortcuts = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "launchAtLogin", out v)) s.LaunchAtLogin = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "tourSeen", out v)) s.TourSeen = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "offsetYawDeg", out v)) s.OffsetYawDeg = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "offsetPitchDeg", out v)) s.OffsetPitchDeg = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "panelAtStart", out v)) s.PanelAtStart = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "grabKeys", out v)) s.GrabKeys = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "pinnedApps", out v))
        {
            if (v.ValueKind != JsonValueKind.Array) throw new JsonException("pinnedApps is not a list");
            s.PinnedApps = v.EnumerateArray().Select(SpaceSettingsJson.String).ToList();
        }
        if (SpaceSettingsJson.Has(c, "pinsChosen", out v)) s.PinsChosen = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "controllerMode", out v))
        {
            s.ControllerMode = ControllerModeExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("controllerMode");
        }
        if (SpaceSettingsJson.Has(c, "handsOn", out v)) s.HandsOn = SpaceSettingsJson.Bool(v);
        if (SpaceSettingsJson.Has(c, "handAim", out v))
        {
            s.HandAim = HandAimExt.FromRawValue(SpaceSettingsJson.String(v)) ?? throw new JsonException("handAim");
        }
        if (SpaceSettingsJson.Has(c, "steadyZoom", out v)) s.SteadyZoom = SpaceSettingsJson.Double(v);
        if (SpaceSettingsJson.Has(c, "screenYawDeg", out v)) s.ScreenYawDeg = SpaceSettingsJson.Doubles(v);
        if (SpaceSettingsJson.Has(c, "screenPitchDeg", out v)) s.ScreenPitchDeg = SpaceSettingsJson.Doubles(v);
        return s;
    }

    /// <summary>Swift's `try? JSONDecoder().decode(SpaceSettings.self, from:)`: null when [data] is not settings.</summary>
    public static SpaceSettings? TryDecode(byte[] data)
    {
        try
        {
            return Decode(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // MARK: - The settings between runs (UserDefaults on the Mac)

    /// <summary>%LOCALAPPDATA%\Pocket XR\settings.json: the same folder as the log.</summary>
    public static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pocket XR", "settings.json");

    /// <summary>
    /// The saved settings, every value inside its range; the defaults when none are saved or the file is not settings.
    /// [file] is for checks; left out, it is SettingsFile.
    /// </summary>
    public static SpaceSettings Load(string? file = null)
    {
        try
        {
            file ??= SettingsFile;
            if (File.Exists(file) && TryDecode(File.ReadAllBytes(file)) is SpaceSettings loaded) return loaded.Clamped;
        }
        catch (Exception)
        {
        }
        return new SpaceSettings();
    }

    /// <summary>
    /// Keeps these settings for the next run. False if the file could not be written. [file] is for checks; left out,
    /// it is SettingsFile.
    /// </summary>
    public bool Save(string? file = null)
    {
        try
        {
            file ??= SettingsFile;
            string? folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string fresh = file + ".new";
            File.WriteAllBytes(fresh, Encode());
            File.Move(fresh, file, true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Reading and writing the settings' JSON the way the Mac's Codable does.</summary>
internal static class SpaceSettingsJson
{
    /// <summary>The key is there and not null (decodeIfPresent).</summary>
    internal static bool Has(JsonElement o, string key, out JsonElement value) =>
        o.TryGetProperty(key, out value) && value.ValueKind != JsonValueKind.Null;

    internal static JsonElement Required(JsonElement o, string key) =>
        Has(o, key, out JsonElement value) ? value : throw new JsonException(key + " is missing");

    internal static int Int(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Number) throw new JsonException("not a number");
        if (v.TryGetInt32(out int i)) return i;
        // A whole number written as 3.0 is taken, as the Mac takes it.
        double d = v.GetDouble();
        if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
        throw new JsonException("not a whole number");
    }

    internal static double Double(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Number) throw new JsonException("not a number");
        return v.GetDouble();
    }

    internal static bool Bool(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        throw new JsonException("not true or false");
    }

    internal static string String(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) throw new JsonException("not text");
        return v.GetString() ?? "";
    }

    internal static List<double> Doubles(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new JsonException("not a list");
        return v.EnumerateArray().Select(Double).ToList();
    }

    internal static void WriteDoubles(Utf8JsonWriter w, List<double> values)
    {
        w.WriteStartArray();
        foreach (double value in values) w.WriteNumberValue(value);
        w.WriteEndArray();
    }

    internal static void WriteObject(Utf8JsonWriter w, List<(string Key, Action<Utf8JsonWriter> Write)> entries, bool sorted)
    {
        if (sorted) entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        w.WriteStartObject();
        foreach ((string key, Action<Utf8JsonWriter> write) in entries)
        {
            w.WritePropertyName(key);
            write(w);
        }
        w.WriteEndObject();
    }

    /// <summary>Two lists hold the same numbers (both null counts as the same).</summary>
    internal static bool SameList(List<double>? a, List<double>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
