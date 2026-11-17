using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PocketXR.Core;

namespace PocketXR.Ui;

/// <summary>
/// Pocket XR's look for WPF: the core's numbers (Look) as brushes, type and motion. Shared by every window.
/// "Light on glass": always dark, one accent, light for focus and never shadow, nothing bounces.
/// </summary>
public static class Theme
{
    /// <summary>True while windows are drawn to picture files (--render-ui): nothing scrolls, nothing waits to arrive.</summary>
    public static bool Static;

    public static Color C(LookColor c) =>
        Color.FromArgb((byte)Math.Round(c.A * 255), (byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    public static SolidColorBrush B(LookColor c)
    {
        var b = new SolidColorBrush(C(c));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush B(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush White(double alpha) => B(new LookColor(1, 1, 1, alpha));

    public static readonly SolidColorBrush Page = B(Look.Page);
    public static readonly SolidColorBrush Pad = B(Look.Pad);
    public static readonly SolidColorBrush Card = B(Look.Card);
    public static readonly SolidColorBrush Accent = B(Look.Accent);
    public static readonly SolidColorBrush AccentDeep = B(Look.AccentDeep);
    public static readonly SolidColorBrush Held = B(Look.Held);
    public static readonly SolidColorBrush Ink = B(Look.Ink);
    public static readonly SolidColorBrush Ink2 = B(Look.Ink2);
    public static readonly SolidColorBrush Ink3 = B(Look.Ink3);
    public static readonly SolidColorBrush Rim = B(Look.Rim);
    public static readonly SolidColorBrush RimFocus = B(Look.RimFocus);
    public static readonly SolidColorBrush GlassHover = B(Look.GlassHover);
    public static readonly SolidColorBrush Control = B(Look.Control);
    public static readonly SolidColorBrush ControlSoft = B(Look.Control.Alpha(0.55));
    public static readonly SolidColorBrush Media = B(Look.Media);
    public static readonly SolidColorBrush Good = B(Look.Good);
    public static readonly SolidColorBrush Warn = B(Look.Warn);
    public static readonly SolidColorBrush Hairline = White(0.08);
    public static readonly SolidColorBrush Clear = B(new LookColor(0, 0, 0, 0));
    public static readonly Color DangerColor = Color.FromRgb(229, 72, 77);
    public static readonly SolidColorBrush DangerInk = B(Color.FromRgb(255, 143, 143));

    public static readonly LinearGradientBrush Glass = MakeGlass();

    private static LinearGradientBrush MakeGlass()
    {
        var g = new LinearGradientBrush(C(Look.GlassTop), C(Look.GlassBottom), 90);
        g.Freeze();
        return g;
    }

    // Type: Windows 11's own face (Segoe UI Variable), Segoe UI on Windows 10. The Mac app's sizes.
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Small = new("Segoe UI Variable Small, Segoe UI");
    /// <summary>Windows' own icon face: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const double TitleSize = 26, HeadingSize = 16, BodySize = 15, LineSize = 14, NoteSize = 12.5, ChipSize = 11,
        ButtonSize = 14;

    // Sizes for a desktop, as on the Mac: rows 44, controls 32.
    public const double RowHeight = 44, ButtonHeight = 32, CardRadius = 12, ControlRadius = 8, Inset = 16, Rise = 12;
    public const double Stagger = 0.06;

    /// <summary>
    /// The few things WPF only takes as styles: a thin dark scroll bar and a text box, in Pocket XR's colours. Kept
    /// as one piece of XAML, so every window gets them.
    /// </summary>
    public static void InstallResources(Application app)
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="PxThumb" TargetType="Thumb">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Thumb">
                      <Border x:Name="b" CornerRadius="3" Background="#38FFFFFF" Margin="2,0,2,0"/>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#66FFFFFF"/></Trigger>
                        <Trigger Property="IsDragging" Value="True"><Setter TargetName="b" Property="Background" Value="#8FB6FF"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style x:Key="PxPage" TargetType="RepeatButton">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="Focusable" Value="False"/>
                <Setter Property="Template">
                  <Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ScrollBar">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="Width" Value="10"/>
                <Setter Property="MinWidth" Value="10"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                      <Grid Background="Transparent">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                          <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource PxPage}" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
                          <Track.Thumb><Thumb Style="{StaticResource PxThumb}" MinHeight="28"/></Track.Thumb>
                          <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource PxPage}" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
                        </Track>
                      </Grid>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="Foreground" Value="#EBFFFFFF"/>
                <Setter Property="CaretBrush" Value="#8FB6FF"/>
                <Setter Property="SelectionBrush" Value="#5A93FF"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="TextBox">
                      <Border x:Name="b" CornerRadius="8" Background="#15171C" BorderBrush="#24FFFFFF" BorderThickness="1" Padding="8,0,8,1">
                        <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" Focusable="False"
                                      HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="#52FFFFFF"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="#5A93FF"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ToolTip">
                <Setter Property="OverridesDefaultStyle" Value="True"/>
                <Setter Property="HasDropShadow" Value="False"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ToolTip">
                      <Border CornerRadius="6" Background="#F02A3142" BorderBrush="#24FFFFFF" BorderThickness="1" Padding="8,4,8,5">
                        <ContentPresenter/>
                      </Border>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """;
        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml));
    }

    public static readonly IEasingFunction? None = null;

    /// <summary>Everything that arrives or moves on screen.</summary>
    public static KeySpline EaseEnter => new(Look.EaseEnter.Item1, Look.EaseEnter.Item2, Look.EaseEnter.Item3, Look.EaseEnter.Item4);
    /// <summary>Everything that leaves.</summary>
    public static KeySpline EaseExit => new(Look.EaseExit.Item1, Look.EaseExit.Item2, Look.EaseExit.Item3, Look.EaseExit.Item4);
    public static KeySpline EaseInOut => new(Look.EaseInOut.Item1, Look.EaseInOut.Item2, Look.EaseInOut.Item3, Look.EaseInOut.Item4);

    /// <summary>Windows' "Animation effects" setting is off: things only fade, or change at once.</summary>
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    /// <summary>Moves a number to [to] over [seconds] on the enter curve; at once in pictures and with motion reduced.</summary>
    public static void Animate(IAnimatable target, DependencyProperty property, double to, double seconds = -1,
        double delay = 0, KeySpline? ease = null)
    {
        if (seconds < 0) seconds = Look.Enter;
        if (Static || ReduceMotion || seconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), ease ?? EaseEnter));
        target.BeginAnimation(property, a);
    }

    /// <summary>Moves a brush's colour to [to] over [seconds].</summary>
    public static void AnimateColor(SolidColorBrush brush, Color to, double seconds = -1)
    {
        if (seconds < 0) seconds = Look.Quick;
        if (Static || ReduceMotion || seconds <= 0)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        var a = new ColorAnimationUsingKeyFrames();
        a.KeyFrames.Add(new SplineColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), EaseEnter));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    /// <summary>
    /// A card's arrival: a fade and a 12 px rise, each card 60 ms after the one before. With motion reduced it only fades.
    /// </summary>
    public static T Arrive<T>(T element, int order) where T : FrameworkElement
    {
        if (Static) return element;
        var move = new TranslateTransform(0, ReduceMotion ? 0 : Rise);
        element.RenderTransform = move;
        element.Opacity = 0;
        bool shown = false;
        element.Loaded += (_, _) =>
        {
            if (shown) return;
            shown = true;
            double delay = ReduceMotion ? 0 : Stagger * order;
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
            fade.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Look.Enter)), EaseEnter));
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            if (!ReduceMotion)
            {
                var rise = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
                rise.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Look.Enter)), EaseEnter));
                move.BeginAnimation(TranslateTransform.YProperty, rise);
            }
        };
        return element;
    }
}

/// <summary>Glyphs from Windows' icon face, named for what they show.</summary>
public static class Glyph
{
    public const string Close = "", Minimise = "", Settings = "", Recentre = "",
        Plus = "", Minus = "", Check = "", ChevronRight = "", ChevronLeft = "",
        ChevronDown = "", Search = "", Pin = "", Unpin = "", Save = "", Delete = "",
        Reset = "", Glasses = "", Screen = "", Space = "", Tracking = "", Hand = "",
        Controller = "", Keyboard = "", About = "", Mail = "", Folder = "",
        Open = "", Help = "", Warning = "", Cursor = "", Layout = "", Cube = "",
        Lock = "", Apps = "", Back = "", Forward = "", Camera = "", Eye = "",
        Play = "", More = "", Power = "", Home = "";
}
