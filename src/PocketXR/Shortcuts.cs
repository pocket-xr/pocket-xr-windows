using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PocketXR.Core;

namespace PocketXR;

/// <summary>
/// The keys of one shortcut: modifiers as Windows' RegisterHotKey takes them (1 Alt, 2 Ctrl, 4 Shift, 8 the Windows
/// key) and a virtual key.
/// </summary>
public readonly record struct ShortcutKeys(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>Ctrl+Alt and a key: every shortcut Pocket XR comes with.</summary>
    public static ShortcutKeys CtrlAlt(uint vk) => new(Ctrl | Alt, vk);

    /// <summary>At least Ctrl, Alt or the Windows key: a plain key, or Shift and a key, would stop that key typing.</summary>
    public bool Usable => (Modifiers & (Ctrl | Alt | Win)) != 0 && VirtualKey != 0;

    /// <summary>As shown, such as "Ctrl+Alt+R".</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Win) != 0) parts.Add("Win");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    /// <summary>A key's name as it is printed on the key.</summary>
    public static string KeyName(uint vk) => vk switch
    {
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0xBB => "=",
        0xBD => "-",
        0xDB => "[",
        0xDD => "]",
        0xBE => ".",
        0xBC => ",",
        0xBA => ";",
        0xDE => "'",
        0xBF => "/",
        0xDC => "\\",
        0xC0 => "`",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => $"key {vk:X2}",
    };
}

/// <summary>
/// The keys chosen for Pocket XR's shortcuts, where they differ from the ones it comes with. Kept in their own file
/// (%LOCALAPPDATA%\Pocket XR\shortcuts.json), apart from the settings Pocket XR for Mac shares: Windows keys mean nothing
/// on a Mac.
/// </summary>
public static class ShortcutChoices
{
    private static Dictionary<string, ShortcutKeys> chosen = new();
    /// <summary>False while windows are drawn to pictures: they show the keys Pocket XR comes with.</summary>
    public static bool Persist = true;

    private static string File => Path.Combine(Log.Shared.Folder, "shortcuts.json");

    public static void Load()
    {
        chosen = new();
        if (!Persist) return;
        try
        {
            if (!System.IO.File.Exists(File)) return;
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                var k = new ShortcutKeys(p.Value.GetProperty("modifiers").GetUInt32(), p.Value.GetProperty("key").GetUInt32());
                if (k.Usable && Array.Exists(Shortcut.Defaults, s => s.Id == p.Name)) chosen[p.Name] = k;
            }
        }
        catch (Exception e)
        {
            Log.P($"shortcuts: {Path.GetFileName(File)} could not be read, the usual keys are used: {e.Message}");
            chosen = new();
        }
    }

    private static void Save()
    {
        if (!Persist) return;
        try
        {
            var data = chosen.ToDictionary(kv => kv.Key, kv => new Dictionary<string, uint> { ["modifiers"] = kv.Value.Modifiers, ["key"] = kv.Value.VirtualKey });
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.P($"shortcuts: not saved: {e.Message}");
        }
    }

    public static ShortcutKeys? Get(string id) => chosen.TryGetValue(id, out ShortcutKeys k) ? k : null;

    public static void Set(string id, ShortcutKeys keys)
    {
        ShortcutKeys usual = Array.Find(Shortcut.Defaults, s => s.Id == id)!.Usual;
        if (keys == usual) chosen.Remove(id); else chosen[id] = keys;
        Save();
    }

    public static void Reset(string id)
    {
        if (chosen.Remove(id)) Save();
    }

    public static void ResetAll()
    {
        chosen.Clear();
        Save();
    }

    public static bool AnyChosen => chosen.Count > 0;
}
