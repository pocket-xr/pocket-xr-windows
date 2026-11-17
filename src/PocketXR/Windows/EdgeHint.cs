using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// A small lit tab on the edge of the PC's screen that says where the floating screens are: past that edge. Clicks go
/// through it, and it goes away for the session once the pointer has been across.
/// </summary>
public sealed class EdgeHint
{
    private Window? window;
    private Face? face;
    private DispatcherTimer? timer;
    private const double Width = 236, Height = 52;

    /// <summary>Shows the tab half way up the right (or left) edge of [screen].</summary>
    public void Show(DisplayInfo screen, bool right)
    {
        if (window == null)
        {
            face = new Face(right);
            window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                Topmost = true, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize, Content = face,
                Width = Width, Height = Height, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
                Title = "Pocket XR: your floating screens are past this edge",
            };
            window.SourceInitialized += (_, _) =>
            {
                IntPtr h = new WindowInteropHelper(window).Handle;
                // Clicks pass through; no activation; not in Alt+Tab.
                SetWindowLongPtr(h, -20, new IntPtr(GetWindowLongPtr(h, -20).ToInt64() | 0x20L | 0x08000000L | 0x80L));
            };
            window.Show();
            if (!Theme.ReduceMotion)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
                timer.Tick += (_, _) => face.InvalidateVisual();
                timer.Start();
            }
        }
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int w = (int)Math.Round(Width * scale), h2 = (int)Math.Round(Height * scale);
        int x = right ? screen.X + screen.Width - w : screen.X, y = screen.Y + (screen.Height - h2) / 2;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h2, 0x0010);
    }

    public void Close()
    {
        timer?.Stop();
        timer = null;
        window?.Close();
        window = null;
    }

    /// <summary>"Floating screens" and three chevrons lit one after the other toward the edge, every 1.4 s: a quiet
    /// "this way".</summary>
    public sealed class Face : FrameworkElement
    {
        private readonly bool right;
        private readonly long started = Environment.TickCount64;
        /// <summary>For a picture: the moment to draw, seconds.</summary>
        public double? At;

        public Face(bool right)
        {
            this.right = right;
            Width = EdgeHint.Width;
            Height = EdgeHint.Height;
            IsHitTestVisible = false;
            AutomationProperties.SetName(this, $"Your floating screens are past the {(right ? "right" : "left")} edge of this screen");
        }

        protected override void OnRender(DrawingContext dc)
        {
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip, t = At ?? (Environment.TickCount64 - started) / 1000.0;
            // A tab rounded on the side away from the edge.
            var shape = new StreamGeometry();
            const double r = 26;
            using (StreamGeometryContext c = shape.Open())
            {
                if (right)
                {
                    c.BeginFigure(new Point(Width, 0.5), true, true);
                    c.LineTo(new Point(r, 0.5), true, false);
                    c.ArcTo(new Point(r, Height - 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Counterclockwise, true, false);
                    c.LineTo(new Point(Width, Height - 0.5), true, false);
                }
                else
                {
                    c.BeginFigure(new Point(0, 0.5), true, true);
                    c.LineTo(new Point(Width - r, 0.5), true, false);
                    c.ArcTo(new Point(Width - r, Height - 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(0, Height - 0.5), true, false);
                }
            }
            shape.Freeze();
            dc.DrawGeometry(Theme.B(Look.Card.Alpha(0.94)), new Pen(Theme.B(Look.Accent.Alpha(0.55)), 1), shape);
            FormattedText words = Draw.Text("Floating screens", 14, Theme.Ink, FontWeights.SemiBold, dip);
            const double chevrons = 3 * 12;
            double total = words.Width + 10 + chevrons;
            double x = right ? Width - 12 - total : 12;
            double wordsX = right ? x : x + chevrons + 10, chevronX = right ? x + words.Width + 10 : x;
            dc.DrawText(words, new Point(wordsX, (Height - words.Height) / 2));
            for (int i = 0; i < 3; i++)
            {
                int order = right ? i : 2 - i;
                double phase = (t / 1.4 % 1) * 3 - order;
                double near = Math.Max(0, 1 - Math.Abs(phase - 0.5) * 1.4);
                double opacity = Theme.ReduceMotion || Theme.Static && At == null ? 1 : 0.3 + 0.7 * near;
                FormattedText chevron = Draw.Icon(right ? Glyph.ChevronRight : Glyph.ChevronLeft, 11, Theme.B(Look.Accent.Alpha(opacity)), dip);
                dc.DrawText(chevron, new Point(chevronX + i * 12, (Height - chevron.Height) / 2));
            }
        }
    }

    public static void Pictures(string folder)
    {
        UiRender.Png(Path.Combine(folder, "edge-hint-right.png"), new Face(true) { At = 0.35 });
        UiRender.Png(Path.Combine(folder, "edge-hint-left.png"), new Face(false) { At = 0.35 });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
