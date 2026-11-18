using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// Every window drawn to PNG files, with made-up but possible states, so the design can be looked at with no glasses
/// (PocketXR --render-ui FOLDER). The pictures show the look and the words; they prove nothing about the glasses.
/// </summary>
public static class Pictures
{
    /// <summary>A made-up state: [glasses] plugged in or not, [screens] floating screens laid out by the settings.</summary>
    public static AppState State(SettingsStore store, bool glasses, int screens, bool steady = false, int? pointerOn = null,
        bool driver = true, double yaw = 6, double pitch = -2)
    {
        var state = new AppState
        {
            GlassesName = glasses ? "XREAL Aura" : null,
            DisplayText = glasses ? "3840 x 1080, 60 Hz" : null,
            Stereo = glasses && !steady && store.S.Want3D,
            Fps = glasses ? 60 : 0,
            TrackingOn = glasses,
            ImuHz = glasses ? 1000 : 0,
            BiasReady = glasses,
            YawDeg = yaw, PitchDeg = pitch,
            LinkOpen = glasses,
            Steady = steady,
            SteadyAvailable = glasses,
            DriverReady = driver,
            DriverWhy = driver ? "" : "no virtual display driver Pocket XR knows is installed",
            VirtualScreens = glasses ? screens : 0,
            Capturing = glasses ? screens : 0,
            PointerScreen = pointerOn,
            StatusLine = glasses ? $"XREAL Aura · {screens} screens · head tracking on" : "Plug in your glasses.",
        };
        if (glasses && screens > 0)
        {
            state.Panels = Placements(store.S, screens, steady);
            var tiles = new List<DisplayTile> { new(-1, "This PC", new Rect(0, 0, 1920, 1080)) };
            var sizes = Enumerable.Repeat((Width: 1920, Height: 1080), screens).ToList();
            var origins = DisplayGrid.Origins(state.Panels.ToList(), sizes);
            for (int i = 0; i < screens; i++)
                tiles.Add(new DisplayTile(i, $"Screen {i + 1}", new Rect(1920 + origins[i].X, origins[i].Y, 1920, 1080)));
            state.DisplayTiles = tiles;
        }
        return state;
    }

    /// <summary>Where the screens sit for these settings, as the controller works it out.</summary>
    public static List<PanelPlacement> Placements(SpaceSettings s, int screens, bool steady)
    {
        var aspects = Enumerable.Repeat(16.0 / 9, screens).ToList();
        if (steady)
        {
            const double fov = 12;
            double gap = s.SteadyGapDeg;
            double hfov = 2 * Math.Atan(Math.Tan(fov * Math.PI / 360) * (32.0 / 9)) * 180 / Math.PI;
            double each = Math.Max(2, (hfov * 0.96 - gap * (screens - 1)) / screens * s.SteadyZoom);
            return SpaceLayout.Row(aspects, each, SpaceLayout.DefaultDistance, gap).ToList();
        }
        var panels = SpaceLayout.Arrange(s.Layout, aspects, s.WidthDeg, s.Distance, s.Curve).ToList();
        for (int i = 0; i < panels.Count; i++)
        {
            PanelPlacement p = panels[i];
            p.YawDeg += s.OffsetYawDeg + (i < s.ScreenYawDeg.Count ? s.ScreenYawDeg[i] : 0);
            p.PitchDeg += s.OffsetPitchDeg + (i < s.ScreenPitchDeg.Count ? s.ScreenPitchDeg[i] : 0);
            panels[i] = p;
        }
        return panels;
    }

    public static void ControlPanelPictures(string folder)
    {
        void One(string name, SettingsStore store, AppState state)
        {
            var binder = new Binder();
            FrameworkElement column = ControlPanel.Column(store, state, new NoActions(), binder);
            UiRender.WindowPng(Path.Combine(folder, name), "Pocket XR", column, ControlPanel.Width, null, ControlPanel.StripRight(state, binder));
        }

        var plain = new SettingsStore(persist: false);
        One("control-panel-1-no-glasses.png", plain, State(plain, glasses: false, screens: 0));
        One("control-panel-2-no-driver.png", plain, State(plain, glasses: false, screens: 0, driver: false));
        One("control-panel-3-two-screens.png", plain, State(plain, glasses: true, screens: 2, pointerOn: 0));

        var three = new SettingsStore(persist: false);
        three.Update(s =>
        {
            s.Screens = 3;
            s.Layout = ArrangeLayout.MainSide;
            s.Curve = 0.5;
            s.SaveArrangement();
        });
        One("control-panel-4-three-screens.png", three, State(three, glasses: true, screens: 3, pointerOn: 1, yaw: -14, pitch: 3));

        var steady = new SettingsStore(persist: false);
        One("control-panel-5-steady.png", steady, State(steady, glasses: true, screens: 2, steady: true));

        var saved = new SettingsStore(persist: false);
        saved.Update(s => s.SaveArrangement());
        saved.Update(s =>
        {
            s.WidthDeg = 55;
            s.SaveArrangement();
        });
        UiRender.Png(Path.Combine(folder, "control-panel-6-arrangements.png"),
            ControlPanel.ArrangementList(saved, new NoActions(), () => { }));
    }
}
