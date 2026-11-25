using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// Pocket XR's control panel: the app's home, a small window on the PC's own screen. From the top: what is plugged
// in, a map of the displays (click a screen to put the pointer on it), a pad that moves and resizes the floating
// screens by hand, the layout, size, distance and curve, 3D and Steady mode. Always dark, in the settings window's
// look.

public static class ControlPanel
{
    public const double Width = 440, Side = 16, Gap = 14, MapHeight = 64, PadHeight = 146, SliderRow = 34;
    /// <summary>The pad shows this many degrees up and down at its largest scale.</summary>
    public const double PadPitchSpan = 68;

    /// <summary>The window, kept in step with the settings and the app's state.</summary>
    public static PxWindow Window(SettingsStore store, AppState state, IAppActions actions)
    {
        var binder = new Binder();
        FrameworkElement column = Column(store, state, actions, binder);
        var scroll = new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        // Its height can be changed (its width stays), and it never runs past the bottom of the screen it is on:
        // what does not fit scrolls (ScreenFit).
        var w = new PxWindow("Pocket XR", scroll, StripRight(state, binder), resizable: true)
        {
            Width = Width, MinWidth = Width, MaxWidth = Width, MinHeight = 320, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(SystemParameters.WorkArea.Height - 12, 320),
        };
        ScreenFit.Keep(w);
        store.Changed += (_, _) => binder.Sync();
        state.Changed += binder.Sync;
        return w;
    }

    /// <summary>What the picture is doing now, at the right of the strip: frames a second, 3D, Steady.</summary>
    public static FrameworkElement StripRight(AppState state, Binder binder)
    {
        TextBlock fps = Kit.Text("", 11, Theme.Ink2, wrap: false, digits: true);
        fps.VerticalAlignment = VerticalAlignment.Center;
        Border depth = Kit.Chip("3D"), steady = Kit.Chip("Steady");
        StackPanel row = Kit.Across(6, fps, depth, steady);
        binder.Add(() =>
        {
            row.Visibility = state.GlassesName != null ? Visibility.Visible : Visibility.Collapsed;
            fps.Text = $"{(int)Math.Round(state.Fps)} fps";
            Kit.SetChip(depth, state.Stereo);
            Kit.SetChip(steady, state.Steady);
        });
        return row;
    }

    /// <summary>Everything under the strip.</summary>
    public static FrameworkElement Column(SettingsStore store, AppState state, IAppActions actions, Binder binder)
    {
        var column = new StackPanel { Margin = new Thickness(Side, 6, Side, 12), Width = Width - 2 * Side };
        column.Children.Add(Theme.Arrive(Status(state, binder), 0));
        column.Children.Add(Theme.Arrive(Spaced(MapCard(store, state, actions, binder)), 1));
        column.Children.Add(Theme.Arrive(Spaced(PlaceCard(store, state, actions, binder)), 2));
        column.Children.Add(Theme.Arrive(Spaced(SpaceCard(store, state, actions, binder)), 3));
        column.Children.Add(Theme.Arrive(Spaced(PhoneCard(store, state, actions, binder)), 4));
        column.Children.Add(Theme.Arrive(Spaced(Footer(actions)), 5));
        // The version running now, so a copy left running since before an update is plain to see.
        TextBlock version = Kit.Small($"Pocket XR for Windows {state.Version}");
        version.Margin = new Thickness(4, 10, 4, 0);
        column.Children.Add(version);
        return column;
    }

    private static T Spaced<T>(T e) where T : FrameworkElement
    {
        e.Margin = new Thickness(0, Gap, 0, 0);
        return e;
    }

    // MARK: status

    /// <summary>The glasses and one line saying where things stand.</summary>
    private static FrameworkElement Status(AppState state, Binder binder)
    {
        Grid dot = Kit.Dot(false);
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 7, 10, 0);
        TextBlock title = Kit.Text("", 19, Theme.Ink, FontWeights.Medium, wrap: false);
        TextBlock line = Kit.Small("");
        line.Margin = new Thickness(0, 2, 0, 0);
        line.MaxHeight = 36;
        var words = new StackPanel();
        words.Children.Add(title);
        words.Children.Add(line);
        var row = new DockPanel { Margin = new Thickness(4, 0, 4, 2) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(words);
        AutomationProperties.SetName(row, "Status");
        binder.Add(() =>
        {
            string name = state.GlassesName ?? "Plug in your glasses";
            title.Text = name;
            Kit.SetDot(dot, state.TrackingOn);
            // The status line without what the title already says.
            string text = state.StatusLine;
            if (state.GlassesName is string g && text.StartsWith(g, StringComparison.Ordinal))
                text = text[g.Length..].TrimStart(' ', '·');
            string bare = text.Trim('.', ' ');
            bool show = bare.Length > 0 && bare != name;
            line.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) line.Text = char.ToUpperInvariant(text[0]) + text[1..];
        });
        return row;
    }

    // MARK: the map of displays

    /// <summary>"Your screens": the map, and one line that teaches how the pointer gets to a floating screen.</summary>
    private static FrameworkElement MapCard(SettingsStore store, AppState state, IAppActions actions, Binder binder)
    {
        var map = new MapView(state, actions) { Height = MapHeight, Margin = new Thickness(0, 2, 0, 8) };
        TextBlock rule = Kit.Small("");
        TextBlock count = Kit.Line("");
        count.VerticalAlignment = VerticalAlignment.Center;
        var pair = new PairButton(Glyph.Minus, "Remove a screen", Glyph.Plus, "Add a screen",
            () => SetScreens(store, state, store.S.Screens - 1), () => SetScreens(store, state, store.S.Screens + 1));
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(pair, Dock.Right);
        bottom.Children.Add(pair);
        bottom.Children.Add(count);
        var help = new PxButton("How to add floating screens", Glyph.Help, ButtonKind.Glass, actions.OpenDriverHelp)
        {
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
        };
        var inner = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        inner.Children.Add(map);
        inner.Children.Add(rule);
        inner.Children.Add(help);
        inner.Children.Add(bottom);
        binder.Add(() =>
        {
            int floating = state.DisplayTiles.Count(t => !t.IsOwn);
            map.Visibility = state.DisplayTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Screens above one another need a taller map to stay readable.
            if (state.DisplayTiles.Count > 0)
            {
                double top = state.DisplayTiles.Min(t => t.Frame.Top), bottom = state.DisplayTiles.Max(t => t.Frame.Bottom);
                double tallest = state.DisplayTiles.Max(t => t.Frame.Height);
                map.Height = bottom - top > tallest * 1.2 ? MapHeight * 1.6 : MapHeight;
            }
            map.InvalidateVisual();
            bool noDriver = !state.DriverReady && floating == 0;
            help.Visibility = noDriver ? Visibility.Visible : Visibility.Collapsed;
            // With no driver the count can do nothing, so it is not offered; what the glasses show is said instead.
            bottom.Visibility = noDriver ? Visibility.Collapsed : Visibility.Visible;
            if (noDriver)
            {
                rule.Text = (state.ShowingOwnScreen ? "Your glasses show this PC's screen. " : "")
                    + "Floating screens need a virtual display driver, which Windows asks an administrator to install once.";
            }
            else if (floating == 0)
            {
                rule.Text = "Floating screens appear when your glasses are plugged in.";
            }
            else
            {
                string edge = MapLayout.EdgeWord(state.DisplayTiles) ?? "right";
                rule.Text = floating == 1
                    ? $"Move the pointer off the {edge} edge of this screen to reach your floating screen, or click it here."
                    : $"Move the pointer off the {edge} edge of this screen to reach your floating screens, or click one here.";
            }
            rule.Foreground = floating == 0 && !state.ShowingOwnScreen ? Theme.Ink3 : Theme.Ink2;
            count.Text = store.S.Screens == 1 ? "1 floating screen" : $"{store.S.Screens} floating screens";
        });
        return Kit.Card("Your screens", inner);
    }

    /// <summary>One screen more or fewer, 1 to 6, at most what the driver makes.</summary>
    private static void SetScreens(SettingsStore store, AppState state, int n)
    {
        int to = Math.Clamp(n, SpaceSettings.ScreensRange.LowerBound, ScreenCount.Offered(state.DriverLimit));
        if (to != store.S.Screens) store.Update(s => s.Screens = to);
    }

    /// <summary>The displays drawn to scale inside a box: where each tile goes, and the arrow from the PC's screen to
    /// the first floating screen.</summary>
    public sealed class MapLayout
    {
        public readonly List<(DisplayTile Tile, Rect Rect)> Placed = new();
        public readonly (Point From, Point To)? Arrow;

        public MapLayout(IReadOnlyList<DisplayTile> tiles, Size size)
        {
            if (tiles.Count == 0) return;
            Rect whole = tiles[0].Frame;
            foreach (DisplayTile t in tiles.Skip(1)) whole.Union(t.Frame);
            double scale = Math.Min(size.Width / Math.Max(whole.Width, 1), size.Height / Math.Max(whole.Height, 1));
            double dx = (size.Width - whole.Width * scale) / 2 - whole.X * scale;
            double dy = (size.Height - whole.Height * scale) / 2 - whole.Y * scale;
            foreach (DisplayTile t in tiles)
                Placed.Add((t, new Rect(t.Frame.X * scale + dx, t.Frame.Y * scale + dy, t.Frame.Width * scale, t.Frame.Height * scale)));
            int own = Placed.FindIndex(p => p.Tile.IsOwn), first = Placed.FindIndex(p => !p.Tile.IsOwn);
            if (own < 0 || first < 0) return;
            Rect o = Placed[own].Rect, f = Placed[first].Rect;
            string e = Edge(o, f);
            // A short tail on the PC's side, clear of its name, and the head well inside the floating screen.
            // On a small tile the head stops short of the tile's name.
            double tail = 5, reach = Math.Min(16, (e is "right" or "left" ? f.Width : f.Height) * 0.16);
            if (e is "right" or "left")
            {
                double lo = Math.Max(o.Top, f.Top), hi = Math.Min(o.Bottom, f.Bottom);
                double y = hi > lo ? (lo + hi) / 2 : o.Top + o.Height / 2;
                double x = e == "right" ? o.Right : o.Left, sign = e == "right" ? 1 : -1;
                Arrow = (new Point(x - sign * tail, y), new Point(x + sign * reach, y));
            }
            else
            {
                double lo = Math.Max(o.Left, f.Left), hi = Math.Min(o.Right, f.Right);
                double x = hi > lo ? (lo + hi) / 2 : o.Left + o.Width / 2;
                double y = e == "bottom" ? o.Bottom : o.Top, sign = e == "bottom" ? 1 : -1;
                Arrow = (new Point(x, y - sign * tail), new Point(x, y + sign * reach));
            }
        }

        /// <summary>The PC's edge that [f] lies beyond (y runs down, as displays are arranged).</summary>
        public static string Edge(Rect o, Rect f)
        {
            const double slack = 1;
            if (f.Left >= o.Right - slack) return "right";
            if (f.Right <= o.Left + slack) return "left";
            if (f.Top >= o.Bottom - slack) return "bottom";
            if (f.Bottom <= o.Top + slack) return "top";
            double dx = f.Left + f.Width / 2 - (o.Left + o.Width / 2), dy = f.Top + f.Height / 2 - (o.Top + o.Height / 2);
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? "right" : "left";
            return dy >= 0 ? "bottom" : "top";
        }

        public static string? EdgeWord(IReadOnlyList<DisplayTile> tiles)
        {
            DisplayTile? o = tiles.FirstOrDefault(t => t.IsOwn), f = tiles.FirstOrDefault(t => !t.IsOwn);
            return o == null || f == null ? null : Edge(o.Frame, f.Frame);
        }
    }

    /// <summary>The tiles, to scale, with the arrow across the edge the pointer leaves by. The tile the pointer is on
    /// is lit; a click puts the pointer on that screen.</summary>
    private sealed class MapView : FrameworkElement
    {
        private readonly AppState state;
        private readonly IAppActions actions;
        private int hover = int.MinValue;
        private static readonly Pen ArrowUnder = Frozen(new Pen(Theme.B(Look.Page.Alpha(0.85)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        private static readonly Pen ArrowPen = Frozen(new Pen(Theme.Held, 1.75) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

        public MapView(AppState state, IAppActions actions)
        {
            this.state = state;
            this.actions = actions;
            AutomationProperties.SetName(this, "Map of your screens");
        }

        private MapLayout Layout => new(state.DisplayTiles, new Size(ActualWidth, ActualHeight));

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            MapLayout layout = Layout;
            int pointerOn = state.PointerScreen ?? -1;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach ((DisplayTile tile, Rect rect) in layout.Placed)
            {
                bool lit = tile.Id == pointerOn && state.DisplayTiles.Count > 1, over = tile.Id == hover;
                var r = new Rect(rect.X + 2, rect.Y + 2, Math.Max(rect.Width - 4, 8), Math.Max(rect.Height - 4, 8));
                Brush fill = lit
                    ? new LinearGradientBrush(Theme.C(Look.Accent.Alpha(0.30)), Theme.C(Look.Accent.Alpha(0.16)), 90)
                    : over ? Theme.GlassHover : Theme.Glass;
                Brush rim = lit ? Theme.Accent : over ? Theme.RimFocus : Theme.Rim;
                double half = (lit ? 1.5 : 1) / 2;
                dc.DrawRoundedRectangle(fill, new Pen(rim, lit ? 1.5 : 1),
                    new Rect(r.X + half, r.Y + half, Math.Max(r.Width - 2 * half, 1), Math.Max(r.Height - 2 * half, 1)), 6, 6);
                var name = Draw.Text(tile.Title, 11.5, lit ? Theme.Ink : Theme.Ink2, lit ? FontWeights.Medium : FontWeights.Normal, dip);
                // A name too wide for a small tile is drawn smaller, down to 0.7 of its size.
                if (name.Width > r.Width - 8) name.SetFontSize(Math.Max(11.5 * 0.7, 11.5 * (r.Width - 8) / name.Width));
                name.MaxTextWidth = Math.Max(r.Width - 8, 8);
                name.MaxLineCount = 1;
                name.Trimming = TextTrimming.CharacterEllipsis;
                name.TextAlignment = TextAlignment.Center;
                double textY = r.Y + (r.Height - name.Height) / 2 - (lit ? 6 : 0);
                dc.DrawText(name, new Point(r.X + 4, textY));
                if (lit)
                {
                    var cursor = Draw.Icon(Glyph.Cursor, 9, Theme.Held, dip);
                    dc.DrawText(cursor, new Point(r.X + (r.Width - cursor.Width) / 2, textY + name.Height + 2));
                }
            }
            if (layout.Arrow is var (a, b))
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.001);
                double ux = dx / length, uy = dy / length;
                const double head = 5;
                var g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                {
                    c.BeginFigure(a, false, false);
                    c.LineTo(b, true, true);
                    c.BeginFigure(new Point(b.X - ux * head - uy * head, b.Y - uy * head + ux * head), false, false);
                    c.LineTo(b, true, true);
                    c.LineTo(new Point(b.X - ux * head + uy * head, b.Y - uy * head - ux * head), true, true);
                }
                g.Freeze();
                // A dark edge under it, so it reads across the tiles' rims.
                dc.DrawGeometry(null, ArrowUnder, g);
                dc.DrawGeometry(null, ArrowPen, g);
            }
        }

        private int TileAt(Point p)
        {
            foreach ((DisplayTile tile, Rect rect) in Layout.Placed)
                if (rect.Contains(p)) return tile.Id;
            return int.MinValue;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int now = TileAt(e.GetPosition(this));
            if (now == hover) return;
            hover = now;
            ToolTip = now == int.MinValue ? null
                : Kit.Tip(now < 0 ? "Put the pointer on this PC's screen" : $"Put the pointer on Screen {now + 1}");
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e) { hover = int.MinValue; InvalidateVisual(); }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            int at = TileAt(e.GetPosition(this));
            if (at != int.MinValue) actions.MovePointer(at < 0 ? null : at);
        }
    }

    // MARK: the pad

    /// <summary>Degrees to points on the pad: straight ahead in the middle, left to the left, up at the top. The scale
    /// fits the screens' span and keeps them clear of the words and buttons over the pad ([keepOut], PadFit); a drag
    /// holds the scale it started with ([held]), so the screens do not grow or shrink under the pointer.</summary>
    public readonly struct PadGeometry
    {
        public readonly Size Size;
        /// <summary>Points per degree.</summary>
        public readonly double Scale;

        public PadGeometry(IReadOnlyList<PanelPlacement> panels, Size size, IReadOnlyList<PadFit.Box>? keepOut = null, double? held = null)
        {
            Size = size;
            Scale = held ?? PadFit.Scale(panels, size.Width, size.Height, PadPitchSpan, keepOut ?? Array.Empty<PadFit.Box>());
        }

        public Rect Rect(double yawDeg, double pitchDeg, double widthDeg, double heightDeg)
        {
            PadFit.Box b = PadFit.Place(yawDeg, pitchDeg, widthDeg, heightDeg, Size.Width, Size.Height, Scale);
            return new Rect(b.X, b.Y, b.W, b.H);
        }
    }

    /// <summary>"Place": the pad, and Recentre under it.</summary>
    private static FrameworkElement PlaceCard(SettingsStore store, AppState state, IAppActions actions, Binder binder)
    {
        var pad = new PadView(store, state, actions) { Height = PadHeight };
        TextBlock hint = Kit.Text("", 12, Theme.Ink2);
        hint.Margin = new Thickness(12, 9, 12, 0);
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.HorizontalAlignment = HorizontalAlignment.Left;
        hint.IsHitTestVisible = false;

        // What Steady mode means here, in the middle of the pad.
        TextBlock steadyWords = Kit.Small("Steady mode: the glasses hold the picture.\n− and + change its size.");
        steadyWords.Foreground = Theme.Ink;
        steadyWords.TextAlignment = TextAlignment.Center;
        var steadyNote = new Border
        {
            Child = steadyWords, MaxWidth = 320, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 26),
            CornerRadius = new CornerRadius(Theme.ControlRadius + 2), Background = Theme.B(Look.Card.Alpha(0.94)),
            BorderBrush = Theme.Rim, BorderThickness = new Thickness(1), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        var lineUp = new SmallButton("Line up", actions.ResetScreenMoves)
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
        };
        var size = new PairButton(Glyph.Minus, "Smaller", Glyph.Plus, "Bigger",
            () => actions.Adjust(SpaceAdjust.Smaller), () => actions.Adjust(SpaceAdjust.Bigger))
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10),
        };
        var nearer = new SmallButton("Nearer", () => actions.Adjust(SpaceAdjust.Nearer));
        var further = new SmallButton("Further", () => actions.Adjust(SpaceAdjust.Further));
        StackPanel depth = SettingsPanes.DepthOnly(binder, state, Kit.Across(6, nearer, further));
        depth.HorizontalAlignment = HorizontalAlignment.Right;
        depth.VerticalAlignment = VerticalAlignment.Bottom;
        depth.Margin = new Thickness(10);

        // The words and buttons over the pad, which the screens are kept clear of: whichever are showing, where they
        // are on the pad now.
        pad.KeepOut = () =>
        {
            var boxes = new List<PadFit.Box>();
            foreach (FrameworkElement e in new FrameworkElement[] { hint, lineUp, size, depth })
            {
                if (e.Visibility != Visibility.Visible || e.ActualWidth <= 0 || e.ActualHeight <= 0) continue;
                Point at = e.TranslatePoint(new Point(0, 0), pad);
                boxes.Add(new PadFit.Box(at.X, at.Y, e.ActualWidth, e.ActualHeight));
            }
            return boxes;
        };
        foreach (FrameworkElement e in new FrameworkElement[] { hint, lineUp, size, depth })
            e.SizeChanged += (_, _) => pad.InvalidateVisual();

        var surface = new Grid { ClipToBounds = true };
        surface.Children.Add(pad);
        surface.Children.Add(hint);
        surface.Children.Add(steadyNote);
        surface.Children.Add(lineUp);
        surface.Children.Add(size);
        surface.Children.Add(depth);
        surface.MouseEnter += (_, _) => hint.Foreground = Theme.Ink;
        surface.MouseLeave += (_, _) => hint.Foreground = Theme.Ink2;

        var recentre = new RecentreButton(actions.Recentre) { Margin = new Thickness(12) };
        binder.Add(() =>
        {
            pad.InvalidateVisual();
            bool steady = state.Steady;
            hint.Visibility = steady ? Visibility.Collapsed : Visibility.Visible;
            steadyNote.Visibility = steady ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = state.Panels.Count == 0 ? "Your floating screens will show here."
                : state.Panels.Count == 1 ? "Drag to move. Scroll to resize."
                : "Drag one screen, or beside them for all. Scroll to resize.";
            lineUp.Visibility = !steady && store.S.ScreensMoved ? Visibility.Visible : Visibility.Collapsed;
            // A flat picture has no nearer or further: in Steady mode only the size changes.
            nearer.IsEnabled = !steady;
            further.IsEnabled = !steady;
            recentre.SetKeys(store.S.Shortcuts ? Shortcut.Find("recentre").Keys : null);
        });
        // The pad's top corners follow the card's.
        var top = new Border { CornerRadius = new CornerRadius(Theme.CardRadius - 1, Theme.CardRadius - 1, 0, 0), ClipToBounds = true, Child = surface };
        surface.SizeChanged += (_, _) =>
            surface.Clip = TopRounded(new Size(surface.ActualWidth, surface.ActualHeight), Theme.CardRadius - 1);
        surface.Clip = TopRounded(new Size(Width - 2 * Side - 2, PadHeight), Theme.CardRadius - 1);
        return Kit.Card("Place", top, new Border { Height = 1, Background = Theme.Hairline }, recentre);
    }

    private static Geometry TopRounded(Size s, double r)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext c = g.Open())
        {
            c.BeginFigure(new Point(0, s.Height), true, true);
            c.LineTo(new Point(0, r), false, false);
            c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(s.Width - r, 0), false, false);
            c.ArcTo(new Point(s.Width, r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(s.Width, s.Height), false, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The pad itself: the floating screens as the wearer sees them, from the front. Drag moves one screen or
    /// the whole Space; the wheel resizes.</summary>
    private sealed class PadView : FrameworkElement
    {
        private readonly SettingsStore store;
        private readonly AppState state;
        private readonly IAppActions actions;
        private bool dragging;
        private int? dragScreen;
        private Point last;
        private const double DegreesPerNotch = 1.5;
        private static readonly LinearGradientBrush Ground = Frozen(new LinearGradientBrush(Theme.C(Look.Pad), Theme.C(Look.Page), 90));
        private static readonly Pen Horizon = Frozen(new Pen(Theme.White(0.06), 1));
        private static readonly Pen Tick = Frozen(new Pen(Theme.White(0.14), 1));
        private static readonly Brush ViewFill = Theme.B(Look.Accent.Alpha(0.07));
        private static readonly Pen ViewPen = Frozen(new Pen(Theme.B(Look.Accent.Alpha(0.34)), 1));
        private static readonly LinearGradientBrush Glass = Frozen(new LinearGradientBrush(Theme.C(Look.GlassTop.Alpha(0.96)), Theme.C(Look.GlassBottom.Alpha(0.96)), 90));
        private static readonly Pen TopLight = Frozen(new Pen(Theme.White(0.30), 1));
        private static readonly Pen HeldHalo = Frozen(new Pen(Theme.B(Look.Held.Alpha(0.25)), 5)), HeldPen = Frozen(new Pen(Theme.Held, 1.5));
        private static readonly Pen LitHalo = Frozen(new Pen(Theme.B(Look.Accent.Alpha(0.22)), 5)), LitPen = Frozen(new Pen(Theme.B(Look.Accent.Alpha(0.95)), 1.5));
        private static readonly Pen Plain = Frozen(new Pen(Theme.RimFocus, 1));

        public PadView(SettingsStore store, AppState state, IAppActions actions)
        {
            this.store = store;
            this.state = state;
            this.actions = actions;
            AutomationProperties.SetName(this, "Place your screens");
        }

        private Size Area => new(ActualWidth, ActualHeight);
        /// <summary>The boxes on the pad the screens keep clear of (PlaceCard sets it).</summary>
        public Func<IReadOnlyList<PadFit.Box>>? KeepOut;
        /// <summary>The scale a drag started with, held until it ends.</summary>
        private double heldScale;

        private PadGeometry Geometry() =>
            new(state.Panels, Area, KeepOut?.Invoke(), dragging && heldScale > 0 ? heldScale : null);

        protected override void OnRender(DrawingContext dc)
        {
            Size size = Area;
            dc.DrawRectangle(Ground, null, new Rect(size));
            if (size.Width <= 20 || size.Height <= 20) return;
            var g = Geometry();
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (state.Steady) dc.PushOpacity(0.28);

            // The horizon, a mark every 30 degrees along it, and a longer one straight ahead.
            double midY = Math.Round(size.Height / 2) + 0.5, midX = size.Width / 2;
            dc.DrawLine(Horizon, new Point(0, midY), new Point(size.Width, midY));
            for (int step = -6; step <= 6; step++)
            {
                double x = Math.Round(midX - step * 30 * g.Scale) + 0.5, half = step == 0 ? 7 : 3;
                if (x > 4 && x < size.Width - 4) dc.DrawLine(Tick, new Point(x, midY - half), new Point(x, midY + half));
            }

            // What the glasses show now: a faint rounded rectangle where the head points.
            if (state.GlassesName != null)
            {
                double fovY = Math.Clamp(state.ViewFovYDeg, 5, 120);
                double fovX = 2 * Math.Atan(Math.Tan(fovY * Math.PI / 360) * Math.Max(state.ViewAspect, 0.2)) * 180 / Math.PI;
                Rect r = g.Rect(state.YawDeg, state.PitchDeg, fovX, fovY);
                dc.DrawRoundedRectangle(ViewFill, ViewPen, r, 9, 9);
                var label = Draw.Text("Your view", 10, Theme.B(Look.Held.Alpha(0.75)), FontWeights.Normal, dip);
                double y = r.Bottom + 3 + label.Height < size.Height - 4 ? r.Bottom + 3 : r.Top - 3 - label.Height;
                dc.DrawText(label, new Point(r.X + (r.Width - label.Width) / 2, y));
            }

            for (int i = 0; i < state.Panels.Count; i++)
            {
                PanelPlacement p = state.Panels[i];
                Rect r = g.Rect(p.YawDeg, p.PitchDeg, p.WidthDeg, p.HeightDeg);
                double radius = Math.Min(5, Math.Min(r.Width, r.Height) / 4);
                dc.DrawRoundedRectangle(Glass, null, r, radius, radius);
                // A line of light along the top edge, as glass catches it.
                dc.DrawLine(TopLight, new Point(r.Left + radius, r.Top + 0.5), new Point(r.Right - radius, r.Top + 0.5));
                bool lit = state.PointerScreen == i;
                bool held = dragging && (dragScreen == null || dragScreen == i);
                if (held)
                {
                    dc.DrawRoundedRectangle(null, HeldHalo, r, radius, radius);
                    dc.DrawRoundedRectangle(null, HeldPen, r, radius, radius);
                }
                else if (lit)
                {
                    dc.DrawRoundedRectangle(null, LitHalo, r, radius, radius);
                    dc.DrawRoundedRectangle(null, LitPen, r, radius, radius);
                }
                else
                {
                    dc.DrawRoundedRectangle(null, Plain, r, radius, radius);
                }
                if (r.Width > 16 && r.Height > 14)
                {
                    var number = Draw.Text((i + 1).ToString(CultureInfo.InvariantCulture), 12, lit || held ? Theme.Ink : Theme.Ink2, FontWeights.Medium, dip);
                    dc.DrawText(number, new Point(r.X + (r.Width - number.Width) / 2, r.Y + (r.Height - number.Height) / 2));
                }
            }
            if (state.Steady) dc.Pop();
        }

        /// <summary>The screen under a point of the pad, the topmost first, null on the space around them.</summary>
        private int? ScreenAt(Point point)
        {
            var g = Geometry();
            for (int i = state.Panels.Count - 1; i >= 0; i--)
            {
                PanelPlacement p = state.Panels[i];
                Rect r = g.Rect(p.YawDeg, p.PitchDeg, p.WidthDeg, p.HeightDeg);
                r.Inflate(3, 3);
                if (r.Contains(point)) return i;
            }
            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (state.Steady) return;
            last = e.GetPosition(this);
            // A drag that starts on a screen moves that screen; one that starts beside them moves them all.
            dragScreen = state.Panels.Count > 1 ? ScreenAt(last) : null;
            heldScale = Geometry().Scale;
            dragging = true;
            CaptureMouse();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            Point now = e.GetPosition(this);
            double scale = Geometry().Scale;
            double dx = now.X - last.X, dy = now.Y - last.Y;
            last = now;
            if (scale <= 0 || (dx == 0 && dy == 0)) return;
            // Yaw is left positive and pitch up positive; the pad's x runs right and its y down.
            if (dragScreen is int one) actions.NudgeScreen(one, -dx / scale, -dy / scale);
            else actions.NudgeSpace(-dx / scale, -dy / scale);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            dragScreen = null;
            ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // One notch of the wheel is 120; a touchpad sends many small steps.
            double notches = e.Delta / 120.0;
            if (state.Steady)
                store.Update(s => s.SteadyZoom += notches * DegreesPerNotch / 40);
            else
                store.Update(s => s.WidthDeg += notches * DegreesPerNotch);
            e.Handled = true;
        }
    }

    // MARK: the pad's buttons

    /// <summary>The panel's one main button, as wide as its card: "Recentre", with its shortcut at the right.</summary>
    private sealed class RecentreButton : Press
    {
        private readonly SolidColorBrush fill = new(Theme.C(Look.Accent));
        private readonly TextBlock keys;
        private readonly Border ring;

        public RecentreButton(Action action)
        {
            Label = "Recentre";
            Height = 34;
            CornerRadius = new CornerRadius(17);
            Background = fill;
            ToolTip = Kit.Tip("Bring the screens back in front of you");
            StackPanel middle = Kit.Across(6, Kit.Icon(Glyph.Recentre, 12, Theme.Page), Kit.Text("Recentre", Theme.ButtonSize, Theme.Page, FontWeights.Medium, wrap: false));
            middle.HorizontalAlignment = HorizontalAlignment.Center;
            middle.VerticalAlignment = VerticalAlignment.Center;
            keys = Kit.Text("", 12, Theme.Page, FontWeights.Medium, wrap: false);
            keys.Opacity = 0.62;
            keys.HorizontalAlignment = HorizontalAlignment.Right;
            keys.VerticalAlignment = VerticalAlignment.Center;
            keys.Margin = new Thickness(0, 0, 14, 1);
            ring = new Border { CornerRadius = new CornerRadius(18), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Margin = new Thickness(-2), Opacity = 0, IsHitTestVisible = false };
            var g = new Grid();
            g.Children.Add(middle);
            g.Children.Add(keys);
            g.Children.Add(ring);
            Child = g;
            Click += action;
        }

        public void SetKeys(string? text)
        {
            keys.Text = text ?? "";
        }

        protected override void Restyle()
        {
            Theme.AnimateColor(fill, Theme.C(Hover ? Look.Held : Look.Accent));
            ring.Opacity = Ring ? 1 : 0;
        }
    }

    /// <summary>A small glass pill on the pad.</summary>
    public sealed class SmallButton : Press
    {
        public SmallButton(string title, Action action)
        {
            Label = title;
            Height = 26;
            CornerRadius = new CornerRadius(13);
            BorderThickness = new Thickness(1);
            TextBlock t = Kit.Text(title, 12, Theme.Ink, FontWeights.Medium, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(10, 0, 10, 1);
            Child = t;
            Click += action;
            Restyle();
        }

        protected override void Restyle()
        {
            Background = Hover ? Theme.GlassHover : Theme.B(Look.Control.Alpha(0.9));
            BorderBrush = Ring ? Theme.Accent : Hover ? Theme.RimFocus : Theme.Rim;
            BorderThickness = new Thickness(Ring ? 2 : 1);
        }
    }

    /// <summary>Two symbols in one pill: smaller and bigger.</summary>
    public sealed class PairButton : Border
    {
        public PairButton(string leftGlyph, string leftLabel, string rightGlyph, string rightLabel, Action left, Action right)
        {
            Height = 26;
            CornerRadius = new CornerRadius(13);
            Background = Theme.B(Look.Control.Alpha(0.9));
            BorderBrush = Theme.Rim;
            BorderThickness = new Thickness(1);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Half(leftGlyph, leftLabel, left, new CornerRadius(12, 0, 0, 12)));
            row.Children.Add(new Border { Width = 1, Height = 14, Background = Theme.Rim, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new Half(rightGlyph, rightLabel, right, new CornerRadius(0, 12, 12, 0)));
            Child = row;
        }

        private sealed class Half : Press
        {
            private readonly TextBlock icon;

            public Half(string glyph, string label, Action action, CornerRadius corners)
            {
                Label = label;
                Width = 32;
                CornerRadius = corners;
                ToolTip = Kit.Tip(label);
                icon = Kit.Icon(glyph, 10, Theme.Ink);
                Child = icon;
                Click += action;
            }

            protected override void Restyle()
            {
                Background = Hover ? Theme.GlassHover : Brushes.Transparent;
                icon.Foreground = Ring ? Theme.Held : Theme.Ink;
            }
        }
    }

    // MARK: layout, size, 3D

    /// <summary>The layout, three sliders, and the two switches.</summary>
    private static FrameworkElement SpaceCard(SettingsStore store, AppState state, IAppActions actions, Binder binder)
    {
        var holder = new StackPanel();
        var card = new Border
        {
            Background = Theme.Card, BorderBrush = Theme.Rim, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius), Child = holder, SnapsToDevicePixels = true,
        };
        (bool Steady, int Count)? built = null;
        Binder inner = new();
        binder.Add(() =>
        {
            int count = Math.Max(state.VirtualScreens, store.S.Screens);
            var key = (state.Steady, count);
            if (built != key)
            {
                // Steady mode and the number of screens change which rows there are: the card is made again.
                built = key;
                inner = new Binder();
                holder.Children.Clear();
                if (state.Steady)
                {
                    TextBlock words = Kit.Small("In Steady mode the glasses show your screens side by side on one flat picture. Change their size and the gap between them here. Turn Steady mode off for layouts, distance and curve.");
                    words.Margin = new Thickness(16, 10, 16, 2);
                    holder.Children.Add(words);
                    holder.Children.Add(Slider(inner, "Size", () => store.S.SteadyZoom, v => store.Update(s => s.SteadyZoom = v),
                        SpaceSettings.SteadyZoomRange, 0.05, SettingsFormat.Percent));
                    holder.Children.Add(Slider(inner, "Gap", () => store.S.SteadyGapDeg, v => store.Update(s => s.SteadyGapDeg = v),
                        SpaceSettings.SteadyGapRange, 0.1, SettingsFormat.TenthDegrees));
                }
                else
                {
                    holder.Children.Add(Arrangements(store, actions, inner));
                    holder.Children.Add(Kit.Divider());
                    IReadOnlyList<ArrangeLayout> choices = SpaceLayout.Choices(count);
                    var layout = new Segmented(inner, "Layout", choices.Select(c => SpaceLayout.Title(c, count)).ToList(),
                        () => choices.ToList().IndexOf(store.S.Layout), i => store.Update(s => s.Layout = choices[i])).Stretch();
                    layout.Margin = new Thickness(12, 10, 12, 10);
                    layout.SmallText();
                    holder.Children.Add(layout);
                    holder.Children.Add(Kit.Divider());
                    var sliders = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                    sliders.Children.Add(Slider(inner, "Size", () => store.S.WidthDeg, v => store.Update(s => s.WidthDeg = v), SpaceSettings.WidthRange, 1, SettingsFormat.Degrees));
                    sliders.Children.Add(SettingsPanes.DepthOnly(inner, state, Slider(inner, "Distance", () => store.S.Distance, v => store.Update(s => s.Distance = v), SpaceSettings.DistanceRange, 0.1, SettingsFormat.Metres)));
                    sliders.Children.Add(Slider(inner, "Curve", () => store.S.Curve, v => store.Update(s => s.Curve = v), SpaceSettings.CurveRange, 0.05, SettingsFormat.Curve));
                    holder.Children.Add(sliders);
                }
                holder.Children.Add(Kit.Divider());
                var switches = new Grid { Height = 44 };
                switches.ColumnDefinitions.Add(new ColumnDefinition());
                switches.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                switches.ColumnDefinitions.Add(new ColumnDefinition());
                switches.Children.Add(Switch(inner, "3D", () => store.S.Want3D, v => store.Update(s => s.Want3D = v), () => true));
                var bar = new Border { Width = 1, Height = 24, Background = Theme.Hairline };
                Grid.SetColumn(bar, 1);
                switches.Children.Add(bar);
                FrameworkElement steady = Switch(inner, "Steady mode", () => state.Steady, actions.SetSteady, () => state.SteadyAvailable && state.LinkOpen);
                Grid.SetColumn(steady, 2);
                switches.Children.Add(steady);
                holder.Children.Add(switches);
            }
            inner.Sync();
        });
        return card;
    }

    private static Grid Slider(Binder binder, string title, Func<double> get, Action<double> set,
        (double LowerBound, double UpperBound) range, double step, Func<double, string> text)
    {
        Grid row = Kit.SliderRow(binder, title, get, set, range.LowerBound, range.UpperBound, step, text, titleWidth: 66, valueWidth: 46);
        row.MinHeight = SliderRow;
        row.Height = SliderRow;
        return row;
    }

    private static FrameworkElement Switch(Binder binder, string title, Func<bool> get, Action<bool> set, Func<bool> enabled)
    {
        TextBlock name = Kit.Line(title);
        name.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new Toggle(binder, title, get, set) { VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Margin = new Thickness(Theme.Inset, 0, 10, 0) };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(name);
        binder.Add(() =>
        {
            bool on = enabled();
            toggle.IsEnabled = on;
            name.Foreground = on ? Theme.Ink : Theme.Ink3;
        });
        return row;
    }

    /// <summary>The "Arrangement" row: the saved arrangement that is on now, and a list to pick, save or remove one.</summary>
    private static FrameworkElement Arrangements(SettingsStore store, IAppActions actions, Binder binder)
    {
        TextBlock name = Kit.Line("Arrangement");
        name.VerticalAlignment = VerticalAlignment.Center;
        var picker = new PickerButton { VerticalAlignment = VerticalAlignment.Center };
        var popup = new Popup
        {
            PlacementTarget = picker, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            VerticalOffset = 6, HorizontalOffset = -80,
        };
        picker.Click += () =>
        {
            popup.Child = ArrangementList(store, actions, () => popup.IsOpen = false);
            popup.IsOpen = true;
        };
        var row = new DockPanel { Height = 40, Margin = new Thickness(16, 0, 16, 0) };
        DockPanel.SetDock(picker, Dock.Right);
        row.Children.Add(picker);
        row.Children.Add(name);
        binder.Add(() =>
        {
            SpacePreset? current = store.S.Presets.FirstOrDefault(p => store.S.Matches(p));
            picker.SetTitle(current?.Name ?? (store.S.Presets.Count == 0 ? "Save or load" : "Not saved"));
            if (popup.IsOpen) popup.Child = ArrangementList(store, actions, () => popup.IsOpen = false);
        });
        return row;
    }

    /// <summary>The list under the Arrangement button.</summary>
    public static FrameworkElement ArrangementList(SettingsStore store, IAppActions actions, Action close)
    {
        var list = new StackPanel { Width = 234 };
        IReadOnlyList<SpacePreset> saved = store.S.Presets;
        if (saved.Count == 0)
        {
            TextBlock none = Kit.Text("Nothing saved yet.", Theme.NoteSize, Theme.Ink3);
            none.Margin = new Thickness(10, 6, 10, 6);
            list.Children.Add(none);
        }
        foreach (SpacePreset p in saved)
        {
            SpacePreset preset = p;
            var line = new DockPanel();
            var bin = new IconButton(Glyph.Delete, $"Remove {p.Name}", () => store.Update(s => s.Presets.RemoveAll(x => x.Id == preset.Id)));
            DockPanel.SetDock(bin, Dock.Right);
            line.Children.Add(bin);
            line.Children.Add(new ListRow(p.Name, store.S.Matches(p) ? Glyph.Check : null, false, () =>
            {
                store.Update(s => s.Apply(preset));
                close();
            }));
            list.Children.Add(line);
        }
        list.Children.Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 4, 0, 4) });
        list.Children.Add(new ListRow("Save this arrangement", Glyph.Plus, true, () => store.Update(s => s.SaveArrangement())));
        if (saved.Count > 0)
        {
            list.Children.Add(new ListRow("Rename in Settings…", null, false, () =>
            {
                close();
                actions.OpenSettings();
            }));
        }
        return new Border
        {
            Child = list, Padding = new Thickness(8), Background = Theme.Card, BorderBrush = Theme.RimFocus,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Theme.CardRadius),
        };
    }

    private sealed class PickerButton : Press
    {
        private readonly TextBlock title;

        public PickerButton()
        {
            Label = "Arrangement";
            Height = 28;
            CornerRadius = new CornerRadius(14);
            BorderBrush = Theme.Rim;
            BorderThickness = new Thickness(1);
            title = Kit.Line("");
            title.TextWrapping = TextWrapping.NoWrap;
            title.VerticalAlignment = VerticalAlignment.Center;
            StackPanel row = Kit.Across(6, title, Kit.Icon(Glyph.ChevronDown, 9, Theme.Ink2));
            row.Margin = new Thickness(12, 0, 12, 1);
            Child = row;
            Restyle();
        }

        public void SetTitle(string text) => title.Text = text;

        protected override void Restyle() => Background = Hover ? Theme.GlassHover : Theme.White(0.05);
    }

    private sealed class ListRow : Press
    {
        public ListRow(string text, string? glyph, bool strong, Action action)
        {
            Label = text;
            Height = 30;
            CornerRadius = new CornerRadius(7);
            DimOnPress = false;
            TextBlock t = Kit.Text(text, Theme.LineSize, strong ? Theme.Held : Theme.Ink, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(10, 0, 10, 1) };
            if (glyph != null)
            {
                TextBlock icon = Kit.Icon(glyph, 11, Theme.Accent);
                DockPanel.SetDock(icon, Dock.Right);
                row.Children.Add(icon);
            }
            row.Children.Add(t);
            Child = row;
            Click += action;
        }

        protected override void Restyle() => Background = Hover ? Theme.GlassHover : Brushes.Transparent;
    }

    // MARK: phone

    /// <summary>"Let my phone stream this PC": this PC's own host and the phone link, with one line on where it stands.</summary>
    private static FrameworkElement PhoneCard(SettingsStore store, AppState state, IAppActions actions, Binder binder)
    {
        UIElement? other = null;
        if (PcScreens.PcScreenLauncher.Available)
        {
            var show = new PxButton(PcScreens.PcScreenLauncher.Title, null, ButtonKind.Glass, actions.OpenPcScreen) { HorizontalAlignment = HorizontalAlignment.Left };
            TextBlock note = Kit.Small(PcScreens.PcScreenLauncher.Note);
            note.Margin = new Thickness(0, 8, 0, 0);
            other = new StackPanel { Margin = new Thickness(Theme.Inset, 12, Theme.Inset, 14), Children = { show, note } };
        }
        var row = new Grid { Height = 44 };
        row.Children.Add(Switch(binder, "Let my phone stream this PC", () => store.S.PhoneStreamsPc, actions.SetPhoneStreams, () => true));
        TextBlock words = Kit.Small("");
        words.Margin = new Thickness(Theme.Inset, 0, Theme.Inset, 12);
        binder.Add(() => words.Text = state.PhoneStatus ?? (store.S.PhoneStreamsPc ? "Starting…"
            : "Off. When it is on, Pocket XR on your phone shows this PC's screen in your glasses, pairs with no typing and can start SteamVR."));
        return other == null ? Kit.Card("Phone and other computers", row, words)
            : Kit.Card("Phone and other computers", row, words, Kit.Divider(), other);
    }

    // MARK: footer

    private static FrameworkElement Footer(IAppActions actions)
    {
        StackPanel first = Kit.Across(8,
            new PxButton("Launcher", Glyph.Apps, ButtonKind.Glass, actions.ToggleLauncher),
            new PxButton("Pocket mode", LauncherGlyph.Moon, ButtonKind.Glass, () => actions.SetPocketMode(true)),
            new PxButton("Hands", Glyph.Hand, ButtonKind.Glass, () => actions.SetHands(true)));
        var second = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var quit = new PxButton("Quit", null, ButtonKind.Glass, actions.Quit);
        DockPanel.SetDock(quit, Dock.Right);
        second.Children.Add(quit);
        second.Children.Add(Kit.Across(8,
            new PxButton("Settings…", Glyph.Settings, ButtonKind.Glass, actions.OpenSettings),
            new PxButton("Tour", null, ButtonKind.Glass, actions.OpenTour)));
        return Kit.Down(0, first, second);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
