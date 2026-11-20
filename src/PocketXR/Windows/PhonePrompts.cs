using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// The questions a phone brings to this PC: "allow this phone?" (the PC helper's pairing) and the PIN a phone shows
/// when it pairs with this PC's own host and no PC helper on the phone's side gives the PIN by itself.
/// </summary>
public static class PhonePrompts
{
    /// <summary>A yes or no question in front of everything, asked from any thread; false after a minute unanswered.</summary>
    public static bool Ask(Dispatcher ui, string title, string text)
    {
        bool yes = false;
        var done = new ManualResetEventSlim();
        ui.BeginInvoke(() =>
        {
            try
            {
                var owner = new Window
                {
                    Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent, Width = 1, Height = 1, Left = -100, Top = -100,
                };
                owner.Show();
                yes = MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                owner.Close();
            }
            finally { done.Set(); }
        });
        return done.Wait(TimeSpan.FromSeconds(60)) && yes;
    }

    /// <summary>A message in front of everything, with OK.</summary>
    public static void Tell(Dispatcher ui, string title, string text) => ui.BeginInvoke(() =>
    {
        var owner = new Window
        {
            Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent, Width = 1, Height = 1, Left = -100, Top = -100,
        };
        owner.Show();
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        owner.Close();
    });

    /// <summary>
    /// "Type the PIN your phone shows": a small window that gives the four digits to [give]. It closes itself when
    /// [stillWanted] turns false (the phone paired, gave up, or the PIN came another way).
    /// </summary>
    public static PxWindow PinWindow(Func<string, bool> give, Func<bool> stillWanted)
    {
        const double width = 400;
        var column = new StackPanel { Margin = new Thickness(24, 8, 24, 24), Width = width - 48 };
        column.Children.Add(Kit.Title("Pair your phone"));
        TextBlock intro = Kit.Body("Your phone shows a four-digit PIN. Type it here to let it stream this PC.");
        intro.Margin = new Thickness(0, 8, 0, 16);
        column.Children.Add(intro);
        var box = new TextBox
        {
            Height = Theme.ButtonHeight, MaxLength = 4, FontSize = 18, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        column.Children.Add(box);
        TextBlock note = Kit.Small("");
        note.Margin = new Thickness(0, 8, 0, 0);
        column.Children.Add(note);
        PxWindow? window = null;
        void Send()
        {
            string pin = box.Text.Trim();
            if (!Regex.IsMatch(pin, @"^\d{4}$")) { note.Text = "The PIN is four digits."; return; }
            if (give(pin)) window?.Close();
            else note.Text = "The phone is not waiting for a PIN any more.";
        }
        var pair = new PxButton("Pair", null, ButtonKind.Primary, Send) { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        column.Children.Add(pair);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Send(); };
        window = new PxWindow("Pair your phone", column) { Width = width, SizeToContent = SizeToContent.Height, Topmost = true };
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        watch.Tick += (_, _) => { if (!stillWanted()) window.Close(); };
        window.Closed += (_, _) => watch.Stop();
        window.Loaded += (_, _) => { watch.Start(); box.Focus(); };
        return window;
    }
}
