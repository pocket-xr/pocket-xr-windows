using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The panes. Each control is bound straight to the settings (store.S), so a change is live in the glasses and kept
// at once; what is true now comes from the app's state; anything the app has to do goes through its actions.

public static class SettingsPanes
{
    /// <summary>The cards of [pane], top to bottom.</summary>
    public static IEnumerable<FrameworkElement> Build(SettingsPane pane, SettingsStore store, AppState state,
        IAppActions actions, Binder b, SettingsShow show) => pane switch
    {
        SettingsPane.Space => Space(store, state, actions, b, show),
        SettingsPane.Picture => Picture(store, state, b),
        SettingsPane.Tracking => Tracking(store, state, b),
        SettingsPane.Glasses => GlassesCards(store, state, actions, b),
        SettingsPane.Hands => SettingsHands.Cards(store, state, actions, b),
        SettingsPane.Controls => SettingsController.Cards(store, state, actions, b),
        SettingsPane.Shortcuts => Shortcuts(store, state, actions, b),
        _ => Help(store, state, actions, b, show),
    };

    // MARK: Space

    private static IEnumerable<FrameworkElement> Space(SettingsStore store, AppState state, IAppActions actions, Binder b, SettingsShow show)
    {
        yield return SettingsSpacePreview.Card(store, state, b);
        yield return ScreensCard(store, state, actions, b);
        yield return ShapeCard(store, actions, b);
        yield return PresetsCard(store, b, show);
    }

    private static FrameworkElement ScreensCard(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        var screens = new Segmented(b, "Screens", new[] { "1", "2", "3" }, () => store.S.Screens - 1,
            i => store.Update(s => s.Screens = i + 1));
        // Unlike the Mac, Windows lets displays be added and removed while Pocket XR runs: no restart.
        var screensRow = new SettingsRow("Screens", "How many floating screens Pocket XR adds to your PC.", screens);

        // Windows makes a floating screen only with a virtual display driver: said here, where screens are asked for.
        TextBlock why = Kit.Small("");
        var help = new PxButton("How to add floating screens", Glyph.Help, ButtonKind.Glass, actions.OpenDriverHelp)
        {
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
        };
        var driver = new StackPanel { Margin = new Thickness(Theme.Inset, 10, Theme.Inset, 12) };
        driver.Children.Add(why);
        driver.Children.Add(help);
        UIElement driverLine = Kit.Divider();

        var restart = new SettingsRow("Open Pocket XR again to use this change.", null,
            new PxButton("Restart Pocket XR", Glyph.Reset, ButtonKind.Primary, actions.RestartApp));
        UIElement restartLine = Kit.Divider();

        // The layouts on offer depend on how many screens there are: the picker is made again when that changes.
        var layout = new Border();
        int builtFor = -1;
        var inner = new Binder();
        b.Add(() =>
        {
            SettingsParts.Show(!state.DriverReady, driverLine, driver);
            why.Text = SettingsParts.DriverWords(state);
            SettingsParts.Show(state.NeedsRestart, restartLine, restart);
            int count = store.S.Screens;
            if (count == builtFor)
            {
                inner.Sync();
                return;
            }
            builtFor = count;
            inner = new Binder();
            ArrangeLayout[] choices = SpaceLayout.Choices(count);
            layout.Child = Kit.StackedRow("Layout", new Segmented(inner, "Layout",
                choices.Select(c => SpaceLayout.Title(c, count)).ToList(),
                () => Array.IndexOf(choices, store.S.Layout), i => store.Update(s => s.Layout = choices[i])));
        });

        return Kit.Card("Screens",
            screensRow,
            driverLine, driver,
            restartLine, restart,
            // "Sharper text" (twice the pixels on each screen) is the Mac's; the Windows app does not do it yet, so
            // the switch is left out rather than shown doing nothing.
            Kit.Divider(), layout);
    }

    private static FrameworkElement ShapeCard(SettingsStore store, IAppActions actions, Binder b) => Kit.Card("Size and shape",
        SettingsParts.SliderRow(b, "Size", () => store.S.WidthDeg, v => store.Update(s => s.WidthDeg = v),
            SpaceSettings.WidthRange, 1, SettingsFormat.Degrees),
        Kit.Divider(),
        SettingsParts.SliderRow(b, "Distance", () => store.S.Distance, v => store.Update(s => s.Distance = v),
            SpaceSettings.DistanceRange, 0.1, SettingsFormat.Metres),
        Kit.Divider(),
        SettingsParts.SliderRow(b, "Curve", () => store.S.Curve, v => store.Update(s => s.Curve = v),
            SpaceSettings.CurveRange, 0.05, SettingsFormat.Curve),
        Kit.Divider(),
        new SettingsRow("Recentre", "Brings the screens in front of where you are looking now.",
            new PxButton("Recentre", Glyph.Recentre, ButtonKind.Primary, actions.Recentre)));

    /// <summary>The saved arrangements: use one, rename it, delete it, or save the Space as it is now.</summary>
    private static FrameworkElement PresetsCard(SettingsStore store, Binder b, SettingsShow show)
    {
        Guid? editing = show.Renaming;

        string Describe(ArrangeLayout layout, double widthDeg, double distance, double curve)
        {
            string name = SpaceLayout.Title(layout, store.S.Screens);
            string bend = curve <= 0.0001 ? "flat" : $"curve {SettingsFormat.Curve(curve)}";
            return $"{name} · {SettingsFormat.Degrees(widthDeg)} · {SettingsFormat.Metres(distance)} · {bend}";
        }

        void Rename(Guid id, string name)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0) return;
            store.Update(s =>
            {
                SpacePreset? p = s.Presets.FirstOrDefault(x => x.Id == id);
                if (p != null) p.Name = trimmed;
            });
        }

        UIElement EditRow(SpacePreset preset)
        {
            var box = new TextBox { Text = preset.Name, Height = Theme.ButtonHeight, MaxLength = 40 };
            AutomationProperties.SetName(box, "Arrangement name");
            void Finish()
            {
                editing = null;
                Rename(preset.Id, box.Text);
                b.Sync();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Finish();
                else if (e.Key == Key.Escape)
                {
                    editing = null;
                    b.Sync();
                }
                else return;
                e.Handled = true;
            };
            if (!Theme.Static)
            {
                box.Loaded += (_, _) =>
                {
                    box.Focus();
                    box.SelectAll();
                };
            }
            var done = new PxButton("Done", null, ButtonKind.Primary, Finish) { Margin = new Thickness(8, 0, 0, 0) };
            var row = new Grid { Margin = new Thickness(Theme.Inset, 8, Theme.Inset, 8), MinHeight = Theme.RowHeight - 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(box);
            Grid.SetColumn(done, 1);
            row.Children.Add(done);
            return row;
        }

        IEnumerable<UIElement?> Rows(Binder own)
        {
            if (store.S.Presets.Count == 0)
            {
                yield return Kit.Note("None saved yet. Save the Space as it is now and come back to it with one click, here or from the control panel.");
                yield return Kit.Divider();
            }
            foreach (SpacePreset p in store.S.Presets)
            {
                SpacePreset preset = p;
                if (editing == preset.Id)
                {
                    yield return EditRow(preset);
                }
                else
                {
                    Border inUse = Kit.Chip("In use", lit: true);
                    var use = new PxButton("Use", null, ButtonKind.Glass, () => store.Update(s => s.Apply(preset)));
                    var rename = new IconButton(SettingsGlyph.Edit, $"Rename {preset.Name}", () =>
                    {
                        editing = preset.Id;
                        b.Sync();
                    });
                    var delete = new IconButton(Glyph.Delete, $"Delete {preset.Name}",
                        () => store.Update(s => s.Presets.RemoveAll(x => x.Id == preset.Id)));
                    StackPanel buttons = Kit.Across(4, use, rename, delete);
                    inUse.VerticalAlignment = VerticalAlignment.Center;
                    inUse.Margin = new Thickness(0, 0, 8, 0);
                    var trailing = new StackPanel { Orientation = Orientation.Horizontal };
                    trailing.Children.Add(inUse);
                    trailing.Children.Add(buttons);
                    yield return new SettingsRow(preset.Name, Describe(preset.Layout, preset.WidthDeg, preset.Distance, preset.Curve), trailing);
                    own.Add(() =>
                    {
                        bool current = store.S.Matches(preset);
                        inUse.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
                        use.IsEnabled = !current;
                    });
                }
                yield return Kit.Divider();
            }
            var save = new SettingsRow("Save this arrangement", " ",
                new PxButton("Save", Glyph.Plus, ButtonKind.Glass, () => store.Update(s => s.SaveArrangement())));
            own.Add(() => save.SetNote(Describe(store.S.Layout, store.S.WidthDeg, store.S.Distance, store.S.Curve)));
            yield return save;
        }

        return SettingsParts.LiveCard(b, "Saved arrangements",
            () => string.Join("|", store.S.Presets.Select(p => p.Id + p.Name)) + "#" + editing + "#" + store.S.Screens,
            Rows);
    }

    // MARK: Picture

    private static IEnumerable<FrameworkElement> Picture(SettingsStore store, AppState state, Binder b)
    {
        Border now = Kit.Chip("3D");
        now.VerticalAlignment = VerticalAlignment.Center;
        now.Margin = new Thickness(0, 0, 10, 0);
        var depthSwitch = new Toggle(b, "3D side by side", () => store.S.Want3D, v => store.Update(s => s.Want3D = v));
        var trailing = new StackPanel { Orientation = Orientation.Horizontal };
        trailing.Children.Add(now);
        trailing.Children.Add(depthSwitch);
        var depth = new SettingsRow("3D side by side", " ", trailing);
        b.Add(() =>
        {
            bool glasses = state.GlassesName != null;
            depth.SetNote(!glasses ? "Each eye gets its own picture, so the screens sit at a depth."
                : state.Stereo ? "The glasses are in 3D now." : "The glasses are in 2D now.");
            now.Visibility = glasses ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)now.Child).Text = state.Stereo ? "3D" : "2D";
            Kit.SetChip(now, state.Stereo);
        });
        yield return Kit.Card("3D",
            depth,
            Kit.Divider(),
            SettingsParts.SliderRow(b, "Eye distance", () => store.S.IpdMm, v => store.Update(s => s.IpdMm = v),
                SpaceSettings.IpdRange, 0.5, v => v.ToString("0.0", CultureInfo.InvariantCulture) + " mm"));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(swatches, "Glow colour");
        foreach (GlowColor glow in GlowColorExt.AllCases)
        {
            GlowColor colour = glow;
            swatches.Children.Add(new Swatch(b, Theme.C(colour.Color()), colour.Title(), () => store.S.GlowColor == colour,
                () => store.Update(s => s.GlowColor = colour)) { Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 2, 0, 0, 0) });
        }
        EdgeFade[] fades = EdgeFadeExt.AllCases;
        yield return Kit.Card("Light",
            SettingsParts.SliderRow(b, "Brightness", () => store.S.Brightness, v => store.Update(s => s.Brightness = v),
                SpaceSettings.BrightnessRange, 0.01, SettingsFormat.Percent),
            Kit.Divider(),
            SettingsParts.SliderRow(b, "Window glow", () => store.S.Glow, v => store.Update(s => s.Glow = v),
                (0.0, 1.0), 0.05, SettingsFormat.Glow),
            Kit.Divider(),
            new SettingsRow("Glow colour", "The outline of the screen you are looking at.", swatches),
            Kit.Divider(),
            new SettingsRow("Edge fade", "A fade to black at the edges of what you see, so screens do not end at a hard line.",
                new Segmented(b, "Edge fade", fades.Select(f => f.Title()).ToList(), () => Array.IndexOf(fades, store.S.EdgeFade),
                    i => store.Update(s => s.EdgeFade = fades[i]))));
    }

    // MARK: Head tracking

    private static IEnumerable<FrameworkElement> Tracking(SettingsStore store, AppState state, Binder b)
    {
        TrackingMode[] modes = TrackingModeExt.AllCases;
        var mode = new SettingsRow("Tracking mode", " ",
            new Segmented(b, "Tracking mode", modes.Select(m => m.Title()).ToList(), () => Array.IndexOf(modes, store.S.TrackingMode),
                i => store.Update(s => s.TrackingMode = modes[i])));
        b.Add(() => mode.SetNote(store.S.TrackingMode.Detail()));
        yield return Kit.Card(null,
            SettingsParts.ToggleRow(b, "Head tracking", "The screens hold their place as you turn your head.",
                () => store.S.HeadTracking, v => store.Update(s => s.HeadTracking = v)),
            Kit.Divider(),
            mode,
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Recentre follows head tilt",
                "Recentre with your head more than 15 degrees up or down puts the screens where you look.",
                () => store.S.RecentreFollowsTilt, v => store.Update(s => s.RecentreFollowsTilt = v)),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Head scroll",
                "Turn your head a little past the screen in front and the row slides that way. Works in Smooth Follow and in Steady mode, not in Body Anchor, where the screens already stay put.",
                () => store.S.HeadScroll, v => store.Update(s => s.HeadScroll = v)),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Pointer follows head",
                "The pointer jumps to the screen you look at when it is on another screen.",
                () => store.S.PointerFollowsHead, v => store.Update(s => s.PointerFollowsHead = v)));

        yield return TrackingNow(state, b);

        yield return Kit.Card("Advanced",
            SettingsParts.ToggleRow(b, "Draw smoothing", "Evens out the head's movement from one frame to the next.",
                () => store.S.DrawSmoothing, v => store.Update(s => s.DrawSmoothing = v)),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Still lock", "Holds the screens exactly still while your head is still.",
                () => store.S.StillLock, v => store.Update(s => s.StillLock = v)),
            Kit.Divider(),
            SettingsParts.SliderRow(b, "Glasses display delay", () => store.S.DisplayDelayMs, v => store.Update(s => s.DisplayDelayMs = v),
                SpaceSettings.DelayRange, 1, SettingsFormat.Milliseconds),
            Kit.Note("How far ahead Pocket XR works out where your head will be, to make up for the time the glasses take to show a frame."));
    }

    /// <summary>What the head tracking is doing now.</summary>
    private static FrameworkElement TrackingNow(AppState state, Binder b)
    {
        var gauge = new HeadGauge(state) { Width = 112, Height = 112, VerticalAlignment = VerticalAlignment.Center };
        StackPanel first = Kit.Across(20, Kit.Reading("Turn", out TextBlock turn), Kit.Reading("Tilt", out TextBlock tilt),
            Kit.Reading("Drift", out TextBlock drift));
        StackPanel second = Kit.Across(20, Kit.Reading("Reports a second", out TextBlock reports),
            Kit.Reading("Frames a second", out TextBlock frames));
        StackPanel readings = Kit.Down(14, first, second);
        readings.VerticalAlignment = VerticalAlignment.Center;
        readings.Margin = new Thickness(20, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Theme.Inset) };
        row.Children.Add(gauge);
        row.Children.Add(readings);
        UIElement line = Kit.Divider();
        TextBlock note = Kit.Note("");
        b.Add(() =>
        {
            gauge.InvalidateVisual();
            turn.Text = SettingsFormat.SignedDegrees(state.YawDeg);
            tilt.Text = SettingsFormat.SignedDegrees(state.PitchDeg);
            drift.Text = !state.TrackingOn ? "Waiting" : state.BiasReady ? "Learnt" : "Learning";
            reports.Text = state.ImuHz.ToString(CultureInfo.InvariantCulture);
            frames.Text = ((int)Math.Round(state.Fps)).ToString(CultureInfo.InvariantCulture);
            SettingsParts.Show(!state.TrackingOn || !state.BiasReady, line, note);
            note.Text = !state.TrackingOn ? "No head tracking reports are arriving."
                : "Keep your head still for two seconds so Pocket XR can learn the glasses' drift.";
        });
        return Kit.Card("Now", row, line, note);
    }

    // MARK: Glasses

    private static IEnumerable<FrameworkElement> GlassesCards(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        // Connected.
        Border link = Kit.Chip(""), head = Kit.Chip("");
        StackPanel chips = Kit.Across(6, link, head);
        var connected = new SettingsRow("", " ", chips);
        b.Add(() =>
        {
            bool glasses = state.GlassesName != null;
            connected.Title.Text = state.GlassesName ?? "No glasses";
            connected.SetNote(!glasses ? "Plug in your glasses."
                : state.Busy ? "Changing the glasses' settings…"
                : state.DisplayText ?? "Waiting for the glasses' display.");
            chips.Visibility = glasses ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)link.Child).Text = state.LinkOpen ? "Settings link open" : "No settings link";
            Kit.SetChip(link, state.LinkOpen);
            ((TextBlock)head.Child).Text = state.TrackingOn ? "Head tracking on" : "No head tracking";
            Kit.SetChip(head, state.TrackingOn);
        });
        yield return Kit.Card("Connected", connected);

        // Steady mode.
        TextBlock cannot = Kit.Note("");
        cannot.Margin = new Thickness(Theme.Inset, 0, Theme.Inset, 12);
        b.Add(() =>
        {
            SettingsParts.Show(!state.SteadyAvailable, cannot);
            bool glasses = state.GlassesName != null;
            cannot.Text = glasses ? "These glasses cannot do Steady mode, or their settings link is not open yet."
                : "Steady mode needs glasses that can hold the picture themselves. None are plugged in.";
            cannot.Foreground = glasses ? Theme.Warn : Theme.Ink2;
        });
        yield return Kit.Card("Steady mode",
            SettingsParts.ToggleRow(b, "Steady mode",
                "For reading and working: the glasses hold the picture completely still themselves. A tester uses it on Windows 11 with the XREAL 1S.",
                () => state.Steady, actions.SetSteady, () => state.SteadyAvailable),
            cannot,
            Kit.Divider(),
            SettingsParts.SliderRow(b, "Screen gap", () => store.S.SteadyGapDeg, v => store.Update(s => s.SteadyGapDeg = v),
                SpaceSettings.SteadyGapRange, 0.1, SettingsFormat.TenthDegrees),
            Kit.Note("The space between screens in Steady mode. Steady mode itself is remembered: if it was on, it comes back on when your glasses connect."),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Steady mode in 6DoF",
                "Uses the glasses' Spatial Anchor where they have it, so the picture also stays put when you lean or move.",
                () => store.S.SteadySixDof, v => store.Update(s => s.SteadySixDof = v)));

        // Where the Mac asks for Screen Recording, Windows needs a virtual display driver: the one thing that has to
        // be in place before Pocket XR can show screens in the glasses.
        Border ready = Kit.Chip("Ready", lit: true);
        var help = new PxButton("How to add floating screens", Glyph.Help, ButtonKind.Primary, actions.OpenDriverHelp)
        {
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Theme.Inset, 0, Theme.Inset, 14),
        };
        var driver = new SettingsRow("Virtual display driver", " ", ready);
        b.Add(() =>
        {
            driver.SetNote(state.DriverReady ? "Installed. Pocket XR can make your floating screens."
                : SettingsParts.DriverWords(state));
            SettingsParts.Show(state.DriverReady, ready);
            SettingsParts.Show(!state.DriverReady, help);
        });
        yield return Kit.Card("Floating screens", driver, help);

        yield return Kit.Card("Experimental",
            SettingsParts.ToggleRow(b, "Head tracking for XREAL Air, XBX A01 and VITURE (USB HID)", "Untested on a PC.",
                () => store.S.HidTracking, v => store.Update(s => s.HidTracking = v)));

        // Which glasses do what on a PC, said exactly: only the Aura has been used on one, and only its head tracking.
        yield return Kit.Card("Which glasses work",
            new SettingsRow("XREAL Aura", "Tried on a Steam Deck running Windows: floating screens, 3D and head tracking.",
                Kit.Chip("Works", lit: true)),
            Kit.Divider(),
            new SettingsRow("XREAL One, One Pro, One Pro+, 1S", "The same head tracking as the Aura. A tester uses the 1S on Windows 11, in Steady mode too; the others are not tried on a PC yet.",
                Kit.Chip("1S works")),
            Kit.Divider(),
            new SettingsRow("XREAL Air family and VITURE", "Head tracking with the experimental switch above. Untested on a PC.",
                Kit.Chip("Experimental")),
            Kit.Divider(),
            new SettingsRow("Other glasses", "Screens shown without head tracking. Not tried on a PC yet.",
                Kit.Chip("Screens only")));

        yield return Kit.Card("Tools",
            new SettingsRow("Glasses report", "Saves what Pocket XR knows about your glasses to a file, to send when something is wrong.",
                new PxButton("Save a Glasses report", SettingsGlyph.Document, ButtonKind.Glass, actions.SaveGlassesReport)),
            Kit.Divider(),
            new SettingsRow("Displays", "Where Windows places your floating screens.",
                new PxButton("Open Display settings", Glyph.Screen, ButtonKind.Glass, actions.OpenDisplaySettings)));
    }

    // MARK: Shortcuts

    private static IEnumerable<FrameworkElement> Shortcuts(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        yield return Kit.Card(null,
            SettingsParts.ToggleRow(b, "Keyboard shortcuts", "They work in any app while Pocket XR is open.",
                () => store.S.Shortcuts, v => store.Update(s => s.Shortcuts = v)),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Move the screens with the pointer",
                "Hold Ctrl and Alt: moving the pointer carries the screens with it.",
                () => store.S.GrabKeys, v => store.Update(s => s.GrabKeys = v)),
            Kit.Divider(),
            SettingsParts.ToggleRow(b, "Open the control panel when Pocket XR starts",
                "It is always in the notification area, under the glasses icon.",
                () => store.S.PanelAtStart, v => store.Update(s => s.PanelAtStart = v)));

        yield return SettingsShortcuts.Card(store, state, actions, b);
    }

    // MARK: Help

    private static IEnumerable<FrameworkElement> Help(SettingsStore store, AppState state, IAppActions actions, Binder b, SettingsShow show)
    {
        yield return Kit.Card(null,
            new SettingsRow("Tour of Pocket XR", "Two minutes: setup, your screens, Recentre, 3D and Steady mode",
                new PxButton("Open the tour", Glyph.Play, ButtonKind.Primary, actions.OpenTour)),
            Kit.Divider(),
            new SettingsRow("Send logs to Mike", "Makes a zip of the log, with emails, Wi-Fi names and other addresses taken out, and opens an email.",
                new PxButton("Send logs", SettingsGlyph.Send, ButtonKind.Glass, actions.SendLogs)));

        bool confirming = show.ConfirmReset;
        void SetConfirming(bool on)
        {
            confirming = on;
            b.Sync();
        }
        IEnumerable<UIElement?> FileRows(Binder own)
        {
            yield return new SettingsRow("Save settings to a file", null,
                new PxButton("Save…", SettingsGlyph.SaveFile, ButtonKind.Glass, actions.ExportSettings));
            yield return Kit.Divider();
            yield return new SettingsRow("Restore settings from a file", null,
                new PxButton("Restore…", SettingsGlyph.OpenFile, ButtonKind.Glass, actions.ImportSettings));
            yield return Kit.Divider();
            if (confirming)
            {
                yield return new SettingsRow("Reset to defaults",
                    "Every setting goes back to how it came, saved arrangements included. This cannot be undone.",
                    Kit.Across(8,
                        new PxButton("Cancel", null, ButtonKind.Glass, () => SetConfirming(false)),
                        new PxButton("Reset everything", null, ButtonKind.Danger, () =>
                        {
                            confirming = false;
                            store.Reset();
                            b.Sync();
                        })));
            }
            else
            {
                yield return new SettingsRow("Reset to defaults", null,
                    new PxButton("Reset…", null, ButtonKind.Danger, () => SetConfirming(true)));
            }
        }
        yield return SettingsParts.LiveCard(b, "Your settings", () => confirming, FileRows);

        TextBlock version = Kit.Text(state.Version, Theme.LineSize, Theme.Ink2, wrap: false, digits: true);
        yield return Kit.Card("Pocket XR",
            SettingsParts.ToggleRow(b, "Open when I sign in to Windows", "Pocket XR waits in the notification area for your glasses.",
                () => store.S.LaunchAtLogin, v => store.Update(s => s.LaunchAtLogin = v)),
            Kit.Divider(),
            new SettingsRow("Version", "Free and open source, GPL-3.0.", version),
            Kit.Divider(),
            new SettingsRow("Quit", "Gives the glasses back the settings Pocket XR changed, then closes.",
                new PxButton("Quit and put the glasses back", Glyph.Power, ButtonKind.Glass, actions.Quit)));
    }
}
