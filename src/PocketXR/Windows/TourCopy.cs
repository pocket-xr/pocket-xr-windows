using System.Collections.Generic;
using System.Globalization;
using PocketXR.Core;

namespace PocketXR.Windows;

/// <summary>What is still to do at the end of the tour on a PC, in the order to do it.</summary>
public enum TourOpen
{
    PlugInGlasses,
    /// <summary>The glasses are on USB, but Windows has no display for them.</summary>
    NoPicture,
    /// <summary>No virtual display driver Pocket XR can use, so no floating screens.</summary>
    NoFloatingScreens,
    /// <summary>Everything is there, but no screen is showing a picture on the glasses yet.</summary>
    ScreensNotShowing,
}

/// <summary>
/// The tour's words, in one place (Pocket XR for Mac's TourCopy.swift, for a PC). Plain, short, exact; one idea per
/// step. What is untested is said as untested: on a PC only the XREAL Aura has been used, and of it only its head
/// with Pocket XR for Windows, on a Steam Deck running Windows 11 (17 Nov 2026).
/// </summary>
public static class TourCopy
{
    public const string WindowTitle = "Tour of Pocket XR";
    public const string Skip = "Skip";
    public const string Back = "Back";
    public const string Next = "Next";
    public const string Finish = "Done";
    public const string StateDone = "Done";
    public const string StateToDo = "To do";
    public const string StateOptional = "Optional";

    public static string StepOf(TourStep step) => $"Step {step.Number()} of {Tour.Count}";

    /// <summary>A shortcut's keys as the menu shows them ("Ctrl+Alt+R"), from the app's one list.</summary>
    public static string Keys(string id) => Shortcut.Find(id).Keys;

    /// <summary>A step's title. A long one is broken where it reads best, as the words' column is narrow.</summary>
    public static string Title(TourStep step, bool ready) => step switch
    {
        TourStep.Welcome => "Your PC, all around you",
        TourStep.Setup => "Get set up",
        TourStep.Screens => "Your screens\nare real displays",
        TourStep.Look => "Look around",
        TourStep.Arrange => "Make the Space yours",
        TourStep.Depth => "3D and Steady mode",
        TourStep.Menu => "Always in the\nnotification area",
        _ => ready ? "You're set" : "Almost there",
    };

    /// <summary>What each picture shows, for a screen reader.</summary>
    public static string Picture(TourStep step, bool connected, bool ready) => step switch
    {
        TourStep.Welcome => "Picture: glass screens floating in an arc above a small PC, the middle one lit.",
        TourStep.Setup => connected
            ? "Picture: a PC joined to glasses by a cable, with light running along it."
            : "Picture: a PC and glasses, the cable not plugged in yet.",
        TourStep.Screens => "Picture: the pointer leaves the right edge of the PC's screen and is on the first floating screen. Then it carries a window across.",
        TourStep.Look => "Picture: the view turns away and the screens slide out of sight. A press on Ctrl, Alt and R brings them back in front.",
        TourStep.Arrange => "Picture: your screens seen from behind your seat. They grow, come nearer and curve as the controls change.",
        TourStep.Depth => "Picture: two pictures, one for each eye, come together into screens at depth. Then the screens line up in a row that holds still.",
        TourStep.Menu => "Picture: the menu opens from the glasses icon in the notification area. Then the cable is unplugged and the windows glide back onto the PC's screen.",
        _ => ready ? "Picture: your screens around your PC, and a tick." : "Picture: your screens around your PC.",
    };

    // 1. Welcome
    public const string WelcomeBody = "Pocket XR puts your PC's screens in your glasses, as large screens floating around you. They hold their place as you look around, and your keyboard and mouse work as always.";
    public const string WelcomeNote = "This tour takes about two minutes. Skip it any time: it stays in the menu.";
    public const string WelcomeEarly = "Pocket XR for Windows is new. So far it has been tried with the XREAL Aura, on a Steam Deck running Windows.";

    // 2. Get set up
    public const string SetupBody = "Four things are needed; one is optional.";
    public const string GlassesTitle = "Glasses";
    public const string GlassesUnplugged = "Not plugged in yet. Plug them into a USB-C port that carries video. The tour works without them.";
    public static string GlassesConnected(string name) => $"Connected: {name}.";

    /// <summary>What is known for these glasses on a PC. Nothing is called tested: only the Aura's head tracking and
    /// USB link have been used with Pocket XR for Windows.</summary>
    public static string Support(TourSupport support, string name) => support switch
    {
        TourSupport.Tested => "Tried on a Steam Deck running Windows: floating screens, 3D and head tracking.",
        TourSupport.SameTrackingUntested => $"{name}: uses the same head tracking as the Aura. Not tried on a PC yet.",
        TourSupport.ExperimentalTracking => $"{name}: not tried on a PC yet. Head tracking is an experimental switch in Settings.",
        _ => $"{name}: not tried on a PC yet. No head tracking.",
    };

    public const string PictureTitle = "Picture in the glasses";
    public static string PictureSeen(string display) => $"Windows sees their display: {display}.";
    public const string PictureNotSeen = "Windows does not see their display yet.";
    public const string PictureNeeds = "The glasses need a USB-C port that carries video, or an adapter.";
    public const string PictureWaits = "Comes when the glasses are plugged in.";

    public const string FloatingTitle = "Floating screens";
    public static string FloatingReady(int count) => count == 1 ? "Ready: 1 floating screen." : $"Ready: {count} floating screens.";
    public const string FloatingReadyNoGlasses = "Ready. They appear when your glasses are plugged in.";
    public const string FloatingReadyNone = "Ready.";
    public const string FloatingNotReady = "Not ready yet.";
    public const string FloatingNeeds = "They need a virtual display driver, which an administrator installs once.";
    public const string FloatingHelp = "How to add floating screens";

    public const string ShowingTitle = "Screens showing";
    public static string ShowingNow(int count) => count == 1 ? "1 screen is showing in your glasses." : $"{count} screens are showing in your glasses.";
    public const string ShowingNotYet = "Not showing yet.";
    public const string ShowingWhere = "The menu says where things stand.";

    public const string SignInTitle = "Open at sign-in";
    public const string SignInLine = "Pocket XR starts with Windows.";

    // 3. Your screens are real displays
    public const string ScreensBody = "Windows sees one to three extra displays, to the right of your own screen. Full screen, Snap and Task view work on them as on any display.";
    public const string ScreensPointer = "Move the pointer off the right edge of your PC's screen. It is now on the first floating screen.";
    public const string ScreensDrag = "Drag a window onto a floating screen.";
    public const string ScreensCount = "Change how many screens you have in Settings. It takes effect when Pocket XR opens again.";

    // 4. Look around
    public const string LookBody = "Screens stay where they are as you turn your head.";
    public static string LookRecentre() =>
        $"Lost them, or sitting somewhere new? Recentre brings them back in front of you: press {Keys("recentre")}, or pick Recentre in the menu.";
    public static string LookTry() => $"Try it: press {Keys("recentre")}";
    public const string Recentre = "Recentre";

    // 5. Make the Space yours
    public const string ArrangeBody = "Drag a control: the screens in your glasses change as you do.";
    public const string ArrangeSize = "Size";
    public const string ArrangeDistance = "Distance";
    public const string ArrangeCurve = "Curve";
    public const string LayoutTitle = "Layout";
    public const string ArrangeNote = "The keys beside each control do the same from anywhere. Or hold Ctrl and Alt and move the pointer: the screens move with it.";

    public static string SizeValue(double degrees) => SettingsFormat.Degrees(degrees);
    public static string DistanceValue(double metres) => SettingsFormat.Metres(metres);
    public static string CurveValue(double curve) => SettingsFormat.Curve(curve);

    // 6. 3D and Steady mode
    public const string Depth3D = "3D";
    public const string Depth3DLine = "Each eye gets its own picture, so screens sit at real depth.";
    public static string Depth3DNote() =>
        $"Pocket XR switches the glasses to 3D by itself where they allow it. {Keys("depth")} turns it on and off. Not seen working from a PC yet.";
    public const string Steady = "Steady mode";
    public static string SteadyLine() =>
        $"For reading and working: the glasses hold the picture completely still themselves. {Keys("steady")} turns it on and off.";
    public const string SteadyNote = "For the XREAL Aura and the One series. Not tried on a PC yet.";

    // 7. Always in the notification area
    public const string MenuBody = "The control panel on your PC's screen has everything in one place. Close it and the glasses icon in the notification area (the icons by the clock) opens it again. Its menu holds Recentre, Settings, this tour and Send logs.";
    public const string MenuOpen = "Right-click the glasses icon to open the menu. If you cannot see the icon, click the small arrow beside the clock's icons.";
    public const string MenuQuit = "\"Quit and put the glasses back\" returns your glasses to normal.";
    public const string MenuUnplug = "Unplug the glasses and the floating screens go away by themselves. Your windows come back to your PC's screen.";

    // 8. You're set
    public const string DoneReady = "Your glasses are connected and your screens are showing. Put your glasses on and look up.";
    public const string DoneOpen = "That's the tour. Here is what is still to do:";
    public const string DoneToSetup = "Back to setup";
    public const string DoneAgain = "This tour is always in the menu, and in Settings under Help.";
    public const string DoneShortcuts = "Settings lists every shortcut.";

    public static string Remaining(TourOpen item) => item switch
    {
        TourOpen.PlugInGlasses => "Plug in your glasses.",
        TourOpen.NoPicture => "Get a picture to your glasses: they need a USB-C port that carries video, or an adapter.",
        TourOpen.NoFloatingScreens => "Add floating screens: they need a virtual display driver, which an administrator installs once.",
        _ => "Your glasses are connected, but the screens are not showing on them yet. The menu says where things stand.",
    };
}

/// <summary>What the tour needs to know of the app's state on a PC: the Mac's logic (Tour.Remaining, with its Screen
/// Recording permission always given, as Windows has none) and the two things only a PC needs around it.</summary>
public static class TourNeeds
{
    public static bool Connected(AppState state) => state.GlassesName != null;
    public static bool PictureSeen(AppState state) => Connected(state) && state.DisplayText != null;
    public static bool FloatingReady(AppState state) => state.DriverReady || state.VirtualScreens > 0;
    public static bool Showing(AppState state) => state.Capturing > 0;

    /// <summary>What is still open, in the order to do it.</summary>
    public static List<TourOpen> Open(AppState state)
    {
        TourRemaining[] mac = Tour.Remaining(glassesConnected: Connected(state), screenRecordingAllowed: true, capturing: state.Capturing);
        var list = new List<TourOpen>();
        if (System.Array.IndexOf(mac, TourRemaining.PlugInGlasses) >= 0) list.Add(TourOpen.PlugInGlasses);
        if (Connected(state) && !PictureSeen(state)) list.Add(TourOpen.NoPicture);
        if (!FloatingReady(state)) list.Add(TourOpen.NoFloatingScreens);
        // With no picture or no floating screens, that is why nothing shows: only the cause is listed.
        if (System.Array.IndexOf(mac, TourRemaining.ScreensNotShowing) >= 0 && list.Count == 0) list.Add(TourOpen.ScreensNotShowing);
        return list;
    }

    public static bool Ready(AppState state) => Open(state).Count == 0;
}
