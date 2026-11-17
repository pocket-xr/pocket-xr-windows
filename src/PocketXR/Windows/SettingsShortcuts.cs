using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PocketXR.Ui;

namespace PocketXR.Windows;

/// <summary>
/// The shortcuts list in Settings: each shortcut's keys, a Change button that takes the next keys pressed, a mark on a
/// shortcut whose keys another app already uses (Windows gives a shortcut to the app that asked first), and a way back
/// to the keys Pocket XR comes with.
/// </summary>
public static class SettingsShortcuts
{
    public static StackPanel Card(SettingsStore store, AppState state, IAppActions actions, Binder b)
    {
        var rows = new List<UIElement?>();
        foreach (Shortcut shortcut in Shortcut.All)
        {
            if (rows.Count > 0) rows.Add(Kit.Divider());
            rows.Add(new Row(shortcut, state, actions, b));
        }
        rows.Add(Kit.Divider());
        var resetAll = new PxButton("Use the usual keys for all", Glyph.Reset, ButtonKind.Glass, () => actions.ResetShortcut(null));
        var footer = new SettingsRow("Keys another app already uses", "Windows gives a shortcut to the app that asked for it first. Choose other keys for one marked here, or change it in that other app.", resetAll);
        b.Add(() => resetAll.Visibility = ShortcutChoices.AnyChosen ? Visibility.Visible : Visibility.Collapsed);
        rows.Add(footer);
        StackPanel list = Kit.Card("Shortcuts", rows.ToArray());
        b.Add(() => Theme.Animate(list, UIElement.OpacityProperty, store.S.Shortcuts ? 1 : 0.45));
        return list;
    }

    /// <summary>One shortcut: its name, a note when something is to be said, its keys and its buttons.</summary>
    private sealed class Row : Grid
    {
        private readonly Shortcut shortcut;
        private readonly AppState state;
        private readonly IAppActions actions;
        private readonly SettingsRow row;
        private readonly Border keysHolder = new();
        private readonly PxButton change;
        private readonly IconButton reset;
        private bool listening;
        private string? said;

        public Row(Shortcut shortcut, AppState state, IAppActions actions, Binder b)
        {
            this.shortcut = shortcut;
            this.state = state;
            this.actions = actions;
            change = new PxButton("Change", null, ButtonKind.Glass, Listen) { Margin = new Thickness(10, 0, 0, 0) };
            reset = new IconButton(Glyph.Reset, $"Use {shortcut.Usual.Text} again", () =>
            {
                said = null;
                actions.ResetShortcut(shortcut.Id);
            }) { Margin = new Thickness(4, 0, 0, 0) };
            StackPanel right = Kit.Across(0, keysHolder, change, reset);
            row = new SettingsRow(shortcut.Title, null, right);
            Children.Add(row);
            // The keys are taken from the Change button while it listens: it has the keyboard then.
            change.PreviewKeyDown += OnKey;
            change.LostKeyboardFocus += (_, _) => { if (listening) Stop(null); };
            b.Add(Show);
        }

        private void Show()
        {
            reset.Visibility = shortcut.Changed && !listening ? Visibility.Visible : Visibility.Collapsed;
            if (listening)
            {
                keysHolder.Child = Kit.Text("Press the new keys…", Theme.LineSize, Theme.Held, wrap: false);
                ((TextBlock)keysHolder.Child).VerticalAlignment = VerticalAlignment.Center;
                row.SetNote("With Ctrl, Alt or the Windows key. Esc cancels.");
                return;
            }
            keysHolder.Child = Kit.Keys(shortcut.Keys);
            if (said != null) row.SetNote(said, warn: true);
            else if (state.ShortcutsTaken.Contains(shortcut.Id)) row.SetNote("Another app already uses these keys, so this one does not work.", warn: true);
            else row.SetNote(null);
        }

        private void Listen()
        {
            if (listening) return;
            listening = true;
            said = null;
            actions.PauseShortcuts(true);
            change.Focus();
            Keyboard.Focus(change);
            Show();
        }

        private void Stop(string? note)
        {
            listening = false;
            said = note;
            actions.PauseShortcuts(false);
            Show();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (!listening) return;
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                Stop(null);
                return;
            }
            // A modifier alone: wait for the key that goes with it.
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
            uint mods = 0;
            ModifierKeys m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= ShortcutKeys.Ctrl;
            if ((m & ModifierKeys.Alt) != 0) mods |= ShortcutKeys.Alt;
            if ((m & ModifierKeys.Shift) != 0) mods |= ShortcutKeys.Shift;
            if ((m & ModifierKeys.Windows) != 0) mods |= ShortcutKeys.Win;
            var keys = new ShortcutKeys(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
            if (!keys.Usable)
            {
                row.SetNote($"{keys.Text} on its own would stop that key typing. Add Ctrl, Alt or the Windows key. Esc cancels.", warn: true);
                return;
            }
            // Pocket XR's own keys come back first, so the change is checked against what is really taken.
            listening = false;
            actions.PauseShortcuts(false);
            string? why = actions.ChangeShortcut(shortcut.Id, keys);
            Stop(why);
        }
    }
}
