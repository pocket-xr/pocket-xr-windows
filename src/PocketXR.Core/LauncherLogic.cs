using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PocketXR.Core;

// The launcher's logic that needs no screen: finding apps by name as the user types, the pages of the grid, moving
// the selection with the arrow keys, and the list of pinned apps.

/// <summary>
/// Search: by name, whatever the case or the accents; names that start with what was typed come first, then names
/// with a later word that starts with it, then names that have it anywhere.
/// </summary>
public static class LauncherSearch
{
    /// <summary>The longest search that can be typed.</summary>
    public const int MaxQuery = 40;

    /// <summary>A name or a search without case and accents, for comparing: "Café" and "cafe" are the same.</summary>
    public static string Folded(string text)
    {
        // Width: the full-width and half-width forms become the ordinary ones.
        var wide = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '　') wide.Append(' ');
            else if (c >= '＀' && c <= '￯') wide.Append(NormalizedOrSame(c.ToString(), NormalizationForm.FormKC));
            else wide.Append(c);
        }
        // Accents: taken apart, the marks dropped, put together again.
        string apart = NormalizedOrSame(wide.ToString(), NormalizationForm.FormD);
        var plain = new StringBuilder(apart.Length);
        foreach (char c in apart)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        }
        string folded = NormalizedOrSame(plain.ToString().ToLowerInvariant(), NormalizationForm.FormC);
        // Swift's .whitespaces: spaces of every kind and the tab, not line ends.
        int start = 0;
        int end = folded.Length;
        while (start < end && IsSpaceOrTab(folded[start])) start++;
        while (end > start && IsSpaceOrTab(folded[end - 1])) end--;
        return folded.Substring(start, end - start);
    }

    private static bool IsSpaceOrTab(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Text that is not valid Unicode (half a surrogate pair in a file name) is left as it is.</summary>
    private static string NormalizedOrSame(string text, NormalizationForm form)
    {
        try { return text.Normalize(form); } catch (ArgumentException) { return text; }
    }

    /// <summary>The words of a folded name: runs of letters and digits.</summary>
    internal static List<string> Words(string folded)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        TextElementEnumerator characters = StringInfo.GetTextElementEnumerator(folded);
        while (characters.MoveNext())
        {
            string ch = characters.GetTextElement();
            Rune first = FirstRune(ch);
            if (Rune.IsLetter(first) || Rune.IsNumber(first))
            {
                word.Append(ch);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    private static Rune FirstRune(string character)
    {
        foreach (Rune r in character.EnumerateRunes()) return r;
        return Rune.ReplacementChar;
    }

    /// <summary>
    /// How well [name] answers [query], lower is better: 0 the name starts with it, 1 its words start with what was
    /// typed ("sys set" finds System Settings), 2 it is somewhere in the name, 3 every typed word is somewhere in
    /// the name. Null when it does not match. An empty query matches everything at 0.
    /// </summary>
    public static int? Rank(string name, string query)
    {
        string q = Folded(query);
        if (q.Length == 0) return 0;
        string n = Folded(name);
        if (n.StartsWith(q, StringComparison.Ordinal)) return 0;
        string[] typed = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> nameWords = Words(n);
        bool everyWordStarts = typed.All(t => nameWords.Any(w => w.StartsWith(t, StringComparison.Ordinal)));
        if (everyWordStarts) return 1;
        if (n.Contains(q, StringComparison.Ordinal)) return 2;
        if (typed.Length > 1 && typed.All(t => n.Contains(t, StringComparison.Ordinal))) return 3;
        return null;
    }

    /// <summary>The places in [names] that match [query], best first; names that match equally well keep the order given.</summary>
    public static int[] Results(IReadOnlyList<string> names, string query)
    {
        if (Folded(query).Length == 0) return Enumerable.Range(0, names.Count).ToArray();
        var ranked = new List<(int Rank, int Index)>();
        for (int i = 0; i < names.Count; i++)
        {
            int? r = Rank(names[i], query);
            if (r is int rank) ranked.Add((rank, i));
        }
        ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
        return ranked.Select(r => r.Index).ToArray();
    }

    /// <summary>[query] after [adding] was typed: no control keys, no space at the start, never longer than [MaxQuery].</summary>
    public static string Typed(string query, string adding)
    {
        var result = new StringBuilder(query);
        TextElementEnumerator characters = StringInfo.GetTextElementEnumerator(adding);
        while (characters.MoveNext())
        {
            string ch = characters.GetTextElement();
            if (new StringInfo(result.ToString()).LengthInTextElements >= MaxQuery) break;
            bool printable = true;
            foreach (Rune s in ch.EnumerateRunes())
            {
                // 0xF700...0xF8FF is where the Mac's AppKit puts the arrow and function keys.
                if (!(s.Value >= 0x20 && s.Value != 0x7F && !(s.Value >= 0xF700 && s.Value <= 0xF8FF))) printable = false;
            }
            Rune first = FirstRune(ch);
            if (!printable || IsNewline(first)) continue;
            if (result.Length == 0 && Rune.IsWhiteSpace(first)) continue;
            result.Append(ch);
        }
        return result.ToString();
    }

    private static bool IsNewline(Rune r) =>
        r.Value == 0x0A || r.Value == 0x0B || r.Value == 0x0C || r.Value == 0x0D || r.Value == 0x85
        || r.Value == 0x2028 || r.Value == 0x2029;

    /// <summary>[query] with its last character taken off (Backspace).</summary>
    public static string Erased(string query)
    {
        int[] starts = StringInfo.ParseCombiningCharacters(query);
        return starts.Length == 0 ? "" : query.Substring(0, starts[^1]);
    }
}

/// <summary>Pages of a grid.</summary>
public static class LauncherPaging
{
    /// <summary>How many pages [items] need, one at least (an empty page is still a page).</summary>
    public static int PageCount(int items, int perPage)
    {
        if (!(perPage > 0 && items > 0)) return 1;
        return (items + perPage - 1) / perPage;
    }

    public static int Clamped(int page, int items, int perPage) =>
        Math.Min(Math.Max(page, 0), PageCount(items, perPage) - 1);

    /// <summary>Which items are on [page]: from LowerBound up to, not including, UpperBound.</summary>
    public static (int LowerBound, int UpperBound) Range(int page, int items, int perPage)
    {
        if (!(perPage > 0 && items > 0)) return (0, 0);
        int p = Clamped(page, items, perPage);
        return (p * perPage, Math.Min((p + 1) * perPage, items));
    }

    /// <summary>The page [ofItem] is on.</summary>
    public static int Page(int ofItem, int perPage) => perPage > 0 ? Math.Max(ofItem, 0) / perPage : 0;
}

public enum LauncherSpotKind
{
    Pin,
    Item,
}

/// <summary>What is selected: a pinned app by its place in the row, or an app in the grid by its place in the whole list.</summary>
public readonly record struct LauncherSpot
{
    public LauncherSpotKind Kind { get; }
    /// <summary>The place: in the row of pins for a pin, in the whole list for an item.</summary>
    public int Value { get; }

    private LauncherSpot(LauncherSpotKind kind, int value) { Kind = kind; Value = value; }

    public static LauncherSpot Pin(int value) => new(LauncherSpotKind.Pin, value);
    public static LauncherSpot Item(int value) => new(LauncherSpotKind.Item, value);
}

public enum LauncherMove
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>The shape of what the arrow keys move over: a row of pins above a paged grid.</summary>
public record struct LauncherGrid
{
    public int Pins;
    public int Items;
    public int Columns;
    public int Rows;

    public LauncherGrid(int pins, int items, int columns, int rows)
    {
        Pins = Math.Max(pins, 0);
        Items = Math.Max(items, 0);
        Columns = Math.Max(columns, 1);
        Rows = Math.Max(rows, 1);
    }

    public readonly int PerPage => Columns * Rows;
    public readonly int Pages => LauncherPaging.PageCount(Items, PerPage);

    /// <summary>
    /// The selection and the page after an arrow key. With nothing selected, left and right turn the page, and up
    /// or down pick the first thing there is. Left at the first column goes to the page before, right at the last
    /// column to the page after; up from the top row goes to the pins, down from the pins to the grid.
    /// </summary>
    public readonly (LauncherSpot? Spot, int Page) Move(LauncherSpot? spot, LauncherMove move, int page)
    {
        page = LauncherPaging.Clamped(page, Items, PerPage);
        if (spot is not LauncherSpot chosen)
        {
            switch (move)
            {
                case LauncherMove.Left: return (null, Math.Max(page - 1, 0));
                case LauncherMove.Right: return (null, Math.Min(page + 1, Pages - 1));
                case LauncherMove.Down:
                    if (Items > 0) return (LauncherSpot.Item(page * PerPage), page);
                    return (Pins > 0 ? LauncherSpot.Pin(0) : (LauncherSpot?)null, page);
                default:
                    if (Pins > 0) return (LauncherSpot.Pin(0), page);
                    return (Items > 0 ? LauncherSpot.Item(page * PerPage) : (LauncherSpot?)null, page);
            }
        }
        if (chosen.Kind == LauncherSpotKind.Pin)
        {
            if (!(Pins > 0)) return Move(null, move, page);
            int i = Math.Min(Math.Max(chosen.Value, 0), Pins - 1);
            switch (move)
            {
                case LauncherMove.Left: return (LauncherSpot.Pin(Math.Max(i - 1, 0)), page);
                case LauncherMove.Right: return (LauncherSpot.Pin(Math.Min(i + 1, Pins - 1)), page);
                case LauncherMove.Up: return (LauncherSpot.Pin(i), page);
                default:
                    (int LowerBound, int UpperBound) onPage = LauncherPaging.Range(page, Items, PerPage);
                    if (onPage.LowerBound >= onPage.UpperBound) return (LauncherSpot.Pin(i), page);
                    int column = Across(i, Pins, Columns);
                    return (LauncherSpot.Item(Math.Min(onPage.LowerBound + column, onPage.UpperBound - 1)), page);
            }
        }
        else
        {
            if (!(Items > 0)) return Move(null, move, page);
            int k = Math.Min(Math.Max(chosen.Value, 0), Items - 1);
            int p = k / PerPage;
            int local = k % PerPage;
            int column = local % Columns;
            int row = local / Columns;
            int pageEnd = Math.Min((p + 1) * PerPage, Items);
            switch (move)
            {
                case LauncherMove.Left:
                    if (column > 0) return (LauncherSpot.Item(k - 1), p);
                    if (p > 0) return (LauncherSpot.Item((p - 1) * PerPage + row * Columns + Columns - 1), p - 1);
                    return (LauncherSpot.Item(k), p);
                case LauncherMove.Right:
                    if (column < Columns - 1)
                    {
                        return (LauncherSpot.Item(k + 1 < pageEnd ? k + 1 : k), p);
                    }
                    if (p < Pages - 1) return (LauncherSpot.Item(Math.Min((p + 1) * PerPage + row * Columns, Items - 1)), p + 1);
                    return (LauncherSpot.Item(k), p);
                case LauncherMove.Up:
                    if (row > 0) return (LauncherSpot.Item(k - Columns), p);
                    if (Pins > 0) return (LauncherSpot.Pin(Across(column, Columns, Pins)), p);
                    return (LauncherSpot.Item(k), p);
                default:
                    if (k + Columns < pageEnd) return (LauncherSpot.Item(k + Columns), p);
                    // A shorter last row: go to its end rather than nowhere.
                    int lastRow = (pageEnd - 1 - p * PerPage) / Columns;
                    if (row < lastRow) return (LauncherSpot.Item(pageEnd - 1), p);
                    return (LauncherSpot.Item(k), p);
            }
        }
    }

    /// <summary>
    /// The place among [to] things that sits nearest under (or over) place [i] among [of] things, both rows
    /// spread over the same width.
    /// </summary>
    internal static int Across(int i, int of, int to)
    {
        if (!(of > 0 && to > 0)) return 0;
        double middle = ((double)i + 0.5) / (double)of;
        return Math.Min(Math.Max((int)Math.Floor(middle * (double)to), 0), to - 1);
    }
}

/// <summary>The pinned apps: paths to .app bundles, in the order they were pinned, no path twice, never more than the limit.</summary>
public static class LauncherPins
{
    public enum Added
    {
        Added,
        Already,
        Full,
    }

    /// <summary>The apps offered as pins on the first run, in this order; the first four that are there are taken.</summary>
    public static readonly string[] DefaultNames =
    {
        "Safari", "Mail", "Notes", "Music", "Photos", "Maps", "Terminal", "System Settings",
    };

    /// <summary>Where the Mac's own apps live. Terminal is in Utilities.</summary>
    public static readonly string[] DefaultFolders =
    {
        "/Applications", "/System/Applications", "/System/Applications/Utilities",
    };

    public const int DefaultCount = 4;

    /// <summary>[pins] without empty paths and repeats (the first is kept), cut to [limit].</summary>
    public static string[] Cleaned(IReadOnlyList<string> pins, int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();
        foreach (string p in pins)
        {
            if (p.Length == 0) continue;
            if (cleaned.Count >= Math.Max(limit, 0)) break;
            if (seen.Add(p)) cleaned.Add(p);
        }
        return cleaned.ToArray();
    }

    /// <summary>Pins [path] at the end. Already when it is pinned, Full when [limit] apps are; the list is unchanged then.</summary>
    public static (string[] Pins, Added Result) Adding(string path, IReadOnlyList<string> to, int limit)
    {
        string[] tidy = Cleaned(to, limit);
        if (path.Length == 0 || Array.IndexOf(tidy, path) >= 0) return (tidy, Added.Already);
        if (tidy.Length >= limit) return (tidy, Added.Full);
        return (tidy.Append(path).ToArray(), Added.Added);
    }

    public static string[] Removing(string path, IReadOnlyList<string> @from) =>
        @from.Where(p => p != path).ToArray();

    /// <summary>
    /// The pins to show: those whose app is still there. The setting itself is left alone, so an app on a drive
    /// that is not plugged in comes back with the drive.
    /// </summary>
    public static string[] Shown(IReadOnlyList<string> pins, int limit, Func<string, bool> exists) =>
        Cleaned(pins, limit).Where(exists).ToArray();

    /// <summary>The first run's pins: up to [DefaultCount] of [DefaultNames] that exist, each looked for in [DefaultFolders].</summary>
    public static string[] Defaults(Func<string, bool> exists) => Defaults(DefaultCount, exists);

    /// <summary>The first run's pins: up to [count] of [DefaultNames] that exist, each looked for in [DefaultFolders].</summary>
    public static string[] Defaults(int count, Func<string, bool> exists)
    {
        var found = new List<string>();
        foreach (string name in DefaultNames)
        {
            if (found.Count >= count) break;
            string? first = DefaultFolders.Select(folder => $"{folder}/{name}.app").FirstOrDefault(exists);
            if (first != null) found.Add(first);
        }
        return found.ToArray();
    }
}
