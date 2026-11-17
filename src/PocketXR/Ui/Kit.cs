using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PocketXR.Core;

namespace PocketXR.Ui;

// The parts every Pocket XR window is made of, in Pocket XR's look: cards with a hairline rim, rows, a switch, a
// slider, a segmented picker with a sliding highlight, buttons, chips and key caps. Light marks focus and what is
// held; nothing casts a shadow and nothing bounces. Built in code, so a window can also be drawn to a picture file.

/// <summary>
/// Keeps controls showing what is true: each control adds a "show the value now" action, and the window calls Sync()
/// whenever the settings or the app's state change.
/// </summary>
public sealed class Binder
{
    private readonly List<Action> syncs = new();
    private bool syncing;

    public void Add(Action sync) { syncs.Add(sync); sync(); }

    public void Sync()
    {
        if (syncing) return;
        syncing = true;
        try { foreach (Action s in syncs.ToArray()) s(); }
        finally { syncing = false; }
    }
}

/// <summary>Something that can be pressed: hover, press, keyboard focus, Space and Enter, and a name for screen readers.</summary>
public class Press : Border
{
    public event Action? Click;
    public bool Hover { get; private set; }
    public bool Pressed { get; private set; }
    /// <summary>Keyboard focus that arrived by the keyboard: the focus ring shows.</summary>
    public bool Ring { get; private set; }
    public string Label = "";
    /// <summary>For a switch: "On" or "Off"; null for a plain button.</summary>
    public Func<bool>? Toggled;
    public AutomationControlType Kind = AutomationControlType.Button;
    /// <summary>The whole face dims a little while pressed.</summary>
    public bool DimOnPress = true;

    public Press()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.4; };
    }

    /// <summary>Called when hover, press, focus or the value changed: set colours here.</summary>
    protected virtual void Restyle() { }

    public void Refresh() => Restyle();

    protected void Fire() => Click?.Invoke();

    protected override void OnMouseEnter(MouseEventArgs e) { Hover = true; Restyle(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(MouseEventArgs e) { Hover = false; Restyle(); base.OnMouseLeave(e); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Pressed = true;
        CaptureMouse();
        if (DimOnPress) Theme.Animate(this, OpacityProperty, 0.72, Look.Quick);
        Restyle();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Pressed) return;
        Pressed = false;
        ReleaseMouseCapture();
        if (DimOnPress) Theme.Animate(this, OpacityProperty, 1, Look.Quick);
        Restyle();
        if (IsMouseOver) Fire();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Fire();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Ring = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        Restyle();
        base.OnGotKeyboardFocus(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Ring = false;
        Restyle();
        base.OnLostKeyboardFocus(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PressPeer(this);

    private sealed class PressPeer : FrameworkElementAutomationPeer, IInvokeProvider, IToggleProvider
    {
        private readonly Press owner;
        public PressPeer(Press owner) : base(owner) { this.owner = owner; }
        protected override string GetNameCore() => owner.Label;
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.Kind;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface p) =>
            p == PatternInterface.Invoke && owner.Toggled == null ? this
            : p == PatternInterface.Toggle && owner.Toggled != null ? this
            : base.GetPattern(p);
        public void Invoke() => owner.Fire();
        public void Toggle() => owner.Fire();
        public ToggleState ToggleState => owner.Toggled?.Invoke() == true ? ToggleState.On : ToggleState.Off;
    }
}

public enum ButtonKind { Primary, Glass, Danger }

/// <summary>A capsule button: primary (accent), glass (neutral) or danger.</summary>
public sealed class PxButton : Press
{
    private readonly ButtonKind kind;
    private readonly SolidColorBrush fill = new();
    private readonly Border ring;

    public PxButton(string title, string? glyph = null, ButtonKind kind = ButtonKind.Glass, Action? action = null)
    {
        this.kind = kind;
        Label = title;
        Height = Theme.ButtonHeight;
        CornerRadius = new CornerRadius(Theme.ButtonHeight / 2);
        Background = fill;
        BorderThickness = new Thickness(1);
        Brush ink = kind == ButtonKind.Primary ? Theme.Page : kind == ButtonKind.Danger ? Theme.DangerInk : Theme.Ink;
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (glyph != null)
        {
            line.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = Theme.Icons, FontSize = 12, Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0),
            });
        }
        line.Children.Add(Kit.Text(title, Theme.ButtonSize, ink, FontWeights.Medium, wrap: false));
        ring = new Border
        {
            CornerRadius = new CornerRadius(Theme.ButtonHeight / 2 + 1), BorderThickness = new Thickness(2),
            BorderBrush = kind == ButtonKind.Primary ? Brushes.White : Theme.Accent, Margin = new Thickness(-2),
            Opacity = 0, IsHitTestVisible = false,
        };
        var grid = new Grid();
        grid.Children.Add(new Border { Padding = new Thickness(13, 0, 13, 1), Child = line });
        grid.Children.Add(ring);
        Child = grid;
        if (action != null) Click += action;
        Restyle();
    }

    protected override void Restyle()
    {
        Color c = kind switch
        {
            ButtonKind.Primary => Theme.C(Hover ? Look.Held : Look.Accent),
            ButtonKind.Danger => Color.FromArgb((byte)(Hover ? 71 : 36), Theme.DangerColor.R, Theme.DangerColor.G, Theme.DangerColor.B),
            _ => Theme.C(Hover ? Look.GlassHover : Look.Control.Alpha(0.55)),
        };
        Theme.AnimateColor(fill, c);
        BorderBrush = kind switch
        {
            ButtonKind.Primary => Theme.Clear,
            ButtonKind.Danger => Theme.B(Theme.DangerColor, 0.5),
            _ => Hover ? Theme.RimFocus : Theme.Rim,
        };
        ring.Opacity = Ring ? 1 : 0;
    }
}

/// <summary>A round button with only a glyph, 28 across; its name shows as a tooltip.</summary>
public sealed class IconButton : Press
{
    private readonly TextBlock icon;
    private readonly Border ring;

    public IconButton(string glyph, string label, Action? action = null, double size = 28, double glyphSize = 12)
    {
        Label = label;
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        ToolTip = Kit.Tip(label);
        icon = new TextBlock
        {
            Text = glyph, FontFamily = Theme.Icons, FontSize = glyphSize, Foreground = Theme.Ink2,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        ring = new Border
        {
            CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(2), BorderBrush = Theme.Accent,
            Opacity = 0, IsHitTestVisible = false,
        };
        var grid = new Grid();
        grid.Children.Add(icon);
        grid.Children.Add(ring);
        Child = grid;
        if (action != null) Click += action;
    }

    protected override void Restyle()
    {
        Background = Hover ? Theme.GlassHover : Brushes.Transparent;
        icon.Foreground = Hover ? Theme.Ink : Theme.Ink2;
        ring.Opacity = Ring ? 1 : 0;
    }
}

/// <summary>The switch: accent when on, the knob slides without overshoot.</summary>
public sealed class Toggle : Press
{
    private readonly Func<bool> get;
    private readonly SolidColorBrush track = new();
    private readonly TranslateTransform knob = new();
    private readonly Border ring;
    private bool shown;
    private bool first = true;

    public Toggle(Binder binder, string label, Func<bool> get, Action<bool> set)
    {
        this.get = get;
        Label = label;
        Toggled = get;
        DimOnPress = false;
        Width = 44;
        Height = 28;
        var grid = new Grid();
        grid.Children.Add(new Border
        {
            Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Background = track,
            BorderBrush = Theme.Rim, BorderThickness = new Thickness(1),
        });
        grid.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, RenderTransform = knob });
        ring = new Border
        {
            Width = 42, Height = 26, CornerRadius = new CornerRadius(13), BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false,
        };
        grid.Children.Add(ring);
        Child = grid;
        Click += () => set(!get());
        binder.Add(Restyle);
    }

    protected override void Restyle()
    {
        bool on = get();
        Color c = on ? Theme.C(Hover ? Look.Held : Look.Accent) : Theme.C(Hover ? Look.GlassHover : Look.Control);
        if (first) { track.Color = c; knob.X = on ? 8 : -8; first = false; shown = on; }
        else
        {
            Theme.AnimateColor(track, c);
            if (on != shown)
            {
                shown = on;
                Theme.Animate(knob, TranslateTransform.XProperty, on ? 8 : -8, 0.18);
            }
        }
        ring.Opacity = Ring ? 1 : 0;
    }
}

/// <summary>The slider: a thin track, accent up to the thumb, a white thumb that takes an accent glow while it is held.</summary>
public sealed class PxSlider : FrameworkElement
{
    private readonly Func<double> get;
    private readonly Action<double> set;
    private readonly double min, max, step;
    private readonly Func<double, string> valueText;
    private readonly string label;
    private bool dragging, hover, ring;
    private const double Thumb = 16;
    private static readonly SolidColorBrush Rail = Theme.White(0.14);
    private static readonly SolidColorBrush Glow = Theme.B(Look.Accent.Alpha(0.30));
    private static readonly SolidColorBrush GlowSoft = Theme.B(Look.Accent.Alpha(0.12));

    public PxSlider(Binder binder, string label, Func<double> get, Action<double> set, double min, double max, double step,
        Func<double, string> valueText)
    {
        this.get = get; this.set = set; this.min = min; this.max = max; this.step = step;
        this.valueText = valueText; this.label = label;
        Height = 28;
        MinWidth = 80;
        Focusable = true;
        FocusVisualStyle = null;
        binder.Add(InvalidateVisual);
    }

    private double Fraction
    {
        get
        {
            double span = max - min;
            return span > 0 ? Math.Clamp((get() - min) / span, 0, 1) : 0;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, mid = ActualHeight / 2;
        double travel = Math.Max(w - Thumb, 1);
        double x = Fraction * travel;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        dc.DrawRoundedRectangle(Rail, null, new Rect(0, mid - 2, w, 4), 2, 2);
        dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(0, mid - 2, x + Thumb / 2, 4), 2, 2);
        var centre = new Point(x + Thumb / 2, mid);
        if (dragging || ring) dc.DrawEllipse(Glow, null, centre, 14, 14);
        else if (hover) dc.DrawEllipse(GlowSoft, null, centre, 14, 14);
        dc.DrawEllipse(Brushes.White, null, centre, Thumb / 2, Thumb / 2);
    }

    private void SetFromX(double px)
    {
        double travel = Math.Max(ActualWidth - Thumb, 1);
        double f = Math.Clamp((px - Thumb / 2) / travel, 0, 1);
        Commit(min + f * (max - min));
    }

    private void Commit(double raw)
    {
        double steps = Math.Round((raw - min) / step, MidpointRounding.AwayFromZero);
        double next = Math.Clamp(min + steps * step, min, max);
        if (next != get()) set(next);
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { hover = false; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        dragging = true;
        CaptureMouse();
        Focus();
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) SetFromX(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Down) { Commit(get() - step); e.Handled = true; }
        else if (e.Key is Key.Right or Key.Up) { Commit(get() + step); e.Handled = true; }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        ring = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { ring = false; InvalidateVisual(); }

    protected override AutomationPeer OnCreateAutomationPeer() => new SliderPeer(this);

    private sealed class SliderPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        private readonly PxSlider s;
        public SliderPeer(PxSlider s) : base(s) { this.s = s; }
        protected override string GetNameCore() => s.label;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetItemStatusCore() => s.valueText(s.get());
        public override object? GetPattern(PatternInterface p) => p == PatternInterface.RangeValue ? this : base.GetPattern(p);
        public void SetValue(double value) => s.Commit(value);
        public double Value => s.get();
        public bool IsReadOnly => false;
        public double Maximum => s.max;
        public double Minimum => s.min;
        public double LargeChange => s.step * 5;
        public double SmallChange => s.step;
    }
}

/// <summary>A segmented picker: one of a few, with a highlight that slides to the choice.</summary>
public sealed class Segmented : Border
{
    private readonly SegPanel panel;

    public Segmented(Binder binder, string label, IReadOnlyList<string> titles, Func<int> get, Action<int> set)
    {
        CornerRadius = new CornerRadius(Theme.ControlRadius);
        Background = Theme.Pad;
        BorderBrush = Theme.Rim;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        panel = new SegPanel(titles, get, set, label);
        Child = panel;
        binder.Add(panel.Sync);
    }

    /// <summary>Smaller words with less room around them, for a narrow window with long names.</summary>
    public Segmented SmallText()
    {
        panel.Compact();
        return this;
    }

    /// <summary>The segments share the picker's whole width equally.</summary>
    public Segmented Stretch()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Equal = true;
        return this;
    }

    private sealed class Segment : Press
    {
        public readonly TextBlock Text;
        public bool Selected;
        private readonly Border hover;
        private readonly Border ring;

        public Segment(string title)
        {
            Label = title;
            Kind = AutomationControlType.RadioButton;
            DimOnPress = false;
            MinWidth = 36;
            Height = 28;
            Text = Kit.Text(title, 13, Theme.Ink, FontWeights.Normal, wrap: false);
            Text.HorizontalAlignment = HorizontalAlignment.Center;
            Text.VerticalAlignment = VerticalAlignment.Center;
            Text.Margin = new Thickness(12, 0, 12, 1);
            hover = new Border { CornerRadius = new CornerRadius(6), Background = Theme.GlassHover, Opacity = 0 };
            ring = new Border { CornerRadius = new CornerRadius(6), BorderBrush = Theme.Held, BorderThickness = new Thickness(2), Opacity = 0 };
            var g = new Grid();
            g.Children.Add(hover);
            g.Children.Add(Text);
            g.Children.Add(ring);
            Child = g;
        }

        protected override void Restyle()
        {
            hover.Opacity = Hover && !Selected ? 1 : 0;
            ring.Opacity = Ring ? 1 : 0;
            Text.Foreground = Selected ? Theme.Page : Theme.Ink;
            Text.FontWeight = Selected ? FontWeights.Medium : FontWeights.Normal;
        }
    }

    private sealed class SegPanel : Panel
    {
        private readonly Border pick;
        private readonly TranslateTransform slide = new();
        private readonly List<Segment> segments = new();
        private readonly Func<int> get;
        private int shown = -1;
        private readonly double[] lefts;
        public bool Equal;
        private const double Gap = 2, Edge = 1;

        public SegPanel(IReadOnlyList<string> titles, Func<int> get, Action<int> set, string label)
        {
            this.get = get;
            AutomationProperties.SetName(this, label);
            pick = new Border { CornerRadius = new CornerRadius(6), Background = Theme.Accent, RenderTransform = slide };
            Children.Add(pick);
            lefts = new double[titles.Count];
            for (int i = 0; i < titles.Count; i++)
            {
                int index = i;
                var s = new Segment(titles[i]);
                s.Click += () => { if (get() != index) set(index); };
                segments.Add(s);
                Children.Add(s);
            }
        }

        public void Compact()
        {
            foreach (Segment s in segments)
            {
                s.Text.FontSize = 12.5;
                s.Text.Margin = new Thickness(7, 0, 7, 1);
            }
        }

        public void Sync()
        {
            int now = get();
            for (int i = 0; i < segments.Count; i++)
            {
                segments[i].Selected = i == now;
                segments[i].Refresh();
            }
            if (now != shown && shown >= 0 && now >= 0 && now < lefts.Length && IsLoaded)
            {
                // The highlight is laid out at its new place; it starts from the old one and slides home.
                double from = lefts[shown] - lefts[now];
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.X = from;
                Theme.Animate(slide, TranslateTransform.XProperty, 0, 0.22);
            }
            shown = now;
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = Edge, h = 0, widest = 0;
            foreach (Segment s in segments)
            {
                s.Measure(new Size(double.PositiveInfinity, available.Height));
                w += s.DesiredSize.Width + Gap;
                widest = Math.Max(widest, s.DesiredSize.Width);
                h = Math.Max(h, s.DesiredSize.Height);
            }
            if (Equal && !double.IsInfinity(available.Width)) w = available.Width;
            else w += Edge - Gap;
            pick.Measure(new Size(widest, h));
            return new Size(w, h + 2 * Edge);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double x = Edge;
            // Filling the width: every segment gets what its words need, and they share what is left equally (so a
            // long name is never cut while a short one has room to spare). With too little room they all give way.
            double room = final.Width - 2 * Edge - Gap * (segments.Count - 1), wanted = 0;
            foreach (Segment s in segments) wanted += s.DesiredSize.Width;
            double spare = (room - wanted) / Math.Max(segments.Count, 1), squeeze = wanted > 0 ? Math.Min(room / wanted, 1) : 1;
            int now = get();
            for (int i = 0; i < segments.Count; i++)
            {
                double w = !Equal ? segments[i].DesiredSize.Width
                    : spare >= 0 ? segments[i].DesiredSize.Width + spare : segments[i].DesiredSize.Width * squeeze;
                var r = new Rect(x, Edge, Math.Max(w, 0), Math.Max(final.Height - 2 * Edge, 0));
                segments[i].Arrange(r);
                lefts[i] = x;
                if (i == now) pick.Arrange(r);
                x += w + Gap;
            }
            if (now < 0 || now >= segments.Count) pick.Arrange(new Rect(0, 0, 0, 0));
            return final;
        }
    }
}

/// <summary>A round colour to pick, with a ring when it is the one chosen.</summary>
public sealed class Swatch : Press
{
    private readonly Ellipse ringShape;
    private readonly Func<bool> selected;

    public Swatch(Binder binder, Color color, string name, Func<bool> selected, Action pick)
    {
        this.selected = selected;
        Label = name;
        Kind = AutomationControlType.RadioButton;
        Width = 28;
        Height = 28;
        ToolTip = Kit.Tip(name);
        ringShape = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2, Stroke = Theme.White(0.9), Opacity = 0 };
        var g = new Grid();
        g.Children.Add(ringShape);
        g.Children.Add(new Ellipse { Width = 18, Height = 18, Fill = Theme.B(color) });
        Child = g;
        Click += pick;
        binder.Add(Restyle);
    }

    protected override void Restyle()
    {
        ringShape.Stroke = Ring ? Theme.Accent : Theme.White(0.9);
        ringShape.Opacity = selected() || Ring ? 1 : Hover ? 0.35 : 0;
    }
}

/// <summary>Text for pictures drawn by hand (OnRender).</summary>
public static class Draw
{
    private static readonly Typeface Regular = new(Theme.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Medium = new(Theme.Text, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface IconFace = new(Theme.Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static FormattedText Text(string text, double size, Brush ink, FontWeight weight, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, weight == FontWeights.Normal ? Regular : weight == FontWeights.Medium ? Medium
            : new Typeface(Theme.Text, FontStyles.Normal, weight, FontStretches.Normal), size, ink, pixelsPerDip);

    public static FormattedText Icon(string glyph, double size, Brush ink, double pixelsPerDip) =>
        new(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, size, ink, pixelsPerDip);
}

/// <summary>Builders for the plain parts: text, cards, rows, chips, key caps.</summary>
public static class Kit
{
    public static TextBlock Text(string text, double size, Brush ink, FontWeight? weight = null, bool wrap = true,
        bool digits = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, Foreground = ink, FontWeight = weight ?? FontWeights.Normal,
            FontFamily = size >= 19 ? Theme.Display : size < 12 ? Theme.Small : Theme.Text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (digits) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    public static TextBlock Title(string text) => Text(text, Theme.TitleSize, Theme.Ink, FontWeights.Medium);
    public static TextBlock Heading(string text) => Text(text, Theme.HeadingSize, Theme.Ink, FontWeights.Medium);
    public static TextBlock Body(string text) => Text(text, Theme.BodySize, Theme.Ink2);
    public static TextBlock Line(string text) => Text(text, Theme.LineSize, Theme.Ink);
    public static TextBlock Small(string text, bool warn = false) => Text(text, Theme.NoteSize, warn ? Theme.Warn : Theme.Ink2);

    public static TextBlock Icon(string glyph, double size, Brush ink) => new()
    {
        Text = glyph, FontFamily = Theme.Icons, FontSize = size, Foreground = ink,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>A tooltip in Pocket XR's look.</summary>
    public static ToolTip Tip(string text) => new() { Content = Text(text, Theme.NoteSize, Theme.Ink, wrap: false) };

    /// <summary>A group of rows on the card colour with a hairline rim, an optional heading above it.</summary>
    public static StackPanel Card(string? title, params UIElement?[] rows)
    {
        var outer = new StackPanel();
        if (title != null)
        {
            TextBlock head = Text(title, Theme.NoteSize, Theme.Ink2, FontWeights.Medium);
            head.Margin = new Thickness(4, 0, 0, 8);
            AutomationProperties.SetHeadingLevel(head, AutomationHeadingLevel.Level2);
            outer.Children.Add(head);
        }
        var inner = new StackPanel();
        bool any = false;
        foreach (UIElement? row in rows)
        {
            if (row == null) continue;
            if (any && row is not DividerMark && inner.Children[^1] is not DividerMark) { }
            inner.Children.Add(row);
            any = true;
        }
        outer.Children.Add(new Border
        {
            Background = Theme.Card, BorderBrush = Theme.Rim, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius), Child = inner, SnapsToDevicePixels = true,
        });
        return outer;
    }

    /// <summary>A bare card: one child on the card colour with the rim.</summary>
    public static Border Plate(UIElement child, double padding = 0) => new()
    {
        Background = Theme.Card, BorderBrush = Theme.Rim, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Theme.CardRadius), Child = child, Padding = new Thickness(padding),
        SnapsToDevicePixels = true,
    };

    public sealed class DividerMark : Border { }

    /// <summary>The hairline between two rows of a card.</summary>
    public static UIElement Divider() => new DividerMark
    {
        Height = 1, Background = Theme.Hairline, Margin = new Thickness(Theme.Inset, 0, 0, 0), SnapsToDevicePixels = true,
    };

    /// <summary>One row: what it is on the left (with an optional line under it), its control on the right.</summary>
    public static Grid Row(string title, string? note = null, UIElement? trailing = null)
    {
        var g = new Grid { MinHeight = Theme.RowHeight, Margin = new Thickness(Theme.Inset, note == null ? 6 : 10, Theme.Inset, note == null ? 6 : 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.MinHeight = Theme.RowHeight - (note == null ? 12 : 20);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Line(title));
        if (note != null)
        {
            TextBlock n = Small(note);
            n.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(n);
        }
        g.Children.Add(words);
        if (trailing != null)
        {
            if (trailing is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(12, 0, 0, 0);
            }
            Grid.SetColumn(trailing, 1);
            g.Children.Add(trailing);
        }
        return g;
    }

    /// <summary>A row with a switch.</summary>
    public static Grid ToggleRow(Binder binder, string title, string? note, Func<bool> get, Action<bool> set) =>
        Row(title, note, new Toggle(binder, title, get, set));

    /// <summary>A row whose control is too wide to sit beside its name: the name above, the control under it.</summary>
    public static StackPanel StackedRow(string title, UIElement control)
    {
        var s = new StackPanel { Margin = new Thickness(Theme.Inset, 10, Theme.Inset, 10) };
        TextBlock t = Line(title);
        t.Margin = new Thickness(0, 0, 0, 8);
        s.Children.Add(t);
        s.Children.Add(control);
        return s;
    }

    /// <summary>A line of plain words inside a card.</summary>
    public static TextBlock Note(string text, bool warn = false)
    {
        TextBlock t = Small(text, warn);
        t.Margin = new Thickness(Theme.Inset, 10, Theme.Inset, 10);
        return t;
    }

    /// <summary>A row with a slider: its name, the slider, and the value in words on the right.</summary>
    public static Grid SliderRow(Binder binder, string title, Func<double> get, Action<double> set, double min, double max,
        double step, Func<double, string> valueText, double titleWidth = 150, double valueWidth = 64)
    {
        var g = new Grid { MinHeight = Theme.RowHeight, Margin = new Thickness(Theme.Inset, 0, Theme.Inset, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(titleWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueWidth) });
        TextBlock name = Line(title);
        name.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(name);
        var slider = new PxSlider(binder, title, get, set, min, max, step, valueText) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        TextBlock value = Text("", Theme.LineSize, Theme.Ink2, wrap: false, digits: true);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        g.Children.Add(value);
        binder.Add(() => value.Text = valueText(get()));
        return g;
    }

    /// <summary>A small capsule saying a state: lit when it is on.</summary>
    public static Border Chip(string text, bool lit = false)
    {
        var b = new Border { Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 0, 7, 0) };
        TextBlock t = Text(text, Theme.ChipSize, Theme.Ink3, wrap: false);
        t.VerticalAlignment = VerticalAlignment.Center;
        b.Child = t;
        SetChip(b, lit);
        return b;
    }

    public static void SetChip(Border chip, bool lit)
    {
        chip.Background = lit ? Theme.B(Look.Accent.Alpha(0.16)) : Theme.Clear;
        chip.BorderBrush = lit ? Theme.B(Look.Accent.Alpha(0.5)) : Theme.Rim;
        ((TextBlock)chip.Child).Foreground = lit ? Theme.Held : Theme.Ink3;
    }

    /// <summary>A status dot: accent with a soft halo when lit, grey when not.</summary>
    public static Grid Dot(bool lit)
    {
        var g = new Grid { Width = 14, Height = 14 };
        g.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = Theme.B(Look.Accent.Alpha(0.25)) });
        g.Children.Add(new Ellipse { Width = 7, Height = 7 });
        SetDot(g, lit);
        return g;
    }

    public static void SetDot(Grid dot, bool lit)
    {
        dot.Children[0].Opacity = lit ? 1 : 0;
        ((Ellipse)dot.Children[1]).Fill = lit ? Theme.Accent : Theme.Ink3;
    }

    /// <summary>One key of the keyboard.</summary>
    public static Border KeyCap(string text)
    {
        TextBlock t = Text(text, Theme.NoteSize, Theme.Ink, FontWeights.Medium, wrap: false);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            MinWidth = 26, Height = 24, CornerRadius = new CornerRadius(6), Background = Theme.Glass,
            BorderBrush = Theme.Rim, BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 1), Child = t,
        };
    }

    /// <summary>A shortcut as key caps: "Ctrl+Alt+R" becomes three caps.</summary>
    public static StackPanel Keys(string keys)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(s, keys.Replace("+", " "));
        foreach (string part in keys.Split('+'))
        {
            Border cap = KeyCap(part);
            cap.Margin = new Thickness(s.Children.Count == 0 ? 0 : 4, 0, 0, 0);
            s.Children.Add(cap);
        }
        return s;
    }

    /// <summary>A reading: a small name over a number.</summary>
    public static StackPanel Reading(string title, out TextBlock value)
    {
        var s = new StackPanel { MinWidth = 84 };
        s.Children.Add(Text(title, Theme.ChipSize, Theme.Ink3, wrap: false));
        value = Text("", 16, Theme.Ink, FontWeights.Medium, wrap: false, digits: true);
        value.Margin = new Thickness(0, 2, 0, 0);
        s.Children.Add(value);
        return s;
    }

    /// <summary>Children one after another across, [gap] apart.</summary>
    public static StackPanel Across(double gap, params UIElement?[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement? c in children)
        {
            if (c == null) continue;
            if (s.Children.Count > 0 && c is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left + gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            s.Children.Add(c);
        }
        return s;
    }

    /// <summary>Children one under another, [gap] apart.</summary>
    public static StackPanel Down(double gap, params UIElement?[] children)
    {
        var s = new StackPanel();
        foreach (UIElement? c in children)
        {
            if (c == null) continue;
            if (s.Children.Count > 0 && c is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
            s.Children.Add(c);
        }
        return s;
    }

    public static string Number(double v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);
}
