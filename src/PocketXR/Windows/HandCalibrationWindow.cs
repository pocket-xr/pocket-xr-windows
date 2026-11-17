using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The hand aim's calibration: five dots, one at a time, over the whole of a floating screen. The person holds a
// hand over each dot and pinches. Dark and plain, so the dot is the only light.

/// <summary>The calibration's picture: every size is a share of the screen's height, so it looks the same on any screen.</summary>
public sealed class HandCalibrationView : Grid
{
    private readonly Face face;
    private readonly CancelButton cancel;
    private readonly Ellipse breath;
    private readonly ScaleTransform breathScale = new(1, 1);

    /// <summary>The dot being asked for, 0 for the first.</summary>
    public int Step { get; private set; }

    private static int Total => HandCalibrationRun.Dots.Length;

    public HandCalibrationView(int step, Action onCancel)
    {
        Step = step;
        Background = Theme.Page;
        ClipToBounds = true;
        face = new Face(this);
        Children.Add(face);
        // The ring round the lit dot breathes slowly, so the eye finds it at once.
        breath = new Ellipse
        {
            Stroke = Theme.B(Look.Accent.Alpha(0.35)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = breathScale, RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false,
        };
        Children.Add(breath);
        cancel = new CancelButton(onCancel) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Children.Add(cancel);
        Loaded += (_, _) =>
        {
            if (Theme.Static || Theme.ReduceMotion) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var grow = new DoubleAnimation(1, 1.12, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            breathScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            breathScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            breath.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.5, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        };
    }

    /// <summary>Lights dot [step], 0 for the first.</summary>
    public void Show(int step)
    {
        Step = step;
        Place();
    }

    /// <summary>1 at 1080 high.</summary>
    private static double Unit(Size size) => Math.Max(Math.Min(size.Width / 1920, size.Height / 1080), 0.4);

    private Size Area => new(double.IsNaN(Width) ? ActualWidth : Width, double.IsNaN(Height) ? ActualHeight : Height);

    protected override Size MeasureOverride(Size constraint)
    {
        Place(double.IsInfinity(constraint.Width) || double.IsInfinity(constraint.Height) ? Area : constraint);
        return base.MeasureOverride(constraint);
    }

    private void Place() => Place(Area);

    private void Place(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        double unit = Unit(size);
        (double u, double v) = HandCalibrationRun.Dots[Math.Min(Math.Max(Step, 0), Total - 1)];
        double ring = 92 * unit;
        breath.Width = ring;
        breath.Height = ring;
        breath.StrokeThickness = 1.5 * unit;
        breath.Margin = new Thickness(u * size.Width - ring / 2, v * size.Height - ring / 2, 0, 0);
        cancel.Scale(unit);
        cancel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // The button and the line under it are one group, its middle at 86 % of the height.
        cancel.Margin = new Thickness(size.Width / 2 - cancel.DesiredSize.Width / 2, size.Height * 0.86 - 39.5 * unit, 0, 0);
        face.InvalidateVisual();
    }

    /// <summary>Everything that is only drawn: the pool of light, the dots, the words.</summary>
    private sealed class Face : FrameworkElement
    {
        private readonly HandCalibrationView owner;

        public Face(HandCalibrationView owner)
        {
            this.owner = owner;
            AutomationProperties.SetName(this, "Point at the dot and pinch");
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double unit = Unit(new Size(w, h)), dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int total = Total, step = owner.Step;
            dc.DrawRectangle(Theme.Page, null, new Rect(0, 0, w, h));

            // A soft pool of the accent's light behind the lit dot, so the eye finds it at once.
            (double U, double V) lit = HandCalibrationRun.Dots[Math.Min(Math.Max(step, 0), total - 1)];
            var centre = new Point(lit.U * w, lit.V * h);
            var pool = new RadialGradientBrush(Theme.C(Look.Accent.Alpha(0.16)), Theme.C(Look.Accent.Alpha(0)))
            {
                MappingMode = BrushMappingMode.Absolute, Center = centre, GradientOrigin = centre, RadiusX = 300 * unit, RadiusY = 300 * unit,
            };
            dc.DrawRectangle(pool, null, new Rect(0, 0, w, h));

            for (int i = 0; i < total; i++)
            {
                var at = new Point(HandCalibrationRun.Dots[i].U * w, HandCalibrationRun.Dots[i].V * h);
                if (i == step)
                {
                    dc.DrawEllipse(null, new Pen(Theme.B(Look.Held.Alpha(0.8)), 2 * unit), at, 27 * unit, 27 * unit);
                    dc.DrawEllipse(Theme.Accent, null, at, 13 * unit, 13 * unit);
                    dc.DrawEllipse(Brushes.White, null, at, 3.5 * unit, 3.5 * unit);
                }
                else if (i < step)
                {
                    // Done: a quiet tick where the dot was.
                    dc.DrawEllipse(Theme.White(0.06), null, at, 17 * unit, 17 * unit);
                    FormattedText tick = Draw.Icon(Glyph.Check, 15 * unit, Theme.Ink3, dip);
                    dc.DrawText(tick, new Point(at.X - tick.Width / 2, at.Y - tick.Height / 2));
                }
                else
                {
                    // Still to come: a faint ring, so the next place to look is no surprise.
                    dc.DrawEllipse(null, new Pen(Theme.White(0.16), 1.5 * unit), at, 10.25 * unit, 10.25 * unit);
                }
            }

            // The words: between the upper dots and above the middle one, clear of all five.
            FormattedText title = Draw.Text("Point at the dot and pinch", 40 * unit, Theme.Ink, FontWeights.Medium, dip);
            FormattedText count = Draw.Text($"{Math.Min(step + 1, total).ToString(CultureInfo.InvariantCulture)} of {total}", 20 * unit, Theme.Ink2, FontWeights.Medium, dip);
            FormattedText how = Draw.Text(
                "Hold your hand so it covers the dot, then touch thumb and first finger together. Go by your hand, not by the pointer: the pointer may be off until this is done.",
                20 * unit, Theme.Ink2, FontWeights.Normal, dip);
            how.MaxTextWidth = w * 0.44;
            how.TextAlignment = TextAlignment.Center;
            how.LineHeight = 28 * unit;
            double barsHigh = 6 * unit, rowHigh = Math.Max(count.Height, barsHigh);
            double whole = title.Height + 14 * unit + rowHigh + 20 * unit + how.Height;
            double y = h * 0.29 - whole / 2;
            dc.DrawText(title, new Point(w / 2 - title.Width / 2, y));
            y += title.Height + 14 * unit;

            // Progress: a bar for each dot, the lit one longer, then "3 of 5".
            double barsWide = 0;
            for (int i = 0; i < total; i++) barsWide += (i == step ? 30 : 14) * unit + (i > 0 ? 7 * unit : 0);
            double x = w / 2 - (barsWide + 10 * unit + count.Width) / 2, mid = y + rowHigh / 2;
            for (int i = 0; i < total; i++)
            {
                double wide = (i == step ? 30 : 14) * unit;
                Brush fill = i < step ? Theme.B(Look.Accent.Alpha(0.45)) : i == step ? Theme.Accent : Theme.White(0.16);
                dc.DrawRoundedRectangle(fill, null, new Rect(x, mid - barsHigh / 2, wide, barsHigh), barsHigh / 2, barsHigh / 2);
                x += wide + 7 * unit;
            }
            dc.DrawText(count, new Point(x + 3 * unit, mid - count.Height / 2));
            y += rowHigh + 20 * unit;
            dc.DrawText(how, new Point(w / 2 - how.MaxTextWidth / 2, y));

            FormattedText hint = Draw.Text("Click Cancel with the mouse. It also stops by itself after 30 seconds without a pinch.",
                16 * unit, Theme.Ink3, FontWeights.Normal, dip);
            dc.DrawText(hint, new Point(w / 2 - hint.Width / 2, h * 0.86 + 30 * unit - hint.Height / 2));
        }
    }

    /// <summary>"Cancel", as large as the picture asks.</summary>
    private sealed class CancelButton : Press
    {
        private readonly TextBlock text;

        public CancelButton(Action action)
        {
            Label = "Cancel";
            BorderThickness = new Thickness(1);
            text = Kit.Text("Cancel", 20, Theme.Ink, FontWeights.Medium, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            Child = text;
            Click += action;
            Scale(1);
            Restyle();
        }

        public void Scale(double unit)
        {
            Height = 48 * unit;
            CornerRadius = new CornerRadius(24 * unit);
            text.FontSize = 20 * unit;
            text.Margin = new Thickness(30 * unit, 0, 30 * unit, 1);
        }

        protected override void Restyle()
        {
            Background = Hover ? Theme.GlassHover : Theme.ControlSoft;
            BorderBrush = Ring ? Theme.Accent : Hover ? Theme.RimFocus : Theme.Rim;
        }
    }
}

/// <summary>
/// A borderless window over the whole of one floating screen, in front of everything on it. It never takes the
/// keyboard, and its Cancel can be clicked without making Pocket XR the front app. Not shown on a screen yet: only
/// its content has been drawn to a picture.
/// </summary>
public sealed class HandCalibrationWindow
{
    private readonly Window window;
    private readonly HandCalibrationView view;

    /// <summary>[screen] is the floating screen's place on the desktop, in WPF's units.</summary>
    public HandCalibrationWindow(Rect screen, Action onCancel)
    {
        view = new HandCalibrationView(0, onCancel);
        window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            ShowActivated = false, Background = Theme.Page, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = screen.Left, Top = screen.Top, Width = screen.Width, Height = screen.Height, Content = view,
            Title = "Pocket XR hand calibration", UseLayoutRounding = true,
        };
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtr(handle, ExStyle, new IntPtr(GetWindowLongPtr(handle, ExStyle).ToInt64() | NoActivate));
        };
    }

    /// <summary>Shows the window (the first time) with dot [step] lit, 0 for the first.</summary>
    public void Show(int step)
    {
        view.Show(step);
        if (window.IsVisible) return;
        view.Opacity = 0;
        window.Show();
        Theme.Animate(view, UIElement.OpacityProperty, 1);
    }

    public void Close() => window.Hide();

    private const int ExStyle = -20;
    private const long NoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
