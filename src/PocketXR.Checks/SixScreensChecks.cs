using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PocketXR.Core;

namespace PocketXR.Checks;

/// <summary>
/// 0.4.11: up to six floating screens and six PC screens (three up to 0.4.10), the default and anything with three or
/// fewer as before. Checked by sums only: nothing here makes a display or starts an Apollo.
/// </summary>
public static class SixScreensChecks
{
    public static void Run()
    {
        Counts();
        DriverLimits();
        Settings();
        MainDisplayPicker();
        Ports();
        SetupScriptPorts();
        Layouts();
    }

    private static void Counts()
    {
        Check.That(ScreenCount.Max == 6 && PcScreenLogic.Count == 6 && MainDisplay.Max == 6, "six screens everywhere: floating, PC screens, main display");
        Check.That(SpaceSettings.ScreensRange == (1, 6), "the Screens setting is 1 to 6");
        Check.That(new SpaceSettings().Screens == 2, "two floating screens at first, as before");
        Check.That(SpaceSettings.TryDecode("{\"screens\": 3}"u8.ToArray())?.Screens == 3, "a saved 3 stays 3");
        Check.That(new SpaceSettings { Screens = 6 }.Clamped.Screens == 6 && new SpaceSettings { Screens = 7 }.Clamped.Screens == 6
            && new SpaceSettings { Screens = 0 }.Clamped.Screens == 1, "6 kept, 7 brought to 6, 0 to 1");
        Check.That(SpaceSettings.Decode(new SpaceSettings { Screens = 5 }.Encode()).Screens == 5, "5 screens saved and read");
    }

    private static void DriverLimits()
    {
        Check.That(ScreenCount.DriverLimit("SudoVDA", null) == 10, "SudoVDA: 10 when maxMonitors is not set (its README)");
        Check.That(ScreenCount.DriverLimit("SudoVDA", 4) == 4, "SudoVDA: its maxMonitors when set");
        Check.That(ScreenCount.DriverLimit("SudoVDA", 0) == 10, "SudoVDA: a maxMonitors of 0 read as not set");
        Check.That(ScreenCount.DriverLimit("Parsec Virtual Display Adapter", null) == 8, "Parsec: parsec-vdd's 8");
        Check.That(ScreenCount.DriverLimit("Something else", 3) == null && ScreenCount.DriverLimit(null, null) == null, "another driver: not known");
        Check.That(ScreenCount.Offered(null) == 6 && ScreenCount.Offered(10) == 6 && ScreenCount.Offered(8) == 6, "six offered unless the driver makes fewer");
        Check.That(ScreenCount.Offered(4) == 4 && ScreenCount.Offered(0) == 1, "fewer offered when the driver makes fewer, never none");
        Check.That(ScreenCount.ToMake(6, 4) == 4 && ScreenCount.ToMake(2, 4) == 2 && ScreenCount.ToMake(9, null) == 6, "made: the setting, at most the driver's limit");
        string? words = ScreenCount.LimitWords(6, 4, "SudoVDA");
        Check.That(words != null && words.Contains("6 asked, 4 made") && words.Contains("maxMonitors"), "the log says why fewer were made");
        Check.That(ScreenCount.LimitWords(3, 10, "SudoVDA") == null && ScreenCount.LimitWords(6, null, null) == null, "nothing logged when all are made");
    }

    private static void Settings()
    {
        var s = new SpaceSettings();
        s.SetPortrait(5, true);
        Check.That(s.IsPortrait(5) && !s.IsPortrait(4), "screen 6 can be portrait");
        Check.That(SpaceSettings.Decode(s.Encode()).Clamped.IsPortrait(5), "screen 6 portrait saved and kept");
        s.SetPortrait(6, true);
        Check.That(!s.IsPortrait(6) && s.PortraitScreens == 1 << 5, "no seventh screen");
        Check.That(new SpaceSettings { PortraitScreens = 0b111 }.Clamped.PortraitScreens == 0b111, "a 0.4.10 file's portrait screens unchanged");
        Check.That(new SpaceSettings { PortraitScreens = 0xFFFF }.Clamped.PortraitScreens == 0b111111, "bits past screen 6 dropped");
        var moved = new SpaceSettings { Screens = 6 };
        moved.MoveScreen(5, 3, 1);
        Check.That(moved.ScreenYawDeg.Count == 6 && Check.Near(moved.ScreenYawDeg[5], 3) && Check.Near(moved.ScreenPitchDeg[5], 1), "screen 6 can be moved by hand");
        moved.MoveScreen(6, 3, 1);
        Check.That(moved.ScreenYawDeg.Count == 6, "a seventh is not");
    }

    private static void MainDisplayPicker()
    {
        Check.That(string.Join("|", MainDisplay.Titles(3)) == "This PC|Screen 1|Screen 2|Screen 3", "3 screens: the picker as in 0.4.10");
        Check.That(string.Join("|", MainDisplay.Titles(6)) == "This PC|1|2|3|4|5|6", "6 screens: numbers, so it fits");
        Check.That(MainDisplay.FloatingIndex(6, 6) == 5 && MainDisplay.FloatingIndex(6, 5) == -1, "Screen 6 main only when there are six");
        Check.That(MainDisplay.ChoiceNow(5, false) == 6 && MainDisplay.ChoiceNow(6, true) == 0, "screen 6 read back as main; a seventh is not one");
        Check.That(new SpaceSettings { MainScreen = 6 }.Clamped.MainScreen == 6, "Screen 6 kept as the main display");
    }

    private static void Ports()
    {
        var ports = PcScreenLogic.InstancePorts();
        Check.That(ports.SequenceEqual(new[] { 47989, 48989, 49989, 51989, 52989, 53989 }), "PC screens 1 to 6: 47989, 48989, 49989, then 51989 to 53989");
        Check.That(PcScreenLogic.InstancePorts(3).SequenceEqual(new[] { 47989, 48989, 49989 }), "PC screens 1 to 3 on their 0.4.10 ports");
        Check.That(PcScreenLogic.InstancePort(1) == PcScreenLogic.DefaultPort, "PC screen 1 is the main Apollo");
        var spans = ports.Select(PcScreenLogic.PortSpan).ToList();
        bool apart = true;
        for (int i = 0; i < spans.Count; i++)
            for (int j = i + 1; j < spans.Count; j++)
                if (PcScreenLogic.Overlaps(spans[i], spans[j])) apart = false;
        Check.That(apart, "no two PC screens' hosts share a port");
        var own = PcScreenLogic.PortSpan(PcScreenLogic.OwnHostPort);
        Check.That(PcScreenLogic.OwnHostPort == 50989 && spans.All(s => !PcScreenLogic.Overlaps(s, own)), "none shares a port with this app's own host (50984 to 51010)");
        Check.That(spans.All(s => PcScreenLogic.PhoneLinkPort < s.Low || PcScreenLogic.PhoneLinkPort > s.High), "none takes the phone link's 47610");
        Check.That(ports.All(p => p + 21 <= 65535), "all inside the port range");
        // PC screen 6 takes the next free extra instance, as 2 and 3 did.
        Check.That(PcScreenLogic.InstanceFor(5, ports, p => p != 51989 && p != 52989) == 48989, "PC screen 6: the first free extra");
        Check.That(PcScreenLogic.InstanceFor(0, ports, _ => true) == 47989, "PC screen 1 still the main one");
        Check.That(PcScreenLogic.PcName("MIKE screen 6") == "MIKE", "\"MIKE screen 6\" is MIKE's");
    }

    /// <summary>The setup script's table of ports (PocketXR-PC-Screens.ps1) is the app's.</summary>
    private static void SetupScriptPorts()
    {
        string? script = FindUp(Path.Combine("src", "PocketXR", "PcHost", "PocketXR-PC-Screens.ps1"));
        Check.That(script != null, "the PC screens setup script is found");
        if (script == null) return;
        string text = File.ReadAllText(script);
        var table = Regex.Matches(text, @"@\{\s*Number\s*=\s*(\d+);\s*Port\s*=\s*(\d+)\s*\}")
            .Select(m => (N: int.Parse(m.Groups[1].Value), Port: int.Parse(m.Groups[2].Value))).ToList();
        Check.That(table.Select(t => t.N).SequenceEqual(Enumerable.Range(2, 5)), "the script makes instances for PC screens 2 to 6");
        Check.That(table.All(t => t.Port == PcScreenLogic.InstancePort(t.N)), "each on the port the app expects");
        Check.That(Regex.IsMatch(text, @"\$MaxScreens\s*=\s*6\b") && Regex.IsMatch(text, @"\$DefaultScreens\s*=\s*3\b")
            && Regex.IsMatch(text, @"\$OwnHostPort\s*=\s*" + PcScreenLogic.OwnHostPort + @"\b"), "the script's most, first-time count and own host port");
    }

    private static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Layouts()
    {
        foreach (int n in new[] { 4, 5, 6 })
        {
            var aspects = Enumerable.Repeat(16.0 / 9, n).ToList();
            var choices = SpaceLayout.Choices(n);
            Check.That(choices.Length == 4, $"{n} screens: four layouts offered");
            Check.That(choices.Select(c => SpaceLayout.Title(c, n)).Distinct().Count() == 4, $"{n} screens: each layout its own name");
            foreach (ArrangeLayout layout in choices)
            {
                PanelPlacement[] p = SpaceLayout.Arrange(layout, aspects, 40, 1.4, 0);
                string what = $"{n} screens, {SpaceLayout.Title(layout, n)}";
                Check.That(p.Length == n, $"{what}: one place each");
                Check.That(!AnyOverlap(p), $"{what}: no two screens on top of each other");
                Check.That(p.All(q => Math.Abs(q.PitchDeg) + q.HeightDeg / 2 <= SpaceLayout.MaxPitchDeg + 1e-6), $"{what}: inside 35 degrees up and down");
            }
            PanelPlacement[] row = SpaceLayout.Arrange(ArrangeLayout.Row, aspects, 40, 1.4, 0);
            Check.That(row.All(q => Check.Near(q.WidthDeg, 40)) && Check.Near(SpaceLayout.MiddleYawDeg(row), 0), $"{n} across: full size, the middle ahead");
            PanelPlacement[] rows = SpaceLayout.Arrange(ArrangeLayout.Stacked, aspects, 40, 1.4, 0);
            Check.That(rows.Select(q => Math.Round(q.PitchDeg, 6)).Distinct().Count() == 2 && rows[0].PitchDeg > 0 && rows[n - 1].PitchDeg < 0,
                $"{n} stacked: two rows, the first screen on top");
            Check.That(Check.Near(SpaceLayout.Middle(rows).YawDeg, 0, 1e-9) || n % 2 == 1, $"{n} stacked: the rows centred");
            PanelPlacement[] main = SpaceLayout.Arrange(ArrangeLayout.MainSide, aspects, 40, 1.4, 0);
            Check.That(Check.Near(main[0].YawDeg, 0) && main[1].YawDeg < 0 && main[n - 1].YawDeg > 0, $"{n} main + sides: main ahead, screen 2 right, the last left");
            PanelPlacement[] theater = SpaceLayout.Arrange(ArrangeLayout.Theater, aspects, 40, 1.4, 0);
            Check.That(Check.Near(theater[0].YawDeg, 0) && theater[1].YawDeg > 0 && theater[2].YawDeg < 0 && Math.Abs(theater[3].YawDeg) > Math.Abs(theater[1].YawDeg),
                $"{n} theater: flanks left, right, then further out");
        }
        Check.That(SpaceLayout.Title(ArrangeLayout.Row, 6) == "Six across" && SpaceLayout.Title(ArrangeLayout.Stacked, 4) == "Two rows"
            && SpaceLayout.Title(ArrangeLayout.Stacked, 3) == "Stacked" && SpaceLayout.Title(ArrangeLayout.MainSide, 3) == "Main + two stacked",
            "names for 4 to 6, and 3's as before");
        Check.That(SpaceLayout.Arrange(ArrangeLayout.Theater, Enumerable.Repeat(16.0 / 9, 7).ToList(), 40, 1.4, 0).All(q => q.Distance == 1.4),
            "seven screens (never asked): a plain row");
    }

    /// <summary>Whether two screens' angular rectangles cross (a hair's overlap at the edges allowed).</summary>
    private static bool AnyOverlap(IReadOnlyList<PanelPlacement> p)
    {
        for (int i = 0; i < p.Count; i++)
            for (int j = i + 1; j < p.Count; j++)
            {
                double yaw = Math.Min(p[i].YawDeg + p[i].WidthDeg / 2, p[j].YawDeg + p[j].WidthDeg / 2)
                    - Math.Max(p[i].YawDeg - p[i].WidthDeg / 2, p[j].YawDeg - p[j].WidthDeg / 2);
                double pitch = Math.Min(p[i].PitchDeg + p[i].HeightDeg / 2, p[j].PitchDeg + p[j].HeightDeg / 2)
                    - Math.Max(p[i].PitchDeg - p[i].HeightDeg / 2, p[j].PitchDeg - p[j].HeightDeg / 2);
                if (yaw > 0.01 && pitch > 0.01) return true;
            }
        return false;
    }
}
