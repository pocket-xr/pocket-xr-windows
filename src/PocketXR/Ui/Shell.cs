using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using PocketXR.Core;

namespace PocketXR.Ui;

/// <summary>
/// A Pocket XR window: always dark, its own 30 px strip with the window's name on the left and Windows' minimise and
/// close buttons on the right, the content under it. The same frame is used to draw a window to a picture file.
/// </summary>
public class PxWindow : Window
{
    public const double Strip = 30;

    /// <summary>Closing only hides the window; the tray icon brings it back as it was.</summary>
    public bool HideOnClose = true;

    public PxWindow(string title, UIElement body, UIElement? stripRight = null, bool resizable = false)
    {
        Title = title;
        Background = Theme.Page;
        Foreground = Theme.Ink;
        FontFamily = Theme.Text;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = Strip, GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0), UseAeroCaptionButtons = false,
        });
        Content = Frame(title, body, stripRight, minimise: () => WindowState = WindowState.Minimized, close: Close);
        SourceInitialized += (_, _) => Dark(new WindowInteropHelper(this).Handle);
        Closing += (_, e) =>
        {
            if (!HideOnClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>The strip and the body, as one element.</summary>
    public static Grid Frame(string title, UIElement body, UIElement? stripRight, Action? minimise, Action? close)
    {
        var root = new Grid { Background = Theme.Page };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Strip) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var strip = new Grid();
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock name = Kit.Text(title, 13, Theme.Ink, FontWeights.Medium, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(Theme.Inset + 4, 0, 8, 1);
        System.Windows.Automation.AutomationProperties.SetHeadingLevel(name, System.Windows.Automation.AutomationHeadingLevel.Level1);
        strip.Children.Add(name);
        if (stripRight is FrameworkElement right)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(right, 1);
            strip.Children.Add(right);
        }
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        caption.Children.Add(new CaptionButton(Glyph.Minimise, "Minimise", danger: false, minimise));
        caption.Children.Add(new CaptionButton(Glyph.Close, "Close", danger: true, close));
        WindowChrome.SetIsHitTestVisibleInChrome(caption, true);
        Grid.SetColumn(caption, 2);
        strip.Children.Add(caption);
        root.Children.Add(strip);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    /// <summary>Windows' caption button, in Pocket XR's colours: 46 by 30, red under the pointer for close.</summary>
    private sealed class CaptionButton : Press
    {
        private readonly bool danger;
        private readonly TextBlock icon;
        private static readonly SolidColorBrush Red = Theme.B(Color.FromRgb(0xC4, 0x2B, 0x1C));

        public CaptionButton(string glyph, string label, bool danger, Action? action)
        {
            this.danger = danger;
            Label = label;
            Width = 46;
            Height = Strip;
            Focusable = false;
            DimOnPress = false;
            icon = Kit.Icon(glyph, 10, Theme.Ink2);
            Child = icon;
            if (action != null) Click += action;
        }

        protected override void Restyle()
        {
            Background = Hover ? (danger ? Red : Theme.GlassHover) : Brushes.Transparent;
            icon.Foreground = Hover ? (danger ? Brushes.White : Theme.Ink) : Theme.Ink2;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Asks Windows for a dark frame, a hairline in Pocket XR's rim colour and (Windows 11) round corners.</summary>
    public static void Dark(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE: round
        int rim = 0x002E2B2A; // 0x00BBGGRR: the rim over the page colour
        DwmSetWindowAttribute(hwnd, 34, ref rim, sizeof(int)); // DWMWA_BORDER_COLOR
        int caption = 0x00100D0C; // the page colour
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }
}

/// <summary>Draws a window's content to a PNG file, with no window and no glasses (--render-ui).</summary>
public static class UiRender
{
    /// <summary>
    /// [view] is laid out [width] wide (and [height] high, or as high as it wants), inside the window frame with
    /// [title], and saved at [scale] pixels a point.
    /// </summary>
    public static void WindowPng(string path, string title, FrameworkElement view, double width, double? height = null,
        UIElement? stripRight = null, double scale = 2)
    {
        Grid frame = PxWindow.Frame(title, view, stripRight, null, null);
        frame.Width = width;
        if (height is double h) frame.Height = h;
        var rounded = new Border
        {
            Child = frame, CornerRadius = new CornerRadius(8), BorderBrush = Theme.B(Color.FromRgb(0x2A, 0x2B, 0x2E)),
            BorderThickness = new Thickness(1), Background = Theme.Page,
        };
        Png(path, rounded, scale, clipRadius: 8);
    }

    /// <summary>[view] alone, at the size it asks for.</summary>
    public static void Png(string path, FrameworkElement view, double scale = 2, double clipRadius = 0)
    {
        view.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(view, TextFormattingMode.Ideal);
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = view.DesiredSize;
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        if (clipRadius > 0) view.Clip = new RectangleGeometry(new Rect(size), clipRadius, clipRadius);
        // Let anything queued for after layout (bindings to sizes, deferred drawing) run before the picture is taken.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        int w = Math.Max(1, (int)Math.Ceiling(size.Width * scale)), hgt = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var bitmap = new RenderTargetBitmap(w, hgt, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream file = File.Create(path);
        encoder.Save(file);
        Log.P($"render-ui: {Path.GetFileName(path)} {w}x{hgt}");
    }
}
