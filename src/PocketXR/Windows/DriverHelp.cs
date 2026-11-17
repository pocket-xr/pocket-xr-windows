using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PocketXR.Core;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// "Add floating screens": how to get the virtual display driver floating screens need, in three steps. Pocket XR
/// points to Parsec's Virtual Display Driver (free, from Parsec Cloud, no Parsec app or account needed); it does not
/// bundle it. Pocket XR notices the driver by itself once it is installed (it looks every few seconds).
/// </summary>
public static class DriverHelp
{
    /// <summary>Parsec's own download of its driver, as listed in Microsoft's winget catalogue (Parsec.ParsecVDD).</summary>
    public const string DownloadUrl = "https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe";
    public const double Width = 520;

    public static PxWindow Window(AppState state, IAppActions actions)
    {
        var binder = new Binder();
        FrameworkElement content = Content(state, actions, binder);
        var w = new PxWindow("Add floating screens", content) { Width = Width, SizeToContent = SizeToContent.Height };
        state.Changed += binder.Sync;
        return w;
    }

    public static FrameworkElement Content(AppState state, IAppActions actions, Binder binder)
    {
        var column = new StackPanel { Margin = new Thickness(24, 8, 24, 24), Width = Width - 48 };
        column.Children.Add(Theme.Arrive(Kit.Title("Add floating screens"), 0));
        TextBlock intro = Kit.Body("Windows cannot add screens by itself: it needs a virtual display driver. Pocket XR uses Parsec's, which is free. You do not need the Parsec app or an account.");
        intro.Margin = new Thickness(0, 8, 0, 18);
        column.Children.Add(Theme.Arrive(intro, 1));

        var download = new PxButton("Download the driver", Glyph.Forward, ButtonKind.Primary, actions.DownloadDriver);
        Grid status = Kit.Dot(false);
        TextBlock statusText = Kit.Line("");
        statusText.VerticalAlignment = VerticalAlignment.Center;
        StackPanel statusRow = Kit.Across(10, status, statusText);
        statusRow.Margin = new Thickness(0, 6, 0, 0);

        StackPanel steps = Kit.Card(null,
            Step(1, "Download Parsec's driver", "From Parsec, half a megabyte.", download),
            Kit.Divider(),
            Step(2, "Open the file and choose Yes", "Windows asks an administrator once, because it adds a driver.", null),
            Kit.Divider(),
            Step(3, "That is all", "Pocket XR notices the driver and makes your floating screens. Nothing to restart.", statusRow));
        column.Children.Add(Theme.Arrive(steps, 2));

        TextBlock other = Kit.Small("Already use Apollo for game streaming? Its driver (SudoVDA) works too. Pocket XR is not made by or affiliated with Parsec.");
        other.Margin = new Thickness(4, 14, 4, 0);
        column.Children.Add(Theme.Arrive(other, 3));

        binder.Add(() =>
        {
            Kit.SetDot(status, state.DriverReady);
            statusText.Text = state.DriverReady ? "Driver found. Your floating screens are ready." : "Waiting for the driver…";
            statusText.Foreground = state.DriverReady ? Theme.Ink : Theme.Ink2;
            download.IsEnabled = !state.DriverReady;
        });
        return column;
    }

    /// <summary>One numbered step: the number in a ring, its words, and what goes with it under them.</summary>
    private static UIElement Step(int number, string title, string note, UIElement? extra)
    {
        var ring = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 14, 0),
            Child = new TextBlock
            {
                Text = number.ToString(), FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Theme.Held,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Theme.Text,
            },
        };
        var words = new StackPanel();
        words.Children.Add(Kit.Line(title));
        TextBlock n = Kit.Small(note);
        n.Margin = new Thickness(0, 2, 0, 0);
        words.Children.Add(n);
        if (extra is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Left;
            fe.Margin = new Thickness(0, 12, 0, 0);
            words.Children.Add(fe);
        }
        var row = new DockPanel { Margin = new Thickness(Theme.Inset, 14, Theme.Inset, 14) };
        DockPanel.SetDock(ring, Dock.Left);
        row.Children.Add(ring);
        row.Children.Add(words);
        return row;
    }

    public static void Pictures(string folder)
    {
        var waiting = new AppState { DriverReady = false };
        UiRender.WindowPng(Path.Combine(folder, "driver-help-1-waiting.png"), "Add floating screens", Content(waiting, new NoActions(), new Binder()), Width);
        var found = new AppState { DriverReady = true };
        UiRender.WindowPng(Path.Combine(folder, "driver-help-2-found.png"), "Add floating screens", Content(found, new NoActions(), new Binder()), Width);
    }
}
