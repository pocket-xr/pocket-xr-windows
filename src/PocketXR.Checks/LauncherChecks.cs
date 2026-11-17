using System.Collections.Generic;
using System.Linq;
using PocketXR.Core;

namespace PocketXR.Checks;

public static class LauncherChecks
{
    /// <summary>A list as Swift prints it, for the messages.</summary>
    private static string Shown(IEnumerable<string> list) => "[" + string.Join(", ", list.Select(s => "\"" + s + "\"")) + "]";

    public static void Run()
    {
        // Search: case and accents do not matter; starts of names first, then starts of words, then anywhere.
        string[] names = { "App Store", "Books", "Café Notes", "Maps", "Notes", "Photo Booth", "Safari", "System Settings", "TextEdit" };
        Check.That(LauncherSearch.Rank(name: "Safari", query: "SAF") == 0, "case does not matter");
        Check.That(LauncherSearch.Rank(name: "Café Notes", query: "cafe") == 0, "accents do not matter");
        Check.That(LauncherSearch.Rank(name: "Cafe", query: "café") == 0, "accents typed do not matter either");
        Check.That(LauncherSearch.Rank(name: "System Settings", query: "set") == 1, "a later word's start");
        Check.That(LauncherSearch.Rank(name: "System Settings", query: "sys set") == 1, "two words' starts");
        Check.That(LauncherSearch.Rank(name: "TextEdit", query: "edit") == 2, "anywhere in the name");
        Check.That(LauncherSearch.Rank(name: "Photo Booth", query: "oto oth") == 3, "every typed word somewhere");
        Check.That(LauncherSearch.Rank(name: "Maps", query: "x") == null, "no match");
        Check.That(LauncherSearch.Rank(name: "Maps", query: "  ") == 0, "only spaces is no search");
        string[] found = LauncherSearch.Results(names: names, query: "no").Select(i => names[i]).ToArray();
        Check.That(found.SequenceEqual(new[] { "Notes", "Café Notes" }), $"starts of names before starts of words: {Shown(found)}");
        string[] o = LauncherSearch.Results(names: names, query: "o").Select(i => names[i]).ToArray();
        Check.That(o.SequenceEqual(new[] { "App Store", "Books", "Café Notes", "Notes", "Photo Booth" }), $"anywhere keeps the order given: {Shown(o)}");
        Check.That(LauncherSearch.Results(names: names, query: "").SequenceEqual(Enumerable.Range(0, names.Length)), "an empty search is everything");
        Check.That(LauncherSearch.Results(names: names, query: "zzz").Length == 0, "nothing found");

        // Typing.
        Check.That(LauncherSearch.Typed("", adding: " a") == "a", "no space at the start");
        Check.That(LauncherSearch.Typed("a", adding: " b") == "a b", "a space inside is kept");
        Check.That(LauncherSearch.Typed("a", adding: "\r\t\u007F") == "a", "control and arrow keys add nothing");
        string @long = LauncherSearch.Typed(new string('x', 39), adding: "abc");
        Check.That(@long.Length == LauncherSearch.MaxQuery && @long.EndsWith("a", System.StringComparison.Ordinal), "40 characters at most");
        Check.That(LauncherSearch.Erased("abc") == "ab" && LauncherSearch.Erased("") == "", "Backspace");

        // Pages.
        Check.That(LauncherPaging.PageCount(items: 0, perPage: 18) == 1, "no apps is still one page");
        Check.That(LauncherPaging.PageCount(items: 18, perPage: 18) == 1, "a full page is one page");
        Check.That(LauncherPaging.PageCount(items: 19, perPage: 18) == 2, "one more makes two");
        Check.That(LauncherPaging.Range(page: 1, items: 30, perPage: 18) == (18, 30), "the last page is short");
        Check.That(LauncherPaging.Range(page: 9, items: 30, perPage: 18) == (18, 30), "a page too far is the last");
        (int LowerBound, int UpperBound) empty = LauncherPaging.Range(page: 0, items: 0, perPage: 18);
        Check.That(empty.LowerBound >= empty.UpperBound, "an empty page");
        Check.That(LauncherPaging.Clamped(page: -3, items: 30, perPage: 18) == 0, "a page before the first is the first");
        Check.That(LauncherPaging.Page(ofItem: 17, perPage: 18) == 0 && LauncherPaging.Page(ofItem: 18, perPage: 18) == 1, "an item's page");

        // Arrow keys: 4 pins over a 6 x 3 grid of 30 apps (two pages; the second has 12).
        var g = new LauncherGrid(pins: 4, items: 30, columns: 6, rows: 3);
        Check.That(g.PerPage == 18 && g.Pages == 2, "grid pages");
        Check.That(g.Move(null, LauncherMove.Right, page: 0) == (null, 1), "with nothing selected, right turns the page");
        Check.That(g.Move(null, LauncherMove.Right, page: 1) == (null, 1), "and stops at the last");
        Check.That(g.Move(null, LauncherMove.Left, page: 0) == (null, 0), "left stops at the first");
        Check.That(g.Move(null, LauncherMove.Down, page: 1) == (LauncherSpot.Item(18), 1), "down picks the page's first app");
        Check.That(g.Move(null, LauncherMove.Up, page: 0) == (LauncherSpot.Pin(0), 0), "up picks the first pin");
        Check.That(g.Move(LauncherSpot.Item(0), LauncherMove.Right, page: 0) == (LauncherSpot.Item(1), 0), "right");
        Check.That(g.Move(LauncherSpot.Item(5), LauncherMove.Right, page: 0) == (LauncherSpot.Item(18), 1), "right at the edge goes to the next page, same row");
        Check.That(g.Move(LauncherSpot.Item(17), LauncherMove.Right, page: 0) == (LauncherSpot.Item(29), 1), "onto a row the next page lacks: its last app");
        Check.That(g.Move(LauncherSpot.Item(18), LauncherMove.Left, page: 1) == (LauncherSpot.Item(5), 0), "left at the edge goes back a page");
        Check.That(g.Move(LauncherSpot.Item(0), LauncherMove.Left, page: 0) == (LauncherSpot.Item(0), 0), "left at the very start stays");
        Check.That(g.Move(LauncherSpot.Item(29), LauncherMove.Right, page: 1) == (LauncherSpot.Item(29), 1), "right at the very end stays");
        Check.That(g.Move(LauncherSpot.Item(7), LauncherMove.Up, page: 0) == (LauncherSpot.Item(1), 0), "up a row");
        Check.That(g.Move(LauncherSpot.Item(0), LauncherMove.Up, page: 0) == (LauncherSpot.Pin(0), 0), "up from the top row to the pins");
        Check.That(g.Move(LauncherSpot.Item(5), LauncherMove.Up, page: 0) == (LauncherSpot.Pin(3), 0), "the nearest pin above");
        Check.That(g.Move(LauncherSpot.Pin(3), LauncherMove.Down, page: 0) == (LauncherSpot.Item(5), 0), "down from a pin to the app under it");
        Check.That(g.Move(LauncherSpot.Pin(0), LauncherMove.Left, page: 0) == (LauncherSpot.Pin(0), 0) && g.Move(LauncherSpot.Pin(3), LauncherMove.Right, page: 0) == (LauncherSpot.Pin(3), 0), "pins stop at their ends");
        Check.That(g.Move(LauncherSpot.Item(12), LauncherMove.Down, page: 0) == (LauncherSpot.Item(12), 0), "down from the bottom row stays");
        var @short = new LauncherGrid(pins: 0, items: 10, columns: 6, rows: 3);
        Check.That(@short.Move(LauncherSpot.Item(5), LauncherMove.Down, page: 0) == (LauncherSpot.Item(9), 0), "down onto a short last row goes to its end");
        Check.That(@short.Move(LauncherSpot.Item(2), LauncherMove.Up, page: 0) == (LauncherSpot.Item(2), 0), "no pins: up from the top row stays");
        Check.That(@short.Move(LauncherSpot.Item(9), LauncherMove.Right, page: 0) == (LauncherSpot.Item(9), 0), "right at the last app stays");
        var none = new LauncherGrid(pins: 0, items: 0, columns: 6, rows: 3);
        Check.That(none.Move(null, LauncherMove.Down, page: 0) == (null, 0) && none.Move(LauncherSpot.Item(3), LauncherMove.Left, page: 2) == (null, 0), "nothing to select");

        // Pins.
        string[] ten = Enumerable.Range(1, 10).Select(i => $"/Applications/A{i}.app").ToArray();
        Check.That(LauncherPins.Cleaned(new[] { "/a", "", "/b", "/a" }, limit: 10).SequenceEqual(new[] { "/a", "/b" }), "no repeats, no empty paths");
        Check.That(LauncherPins.Cleaned(ten.Append("/x").ToArray(), limit: 10).SequenceEqual(ten), "ten at most");
        var add = LauncherPins.Adding("/c", to: new[] { "/a", "/b" }, limit: 10);
        Check.That(add.Result == LauncherPins.Added.Added && add.Pins.SequenceEqual(new[] { "/a", "/b", "/c" }), "a pin goes at the end");
        var again = LauncherPins.Adding("/a", to: new[] { "/a", "/b" }, limit: 10);
        Check.That(again.Result == LauncherPins.Added.Already && again.Pins.SequenceEqual(new[] { "/a", "/b" }), "pinned already");
        var full = LauncherPins.Adding("/x", to: ten, limit: 10);
        Check.That(full.Result == LauncherPins.Added.Full && full.Pins.SequenceEqual(ten), "an eleventh is refused");
        Check.That(LauncherPins.Removing("/a", from: new[] { "/a", "/b" }).SequenceEqual(new[] { "/b" }), "unpin");
        Check.That(LauncherPins.Removing("/z", from: new[] { "/a", "/b" }).SequenceEqual(new[] { "/a", "/b" }), "unpin what is not pinned");
        Check.That(LauncherPins.Shown(new[] { "/a", "/gone", "/b" }, limit: 10, exists: p => p != "/gone").SequenceEqual(new[] { "/a", "/b" }), "pins whose app is gone are not shown");

        // The first run's pins.
        string[] all = LauncherPins.Defaults(exists: _ => true);
        Check.That(all.SequenceEqual(new[] { "/Applications/Safari.app", "/Applications/Mail.app", "/Applications/Notes.app", "/Applications/Music.app" }), $"the first four: {Shown(all)}");
        var mac = new HashSet<string>
        {
            "/Applications/Safari.app", "/System/Applications/Mail.app", "/System/Applications/Maps.app",
            "/System/Applications/Utilities/Terminal.app", "/System/Applications/System Settings.app",
        };
        string[] some = LauncherPins.Defaults(exists: p => mac.Contains(p));
        Check.That(some.SequenceEqual(new[] { "/Applications/Safari.app", "/System/Applications/Mail.app", "/System/Applications/Maps.app", "/System/Applications/Utilities/Terminal.app" }), $"those that exist, in order: {Shown(some)}");
        Check.That(LauncherPins.Defaults(exists: _ => false).Length == 0, "none there, none pinned");
    }
}
