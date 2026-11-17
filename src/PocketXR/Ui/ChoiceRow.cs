using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PocketXR.Core;

namespace PocketXR.Ui;

/// <summary>
/// One of a few choices, as a row of a card: a ring that fills with the accent when chosen, the name, and a line under
/// it saying what the choice means. Its words can change with the state (they are asked for on every Sync).
/// </summary>
public sealed class ChoiceRow : Press
{
    private readonly Func<bool> selected;
    private readonly Func<string> title, note;
    private readonly TextBlock titleText, noteText;
    private readonly Ellipse rim, fill, ring;
    private readonly Border hover;

    public ChoiceRow(Binder binder, Func<string> title, Func<string> note, Func<bool> selected, Action pick, double pad = 10)
    {
        this.selected = selected;
        this.title = title;
        this.note = note;
        Kind = AutomationControlType.RadioButton;
        Toggled = selected;
        DimOnPress = false;

        rim = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1.5 };
        fill = new Ellipse { Width = 9, Height = 9, Fill = Theme.Accent };
        ring = new Ellipse { Width = 24, Height = 24, StrokeThickness = 2, Stroke = Theme.Accent, Opacity = 0 };
        var mark = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(rim);
        mark.Children.Add(fill);
        mark.Children.Add(ring);

        titleText = Kit.Line("");
        noteText = Kit.Small("");
        noteText.Margin = new Thickness(0, 2, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(titleText);
        words.Children.Add(noteText);

        var row = new Grid { Margin = new Thickness(Theme.Inset, pad, Theme.Inset, pad), MinHeight = Theme.RowHeight - 2 * pad };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(mark);
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        // The light under the pointer stays inside the card's round corners.
        hover = new Border { CornerRadius = new CornerRadius(Theme.ControlRadius), Background = Theme.White(0.04), Margin = new Thickness(6, 2, 6, 2), Opacity = 0 };
        var g = new Grid();
        g.Children.Add(hover);
        g.Children.Add(row);
        Child = g;
        Click += () => { if (!selected()) pick(); };
        binder.Add(Restyle);
    }

    public ChoiceRow(Binder binder, string title, string note, Func<bool> selected, Action pick, double pad = 10)
        : this(binder, () => title, () => note, selected, pick, pad)
    {
    }

    protected override void Restyle()
    {
        bool on = selected();
        Label = title();
        titleText.Text = Label;
        titleText.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
        string words = note();
        noteText.Text = words;
        noteText.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        rim.Stroke = on ? Theme.Accent : Hover ? Theme.RimFocus : Theme.White(0.24);
        fill.Opacity = on ? 1 : 0;
        ring.Opacity = Ring ? 1 : 0;
        hover.Opacity = Hover && !on ? 1 : 0;
    }
}
