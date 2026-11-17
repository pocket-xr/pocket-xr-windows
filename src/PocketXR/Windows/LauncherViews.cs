using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PocketXR.Core;
using PocketXR.Platform;
using PocketXR.Ui;

namespace PocketXR.Windows;

// The launcher's looks: a glass panel in the middle of a dark sheet, in Pocket XR's "Light on glass". It is read in
// XR glasses, so the type is larger than in the PC's own windows: no app's name is under 16 and the clock is large.
// Every part has a fixed size and place on the panel (LauncherMetrics), and the whole panel is scaled to fit the
// floating screen it is on.

public static class LauncherMetrics
{
    public const double PanelWidth = 1200, Radius = 30, Side = 48, Inner = 1104;
    public const double Top = 32, Strip = 60, GapStrip = 22, Buttons = 88, GapButtons = 22, Search = 48, GapSearch = 22;
    /// <summary>One app: its icon and its name under it, on one line or two.</summary>
    public const double Cell = 132, Icon = 72, NameSize = 16, NameLine = 20;
    public const int Columns = 6, Rows = 3, SearchRows = 4;
    public const double PinGap = 18, RowGap = 6, SearchRowGap = 6, MaxPinCell = 156;
    /// <summary>The pinned row and the grid, or a search's results.</summary>
    public const double Apps = Cell + PinGap + Rows * Cell + (Rows - 1) * RowGap;
    public const double GapApps = 10, Dots = 16, GapDots = 10, Hint = 22, Bottom = 22;
    /// <summary>The widest the light behind a hovered or selected app gets.</summary>
    public const double Highlight = 168;
    public const double ButtonCell = 136, ButtonCircle = 56;
    public const double MenuWidth = 264, MenuRow = 46;

    public const double StripTop = Top;
    public const double ButtonsTop = StripTop + Strip + GapStrip;
    public const double SearchTop = ButtonsTop + Buttons + GapButtons;
    public const double AppsTop = SearchTop + Search + GapSearch;
    public const double DotsTop = AppsTop + Apps + GapApps;
    public const double HintTop = DotsTop + Dots + GapDots;
    public const double PanelHeight = HintTop + Hint + Bottom;
    public const double Column = Inner / Columns;

    public static double PinCell(int count) => count > 0 ? Math.Min(MaxPinCell, Math.Floor(Inner / count)) : MaxPinCell;

    /// <summary>
    /// How much the panel is scaled on a screen [root] points across: 1 on 1920 x 1080; a smaller screen gets the
    /// whole panel scaled down to fit, a larger one scaled up with it.
    /// </summary>
    public static double Fit(Size root)
    {
        double w = (root.Width - 64) / PanelWidth, h = (root.Height - 48) / PanelHeight;
        double most = Math.Max(1, Math.Min(root.Width / 1920, root.Height / 1080));
        return Math.Max(Math.Min(most, Math.Min(w, h)), 0.3);
    }
}

/// <summary>The dark sheet, the panel in its middle, and the right-click menu over both.</summary>
public sealed class LauncherView : Grid
{
    public readonly LauncherModel Model;
    private readonly Grid panelHost;
    private readonly ScaleTransform fit = new(1, 1), grow = new(1, 1);
    private readonly Canvas panel;
    private readonly StripView strip;
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly SearchField search;
    private readonly Canvas apps = new() { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Apps };
    private readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid hint = new();
    private readonly Border catcher;
    private readonly Canvas menuLayer = new();
    private readonly List<AppCell> cells = new();
    private int shape = -1, builtPage = -1, builtPages = -1, dotsPage = -1;
    private bool builtSteady, builtButtons;
    private string builtHint = "\n";
    private LauncherMenu? builtMenu;

    private static readonly Brush Sheet = MakeSheet();
    private static readonly Brush GlassFill = Frozen(new LinearGradientBrush(Theme.C(Look.GlassTop.Alpha(0.96)), Theme.C(Look.Page), 90));
    private static readonly Brush GlassRim = Frozen(new LinearGradientBrush(Color.FromArgb(87, 255, 255, 255), Color.FromArgb(26, 255, 255, 255), 90));

    public LauncherView(LauncherModel model)
    {
        Model = model;
        Background = Sheet;
        Focusable = true;
        FocusVisualStyle = null;
        UseLayoutRounding = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(this, "Launcher");

        panel = new Canvas { Width = LauncherMetrics.PanelWidth, Height = LauncherMetrics.PanelHeight };
        var glass = new Border
        {
            Width = LauncherMetrics.PanelWidth, Height = LauncherMetrics.PanelHeight, CornerRadius = new CornerRadius(LauncherMetrics.Radius),
            Background = GlassFill, BorderBrush = GlassRim, BorderThickness = new Thickness(1),
        };
        panel.Children.Add(glass);

        strip = new StripView(model) { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Strip };
        Put(strip, LauncherMetrics.Side, LauncherMetrics.StripTop);
        var buttonsRow = new Grid { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Buttons };
        buttonsRow.Children.Add(buttons);
        Put(buttonsRow, LauncherMetrics.Side, LauncherMetrics.ButtonsTop);
        search = new SearchField();
        Put(search, (LauncherMetrics.PanelWidth - SearchField.FieldWidth) / 2, LauncherMetrics.SearchTop);
        Put(apps, LauncherMetrics.Side, LauncherMetrics.AppsTop);
        var dotsRow = new Grid { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Dots };
        dotsRow.Children.Add(dots);
        Put(dotsRow, LauncherMetrics.Side, LauncherMetrics.DotsTop);
        hint.Width = LauncherMetrics.Inner;
        hint.Height = LauncherMetrics.Hint;
        Put(hint, LauncherMetrics.Side, LauncherMetrics.HintTop);

        panelHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = fit, RenderTransform = grow, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        panelHost.Children.Add(panel);
        Children.Add(panelHost);

        // While the menu is open a click anywhere else only closes it.
        catcher = new Border { Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
        catcher.MouseLeftButtonDown += (_, e) => { Model.CloseMenu(); e.Handled = true; };
        catcher.MouseRightButtonDown += (_, e) => { Model.CloseMenu(); e.Handled = true; };
        Children.Add(catcher);
        Children.Add(menuLayer);

        Model.WhereIs = Where;
        Model.Changed += Sync;
        if (Model.Icons != null) Model.Icons.Loaded += IconLoaded;
        SizeChanged += (_, e) => Fit(e.NewSize);
        Sync();
    }

    private void Put(FrameworkElement part, double left, double top)
    {
        Canvas.SetLeft(part, left);
        Canvas.SetTop(part, top);
        panel.Children.Add(part);
    }

    /// <summary>Scales the panel for a window [root] points across.</summary>
    public void Fit(Size root)
    {
        double f = LauncherMetrics.Fit(root);
        if (Math.Abs(f - fit.ScaleX) < 0.0005) return;
        fit.ScaleX = f;
        fit.ScaleY = f;
    }

    /// <summary>The panel's arrival: it fades in and grows a little; with motion reduced it is there at once.</summary>
    public void Arrive()
    {
        if (Theme.Static || Theme.ReduceMotion) return;
        panelHost.BeginAnimation(OpacityProperty, null);
        panelHost.Opacity = 0;
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        grow.ScaleX = grow.ScaleY = 0.96;
        Theme.Animate(panelHost, OpacityProperty, 1);
        Theme.Animate(grow, ScaleTransform.ScaleXProperty, 1);
        Theme.Animate(grow, ScaleTransform.ScaleYProperty, 1);
    }

    /// <summary>A round button the Tab key is on takes Enter and Space itself.</summary>
    public bool KeyBelongsToFocus(Key key) =>
        key is Key.Enter or Key.Space && Keyboard.FocusedElement is RoundButton { Ring: true };

    private Point? Where(LauncherSpot spot)
    {
        AppCell? cell = cells.FirstOrDefault(c => c.Spot == spot);
        if (cell == null || cell.ActualWidth <= 0) return null;
        return cell.TranslatePoint(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2), this);
    }

    // MARK: the mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // A click beside the panel closes the launcher; one on the panel's glass does nothing.
        Point p = e.GetPosition(panel);
        bool onPanel = p.X >= 0 && p.Y >= 0 && p.X <= LauncherMetrics.PanelWidth && p.Y <= LauncherMetrics.PanelHeight;
        if (!onPanel) Model.OnClose(LauncherClose.Dismissed);
        Keyboard.Focus(this);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Model.CloseMenu();
        e.Handled = true;
    }

    // MARK: keeping it true

    private void Sync()
    {
        if (shape != Model.Shape)
        {
            shape = Model.Shape;
            BuildApps();
        }
        foreach (AppCell c in cells) c.Refresh();
        strip.Sync();
        BuildButtons();
        search.Sync(Model.Query);
        BuildDots();
        BuildHint();
        BuildMenu();
    }

    private void IconLoaded(string id)
    {
        foreach (AppCell c in cells)
        {
            if (c.App.Id == id) c.ShowIcon();
        }
    }

    /// <summary>The pinned row over a page of the grid; or a search's results; or a line saying there are none.</summary>
    private void BuildApps()
    {
        apps.Children.Clear();
        cells.Clear();
        if (!Model.Scanned && Model.Apps.Count == 0)
        {
            apps.Children.Add(QuietLine("Looking for apps", null));
        }
        else if (Model.Searching)
        {
            if (Model.Listed.Count == 0) apps.Children.Add(QuietLine("No apps found", $"Nothing is called “{Model.Query}”."));
            else AddGrid(0, LauncherMetrics.SearchRowGap);
        }
        else
        {
            IReadOnlyList<LauncherApp> pins = Model.Pins;
            if (pins.Count == 0)
            {
                TextBlock none = Kit.Text("Right-click an app to pin it here.", 17, Theme.Ink3, wrap: false);
                none.HorizontalAlignment = HorizontalAlignment.Center;
                none.VerticalAlignment = VerticalAlignment.Center;
                var box = new Grid { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Cell };
                box.Children.Add(none);
                apps.Children.Add(box);
            }
            else
            {
                double width = LauncherMetrics.PinCell(pins.Count);
                double x0 = Math.Round((LauncherMetrics.Inner - width * pins.Count) / 2);
                for (int i = 0; i < pins.Count; i++) AddCell(pins[i], LauncherSpot.Pin(i), width, x0 + width * i, 0);
            }
            // The hairline between the pins and every app: light in the middle, gone at the ends.
            var line = new Rectangle { Width = LauncherMetrics.Inner, Height = 1, Fill = Hairline, SnapsToDevicePixels = true };
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            Canvas.SetTop(line, LauncherMetrics.Cell + Math.Floor(LauncherMetrics.PinGap / 2));
            apps.Children.Add(line);
            AddGrid(LauncherMetrics.Cell + LauncherMetrics.PinGap, LauncherMetrics.RowGap);
        }
        // A new page fades in; the same page with other apps (a search) changes at once.
        if (builtPage != Model.Page && builtPage >= 0 && !Theme.Static)
        {
            apps.BeginAnimation(OpacityProperty, null);
            apps.Opacity = 0;
            Theme.Animate(apps, OpacityProperty, 1, 0.18);
        }
        builtPage = Model.Page;
    }

    private void AddGrid(double top, double gap)
    {
        (int from, int to) = Model.PageRange;
        for (int i = from; i < to && i < Model.Listed.Count; i++)
        {
            int local = i - from, column = local % LauncherMetrics.Columns, row = local / LauncherMetrics.Columns;
            AddCell(Model.Listed[i], LauncherSpot.Item(i), LauncherMetrics.Column, LauncherMetrics.Column * column,
                top + (LauncherMetrics.Cell + gap) * row);
        }
    }

    private void AddCell(LauncherApp app, LauncherSpot spot, double width, double left, double top)
    {
        var cell = new AppCell(this, app, spot, width);
        Canvas.SetLeft(cell, left);
        Canvas.SetTop(cell, top);
        apps.Children.Add(cell);
        cells.Add(cell);
    }

    /// <summary>A quiet line in the middle of the apps' place.</summary>
    private static FrameworkElement QuietLine(string text, string? detail)
    {
        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 60) };
        TextBlock head = Kit.Text(text, 26, Theme.Ink, FontWeights.Medium, wrap: false);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        words.Children.Add(head);
        if (detail != null)
        {
            TextBlock line = Kit.Text(detail, 18, Theme.Ink2, wrap: false);
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.Margin = new Thickness(0, 8, 0, 0);
            line.MaxWidth = LauncherMetrics.Inner - 80;
            words.Children.Add(line);
        }
        var box = new Grid { Width = LauncherMetrics.Inner, Height = LauncherMetrics.Apps };
        box.Children.Add(words);
        return box;
    }

    private void BuildButtons()
    {
        bool steady = Model.State.SteadyAvailable;
        if (!builtButtons || steady != builtSteady)
        {
            builtButtons = true;
            builtSteady = steady;
            buttons.Children.Clear();
            foreach (LauncherButton b in LauncherButtons.All)
            {
                if (b == LauncherButton.Steady && !steady) continue;
                buttons.Children.Add(new RoundButton(Model, b));
            }
        }
        foreach (RoundButton b in buttons.Children) b.Refresh();
    }

    private void BuildDots()
    {
        int pages = Model.Grid.Pages;
        if (pages == builtPages && Model.Page == dotsPage) return;
        builtPages = pages;
        dotsPage = Model.Page;
        dots.Children.Clear();
        if (pages <= 1) return;
        for (int p = 0; p < pages; p++) dots.Children.Add(new PageDot(Model, p, pages));
    }

    /// <summary>The quiet line at the bottom: what the keys do, or for a few seconds why something was not done.</summary>
    private void BuildHint()
    {
        string now = Model.Notice != null ? "!" + Model.Notice : Model.Hint;
        if (now == builtHint) return;
        builtHint = now;
        hint.Children.Clear();
        if (Model.Notice is string notice)
        {
            TextBlock words = Kit.Text(notice, 16, Theme.Ink, wrap: false);
            words.VerticalAlignment = VerticalAlignment.Center;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Theme.Warn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 9, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
            row.Children.Add(words);
            AutomationProperties.SetLiveSetting(words, AutomationLiveSetting.Polite);
            hint.Children.Add(row);
        }
        else
        {
            TextBlock words = Kit.Text(Model.Hint, 15, Theme.White(0.5), wrap: false);
            words.HorizontalAlignment = HorizontalAlignment.Center;
            words.VerticalAlignment = VerticalAlignment.Center;
            hint.Children.Add(words);
        }
        if (!Theme.Static)
        {
            hint.BeginAnimation(OpacityProperty, null);
            hint.Opacity = 0;
            Theme.Animate(hint, OpacityProperty, 1, Look.Quick);
        }
    }

    private void BuildMenu()
    {
        LauncherMenu? menu = Model.Menu;
        if (menu == builtMenu) return;
        bool fresh = builtMenu == null || menu == null || builtMenu.App != menu.App;
        builtMenu = menu;
        menuLayer.Children.Clear();
        catcher.Visibility = menu == null ? Visibility.Collapsed : Visibility.Visible;
        if (menu == null) return;
        var view = new MenuView(Model, menu) { LayoutTransform = fit };
        // Its top left: just under the pointer, kept inside the window.
        double f = fit.ScaleX;
        double width = LauncherMetrics.MenuWidth * f, height = (LauncherMetrics.MenuRow * menu.Rows + 12) * f;
        double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
        double x = Math.Min(menu.At.X + 4, w - width - 12), y = Math.Min(menu.At.Y + 4, h - height - 12);
        Canvas.SetLeft(view, Math.Max(x, 12));
        Canvas.SetTop(view, Math.Max(y, 12));
        menuLayer.Children.Add(view);
        if (fresh && !Theme.Static)
        {
            view.Opacity = 0;
            Theme.Animate(view, OpacityProperty, 1, Look.Quick);
        }
    }

    // MARK: brushes

    private static readonly Brush Hairline = MakeHairline();

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>The sheet under the panel: the page colour, a little lighter and bluer behind the panel's top.</summary>
    private static Brush MakeSheet()
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.36), GradientOrigin = new Point(0.5, 0.36), RadiusX = 0.62, RadiusY = 0.78,
        };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x1A, 0x2A), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x0C, 0x0E, 0x14), 0.6));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x08, 0x0A), 1));
        return Frozen(b);
    }

    private static Brush MakeHairline()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(31, 255, 255, 255), 0.33));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(31, 255, 255, 255), 0.67));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        return Frozen(b);
    }
}

// MARK: the top strip

/// <summary>The time, large, and the date; Wi-Fi and the battery on the right. All on one baseline, so it is drawn by hand.</summary>
public sealed class StripView : FrameworkElement
{
    private readonly LauncherModel model;
    private static readonly Typeface Light = new(Theme.Display, FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
    private static readonly Typeface Regular = new(Theme.Display, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen Shell = MakePen(Theme.White(0.45), 1.5);
    private static readonly Brush Nub = Theme.White(0.45);
    private const double Baseline = 49;

    public StripView(LauncherModel model)
    {
        this.model = model;
    }

    private static Pen MakePen(Brush b, double width)
    {
        var p = new Pen(b, width);
        p.Freeze();
        return p;
    }

    public void Sync()
    {
        string said = $"{model.TimeText}, {model.DateText}";
        if (model.Wifi is LauncherWifi w) said += w == LauncherWifi.Connected ? ", Wi-Fi connected" : ", Wi-Fi not connected";
        if (model.Battery is LauncherBattery b) said += $", battery {b.Percent} percent{(b.OnMains ? ", plugged in" : "")}";
        AutomationProperties.SetName(this, said);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    private sealed class StripPeer : FrameworkElementAutomationPeer
    {
        public StripPeer(StripView owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    private FormattedText Words(string text, double size, Brush ink, Typeface face) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText time = Words(model.TimeText, 52, Theme.Ink, Light);
        dc.DrawText(time, new Point(-2, Baseline - time.Baseline));
        FormattedText date = Words(model.DateText, 21, Theme.Ink2, Regular);
        dc.DrawText(date, new Point(time.WidthIncludingTrailingWhitespace + 16, Baseline - date.Baseline));

        double x = ActualWidth;
        if (model.Battery is LauncherBattery battery)
        {
            Brush tint = battery.OnMains ? Theme.Good : battery.Percent <= 20 ? Theme.Warn : Theme.Ink;
            // The battery's picture: a shell, a bar as full as the battery is, and its nub.
            const double bodyW = 35, bodyH = 17;
            double top = Baseline + 2 - bodyH;
            dc.DrawRoundedRectangle(Nub, null, new Rect(x - 2.5, top + 5, 2.5, 7), 1, 1);
            x -= 2.5 + 2 + bodyW;
            dc.DrawRoundedRectangle(null, Shell, new Rect(x + 0.75, top + 0.75, bodyW - 1.5, bodyH - 1.5), 4.5, 4.5);
            double bar = Math.Max(3, 28 * battery.Percent / 100.0);
            dc.DrawRoundedRectangle(tint, null, new Rect(x + 3.5, top + 3.5, bar, 10), 2.5, 2.5);
            FormattedText percent = Words($"{battery.Percent}%", 18, Theme.Ink, Regular);
            x -= 9 + percent.Width;
            dc.DrawText(percent, new Point(x, Baseline - percent.Baseline));
            if (battery.OnMains)
            {
                FormattedText bolt = Draw.Icon(LauncherGlyph.Bolt, 14, Theme.Good, dip);
                x -= 6 + bolt.Width;
                dc.DrawText(bolt, new Point(x, Baseline - 6.5 - bolt.Height / 2));
            }
            x -= 26;
        }
        if (model.Wifi is LauncherWifi wifi)
        {
            bool on = wifi == LauncherWifi.Connected;
            FormattedText words = Words(on ? "Wi-Fi" : "No Wi-Fi", 18, on ? Theme.Ink2 : Theme.Ink3, Regular);
            x -= words.Width;
            dc.DrawText(words, new Point(x, Baseline - words.Baseline));
            FormattedText glyph = Draw.Icon(LauncherGlyph.Wifi, 18, on ? Theme.Ink : Theme.Ink3, dip);
            x -= 8 + glyph.Width;
            dc.DrawText(glyph, new Point(x, Baseline - 6.5 - glyph.Height / 2));
        }
    }
}

// MARK: the round buttons

/// <summary>A round button with its name under it. Lit in the accent while what it switches is on.</summary>
public sealed class RoundButton : Press
{
    private readonly LauncherModel model;
    private readonly LauncherButton button;
    private readonly Ellipse fill, focus;
    private readonly FrameworkElement glyph;
    private readonly TextBlock title;
    private readonly SolidColorBrush fillBrush = new();

    public RoundButton(LauncherModel model, LauncherButton button)
    {
        this.model = model;
        this.button = button;
        Label = button.Title();
        if (button.StaysOpen()) Toggled = () => model.IsLit(button);
        Width = LauncherMetrics.ButtonCell;
        Height = LauncherMetrics.Buttons;
        double d = LauncherMetrics.ButtonCircle;
        fill = new Ellipse { Width = d, Height = d, Fill = fillBrush, StrokeThickness = 1 };
        focus = new Ellipse { Width = d + 10, Height = d + 10, Stroke = Theme.Accent, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };
        if (button == LauncherButton.Depth)
        {
            TextBlock t = Kit.Text("3D", 19, Theme.Ink, FontWeights.SemiBold, wrap: false);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(0, 0, 0, 2);
            glyph = t;
        }
        else
        {
            glyph = Kit.Icon(button.Glyph(), 22, Theme.Ink);
        }
        var circle = new Grid { Width = d + 10, Height = d + 10, Margin = new Thickness(0, -5, 0, -5) };
        circle.Children.Add(fill);
        circle.Children.Add(glyph);
        circle.Children.Add(focus);
        title = Kit.Text(button.Title(), 16, Theme.White(0.74), wrap: false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 8, 0, 0);
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        column.Children.Add(circle);
        column.Children.Add(title);
        Child = column;
        Click += () => model.Press(button);
        Restyle();
    }

    protected override void Restyle()
    {
        bool lit = model.IsLit(button);
        Color c = lit ? Theme.C(Hover ? Look.Held : Look.Accent) : Theme.C(Hover ? Look.GlassHover : Look.Control.Alpha(0.78));
        Theme.AnimateColor(fillBrush, c);
        fill.Stroke = lit ? Theme.B(Look.Held.Alpha(0.9)) : Hover ? Theme.RimFocus : Theme.Rim;
        Brush ink = lit ? Theme.Page : Theme.Ink;
        if (glyph is TextBlock t) t.Foreground = ink;
        title.Foreground = lit || Hover ? Theme.Ink : Theme.White(0.74);
        focus.Opacity = Ring ? 1 : 0;
    }
}

// MARK: search

/// <summary>What has been typed. It is not a text box: the window takes the keys itself, so there is nothing to click.</summary>
public sealed class SearchField : Border
{
    public const double FieldWidth = 500;
    private readonly TextBlock glass, typed, prompt;
    private const double Room = FieldWidth - 2 * 20 - 22 - 12 - 2 - 6;

    public SearchField()
    {
        Width = FieldWidth;
        Height = LauncherMetrics.Search;
        CornerRadius = new CornerRadius(LauncherMetrics.Search / 2);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(20, 0, 20, 0);
        glass = Kit.Icon(Glyph.Search, 18, Theme.Ink3);
        glass.Width = 22;
        glass.Margin = new Thickness(0, 1, 12, 0);
        typed = Kit.Text("", 20, Theme.Ink, wrap: false);
        typed.TextTrimming = TextTrimming.None;
        typed.VerticalAlignment = VerticalAlignment.Center;
        typed.Margin = new Thickness(0, 0, 2, 2);
        var caret = new Border { Width = 2, Height = 24, CornerRadius = new CornerRadius(1), Background = Theme.Accent, VerticalAlignment = VerticalAlignment.Center };
        prompt = Kit.Text("Search apps", 20, Theme.Ink3, wrap: false);
        prompt.VerticalAlignment = VerticalAlignment.Center;
        prompt.Margin = new Thickness(6, 0, 0, 2);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(glass);
        row.Children.Add(typed);
        row.Children.Add(caret);
        row.Children.Add(prompt);
        Child = row;
        AutomationProperties.SetName(this, "Search apps");
        Sync("");
    }

    public void Sync(string query)
    {
        bool typing = query.Length > 0;
        Background = Theme.White(typing ? 0.09 : 0.06);
        BorderBrush = typing ? Theme.B(Look.Accent.Alpha(0.75)) : Theme.Rim;
        glass.Foreground = typing ? Theme.Ink : Theme.Ink3;
        typed.Text = Tail(query);
        typed.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        prompt.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(this, query);
    }

    /// <summary>A search too long for the field keeps its end in view, where the typing is.</summary>
    private string Tail(string query)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Draw.Text(query, 20, Theme.Ink, FontWeights.Normal, dip).WidthIncludingTrailingWhitespace <= Room) return query;
        int[] starts = StringInfo.ParseCombiningCharacters(query);
        for (int i = 1; i < starts.Length; i++)
        {
            string cut = "…" + query[starts[i]..];
            if (Draw.Text(cut, 20, Theme.Ink, FontWeights.Normal, dip).WidthIncludingTrailingWhitespace <= Room) return cut;
        }
        return "…";
    }
}

// MARK: apps

/// <summary>
/// One app: its icon and its name, on two lines if it needs them. The pointer over it lifts it a little and lights
/// its name; the arrow keys' selection draws a ring in the accent round it.
/// </summary>
public sealed class AppCell : Press
{
    public readonly LauncherApp App;
    public readonly LauncherSpot Spot;
    private readonly LauncherView view;
    private readonly Border light, halo, ring;
    private readonly Grid iconBox;
    private readonly ScaleTransform lift = new(1, 1);
    private readonly TextBlock name;
    private bool selected, lifted;

    private static readonly Brush Halo = Theme.B(Look.Accent.Alpha(0.26));
    private static readonly Brush SelectedFill = Theme.B(Look.Accent.Alpha(0.16));
    private static readonly Brush NameInk = Theme.White(0.82);
    private static readonly Brush Waiting = Theme.White(0.05);

    public AppCell(LauncherView view, LauncherApp app, LauncherSpot spot, double width)
    {
        this.view = view;
        App = app;
        Spot = spot;
        Label = app.Name;
        Kind = AutomationControlType.ListItem;
        Focusable = false;
        Width = width;
        Height = LauncherMetrics.Cell;
        double lightWidth = Math.Min(width - 4, LauncherMetrics.Highlight);
        var radius = new CornerRadius(18);
        light = new Border { CornerRadius = radius };
        halo = new Border
        {
            CornerRadius = new CornerRadius(21.5), BorderBrush = Halo, BorderThickness = new Thickness(7), Margin = new Thickness(-3.5),
            Opacity = 0, IsHitTestVisible = false,
        };
        ring = new Border { CornerRadius = radius, BorderBrush = Theme.Accent, BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false };

        iconBox = new Grid
        {
            Width = LauncherMetrics.Icon, Height = LauncherMetrics.Icon, RenderTransform = lift, RenderTransformOrigin = new Point(0.5, 0.5),
            Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };
        name = Kit.Text(app.Name, LauncherMetrics.NameSize, NameInk);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextAlignment = TextAlignment.Center;
        name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        name.LineHeight = LauncherMetrics.NameLine;
        name.MaxHeight = LauncherMetrics.NameLine * 2;
        name.Width = lightWidth - 16;
        name.Margin = new Thickness(0, 5, 0, 0);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        column.Children.Add(iconBox);
        column.Children.Add(name);

        var face = new Grid { Width = lightWidth, Height = LauncherMetrics.Cell, HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(light);
        face.Children.Add(halo);
        face.Children.Add(ring);
        face.Children.Add(column);
        Child = face;
        Click += () => view.Model.Open(app);
        ShowIcon();
        Restyle();
    }

    /// <summary>The app's own icon once it is read; in pictures drawn to files, a drawn one that does not depend on what is installed.</summary>
    public void ShowIcon()
    {
        iconBox.Children.Clear();
        if (view.Model.Samples || view.Model.Icons == null)
        {
            iconBox.Children.Add(new SampleIcon(App.Name));
            return;
        }
        ImageSource? image = view.Model.Icons.Icon(App);
        if (image == null)
        {
            // Not read yet: a faint place for it, so nothing jumps when it comes.
            iconBox.Children.Add(new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(14), Background = Waiting });
            return;
        }
        var picture = new Image { Source = image, Width = LauncherMetrics.Icon, Height = LauncherMetrics.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        iconBox.Children.Add(picture);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        view.Model.ContextClick(Spot, e.GetPosition(view));
        e.Handled = true;
    }

    protected override void Restyle()
    {
        if (view == null) return;
        bool nowSelected = view.Model.Spot == Spot;
        bool held = view.Model.Menu?.Spot == Spot;
        bool lit = Hover || nowSelected || held;
        light.Background = nowSelected ? SelectedFill : lit ? Theme.White(held ? 0.12 : 0.08) : Brushes.Transparent;
        name.Foreground = lit ? Brushes.White : NameInk;
        halo.Opacity = ring.Opacity = nowSelected ? 1 : 0;
        if (nowSelected != selected)
        {
            selected = nowSelected;
            AutomationProperties.SetItemStatus(this, selected ? "Selected" : "");
        }
        bool up = Hover && !Theme.ReduceMotion;
        if (up != lifted)
        {
            lifted = up;
            Theme.Animate(lift, ScaleTransform.ScaleXProperty, up ? 1.06 : 1, Look.Quick);
            Theme.Animate(lift, ScaleTransform.ScaleYProperty, up ? 1.06 : 1, Look.Quick);
        }
    }
}

/// <summary>
/// A drawn icon for a made-up app: a rounded square in one of the look's colours, muted, with the name's initials,
/// the size an app's own icon is inside its box.
/// </summary>
public sealed class SampleIcon : FrameworkElement
{
    private readonly string initials;
    private readonly Brush fill;
    private static readonly Pen Rim = MakeRim();
    private static readonly LookColor[] Tints =
    {
        Look.Accent, new(hex: 0xA77BFF), new(hex: 0xFF7AB8), Look.Good, Look.Warn, new(hex: 0xFF6B5E), new(hex: 0x6FD3E8), new(hex: 0xB9C2D6),
    };

    public SampleIcon(string name)
    {
        Width = Height = 60;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        string[] words = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        initials = words.Length == 0 ? "" : words.Length == 1 ? words[0][..1].ToUpperInvariant()
            : (words[0][..1] + words[1][..1]).ToUpperInvariant();
        // The same colour for the same name on every run.
        uint sum = 7;
        foreach (char c in name) sum = unchecked(sum * 31 + c);
        LookColor tint = Tints[sum % (uint)Tints.Length];
        var g = new LinearGradientBrush(Mix(Look.Control, tint, 0.58), Mix(Look.GlassBottom, tint, 0.34), 90);
        g.Freeze();
        fill = g;
    }

    private static Pen MakeRim()
    {
        var p = new Pen(Theme.White(0.16), 1);
        p.Freeze();
        return p;
    }

    private static Color Mix(LookColor a, LookColor b, double t) =>
        Theme.C(new LookColor(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(fill, Rim, new Rect(0.5, 0.5, 59, 59), 14, 14);
        FormattedText t = Draw.Text(initials, 24, Theme.White(0.94), FontWeights.Medium, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(t, new Point((60 - t.Width) / 2, (60 - t.Height) / 2 - 1));
    }
}

// MARK: page dots, menu

/// <summary>One page's dot: lit for the page shown; a click goes to its page.</summary>
public sealed class PageDot : Press
{
    private readonly Ellipse dot;
    private readonly bool current;

    public PageDot(LauncherModel model, int page, int pages)
    {
        current = page == model.Page;
        Label = $"Page {page + 1} of {pages}";
        Focusable = false;
        Width = 24;
        Height = LauncherMetrics.Dots;
        dot = new Ellipse { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Child = dot;
        Click += () => model.TurnTo(page);
        Restyle();
    }

    protected override void Restyle()
    {
        if (dot == null) return;
        dot.Fill = current ? Theme.Ink : Theme.White(Hover ? 0.5 : 0.28);
    }
}

/// <summary>The right-click menu, in the launcher's own glass.</summary>
public sealed class MenuView : Border
{
    public MenuView(LauncherModel model, LauncherMenu menu)
    {
        Width = LauncherMetrics.MenuWidth;
        CornerRadius = new CornerRadius(14);
        Background = Theme.B(Look.GlassRaised);
        BorderBrush = Theme.RimFocus;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(5);
        AutomationProperties.SetName(this, menu.App.Name);
        var rows = new StackPanel();
        rows.Children.Add(new MenuRow(menu.Pinned ? Glyph.Unpin : Glyph.Pin, menu.Pinned ? "Unpin" : "Pin", menu.Row == 0, () => model.TogglePin(menu.App)));
        if (menu.HasFile)
            rows.Children.Add(new MenuRow(Glyph.Folder, "Show in File Explorer", menu.Row == 1, () => model.ShowInExplorer(menu.App)));
        Child = rows;
        // A click on the menu's own edge is not a click beside it.
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    private sealed class MenuRow : Press
    {
        private readonly TextBlock icon, title;
        private readonly bool keyed;

        public MenuRow(string glyph, string text, bool keyed, Action action)
        {
            this.keyed = keyed;
            Label = text;
            Kind = AutomationControlType.MenuItem;
            Focusable = false;
            Height = LauncherMetrics.MenuRow;
            CornerRadius = new CornerRadius(9);
            icon = Kit.Icon(glyph, 17, Theme.Ink2);
            icon.Width = 24;
            icon.Margin = new Thickness(0, 1, 12, 0);
            title = Kit.Text(text, 17, Theme.Ink, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 0, 1);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            row.Children.Add(title);
            Child = row;
            Click += action;
            Restyle();
        }

        protected override void Restyle()
        {
            if (title == null) return;
            bool lit = Hover || keyed;
            Background = lit ? Theme.B(Look.Accent.Alpha(0.9)) : Brushes.Transparent;
            icon.Foreground = lit ? Theme.Page : Theme.Ink2;
            title.Foreground = lit ? Theme.Page : Theme.Ink;
        }
    }
}
