using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// The PC's USB devices, from Windows' device tree (cfgmgr32): vendor, product, interface classes and name, for
/// Glasses.Detect. Only reads; needs no administrator rights.
/// </summary>
public static class UsbDevices
{
    private static readonly Regex Id = new(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(&MI_[0-9A-F]{2})?\\", RegexOptions.IgnoreCase);
    private static readonly Regex ClassOf = new(@"USB\\Class_([0-9A-F]{2})", RegexOptions.IgnoreCase);
    private const uint FilterEnumerator = 1, FilterPresent = 0x100;
    private const uint DrpCompatibleIds = 3;

    public static List<UsbDeviceInfo> List()
    {
        var found = new List<UsbDeviceInfo>();
        uint flags = FilterEnumerator | FilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out uint size, "USB", flags) != 0 || size == 0) return found;
        var buffer = new char[size];
        if (CM_Get_Device_ID_ListW("USB", buffer, size, flags) != 0) return found;
        var names = new Dictionary<(int, int), string>();
        var classes = new Dictionary<(int, int), List<int>>();
        var order = new List<(int, int)>();
        foreach (string id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = Id.Match(id);
            if (!m.Success) continue;
            var key = (int.Parse(m.Groups[1].Value, NumberStyles.HexNumber), int.Parse(m.Groups[2].Value, NumberStyles.HexNumber));
            if (!classes.ContainsKey(key))
            {
                classes[key] = new List<int>();
                order.Add(key);
            }
            if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) continue;
            // An interface's class is in its compatible ids ("USB\Class_02&SubClass_0d&Prot_00"); a device with one
            // interface has it on the device itself.
            foreach (Match c in ClassOf.Matches(CompatibleIds(node)))
            {
                int cls = int.Parse(c.Groups[1].Value, NumberStyles.HexNumber);
                if (!classes[key].Contains(cls)) classes[key].Add(cls);
            }
            if (!m.Groups[3].Success && !names.ContainsKey(key))
            {
                string name = BusName(node);
                if (name.Length > 0) names[key] = name;
            }
        }
        foreach (var key in order)
            found.Add(new UsbDeviceInfo(vendor: key.Item1, product: key.Item2, interfaceClasses: classes[key].ToArray(), name: names.GetValueOrDefault(key, "")));
        return found;
    }

    /// <summary>One line per USB device, for the log.</summary>
    public static string Describe(IEnumerable<UsbDeviceInfo> list)
    {
        string text = string.Join("; ", list.Select(d =>
            $"{d.Vendor:X4}:{d.Product:X4} \"{d.Name}\" classes {string.Join(",", d.InterfaceClasses.Select(c => c.ToString("X2")))}"));
        return text.Length == 0 ? "none" : text;
    }

    private static string CompatibleIds(uint node)
    {
        uint length = 2048;
        var buffer = new char[length / 2];
        if (CM_Get_DevNode_Registry_PropertyW(node, DrpCompatibleIds, out _, buffer, ref length, 0) != 0) return "";
        return new string(buffer, 0, (int)Math.Min(length / 2, buffer.Length)).Replace('\0', ' ');
    }

    // DEVPKEY_Device_BusReportedDeviceDesc: the product string the device itself gives.
    private static DevPropKey busReported = new()
    {
        fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2), pid = 4,
    };

    private static string BusName(uint node)
    {
        uint length = 512;
        var buffer = new char[length / 2];
        if (CM_Get_DevNode_PropertyW(node, ref busReported, out _, buffer, ref length, 0) != 0) return "";
        string s = new(buffer, 0, (int)Math.Min(length / 2, buffer.Length));
        int end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] char[] buffer,
        ref uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, [Out] char[] buffer,
        ref uint length, uint flags);
}
