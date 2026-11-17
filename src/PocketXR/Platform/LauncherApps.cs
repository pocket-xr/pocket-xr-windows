using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PocketXR.Core;

namespace PocketXR.Platform;

// What the launcher shows that comes from the PC: its apps and their icons, the battery and whether Wi-Fi is
// connected, and how an app is started. Nothing here needs administrator rights, and reading the apps starts
// nothing: the Start menu's shortcuts are files that are read, and Store apps are asked of the shell's own list
// (shell:AppsFolder).

/// <summary>
/// One app. Its id is what it is started and remembered by: the full path of its Start menu shortcut (a .lnk file),
/// or, for a Store app, its name in shell:AppsFolder (such as Microsoft.WindowsCalculator_8wekyb3d8bbwe!App).
/// </summary>
public sealed record LauncherApp(string Id, string Name)
{
    /// <summary>A Start menu shortcut, which is a file; a Store app is not.</summary>
    public bool IsShortcut => LauncherApps.IsShortcutId(Id);
}

public static class LauncherApps
{
    /// <summary>
    /// The apps offered as pins on the first run, in this order; the first four that are there are taken. These are
    /// Windows' own apps, standing in for the Mac's list in LauncherPins.DefaultNames (Safari, Mail, Notes...).
    /// They are looked for by name among the apps found, so no list of folders is needed.
    /// </summary>
    public static readonly string[] DefaultPinNames =
    {
        "Microsoft Edge", "File Explorer", "Notepad", "Photos", "Media Player", "Calculator", "Terminal", "Settings",
    };

    /// <summary>The Start menu's two folders of shortcuts: everyone's, then this person's.</summary>
    public static string[] Folders => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
    }.Where(f => f.Length > 0).ToArray();

    /// <summary>Folders of the Start menu that hold no apps a person opens: what starts with Windows, and its tools for administrators.</summary>
    private static readonly string[] LeftOutFolders = { "Startup", "Administrative Tools" };

    /// <summary>What a shortcut points at when it is a paper or a web page and not an app.</summary>
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ".chm", ".hlp", ".txt", ".pdf", ".htm", ".html", ".mht", ".url", ".rtf", ".md", ".doc", ".docx", ".xml", ".log",
        ".ini", ".website", ".xps",
    };

    private static readonly Regex Uninstaller = new(@"\b(uninstall\w*|uninst|unins\d*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A name that ends as a paper's does ("Sample App Manual"), or is a read-me.</summary>
    private static readonly Regex Paper = new(
        @"(\b(help|documentation|manual|user guide|user's guide|release notes|licen[sc]e|website|web site|homepage|home page|changelog|faq|on the web)\s*$)|(^\s*read ?me\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A Store app's name in shell:AppsFolder: its package family, "!", its app.</summary>
    private static readonly Regex StoreId = new(@"^[^\\/!\s]+_[a-z0-9]{13}![^\\/\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsShortcutId(string id) =>
        id.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && id.Length > 6 && (id[1] == ':' || id.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>The app is still there: a shortcut's file exists. A Store app is only known from the list of apps.</summary>
    public static bool ShortcutExists(string id) => IsShortcutId(id) && File.Exists(id);

    /// <summary>A shortcut's name with nothing asked of the shell: its file's name without ".lnk".</summary>
    public static string PlainName(string id) => IsShortcutId(id) ? Path.GetFileNameWithoutExtension(id) : id;

    /// <summary>
    /// Should the shortcut called [name], which points at [target] with [arguments], be left out: an uninstaller, a
    /// help file, a paper, a link to a web page.
    /// </summary>
    public static bool LeftOut(string name, string target, string arguments)
    {
        if (Uninstaller.IsMatch(name)) return true;
        if (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return true;
        string file = "", extension = "";
        try
        {
            file = Path.GetFileNameWithoutExtension(target);
            extension = Path.GetExtension(target);
        }
        catch (ArgumentException) { }
        if (NotApps.Contains(extension)) return true;
        if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("hh", StringComparison.OrdinalIgnoreCase) || file.Equals("winhlp32", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("msiexec", StringComparison.OrdinalIgnoreCase) && arguments.Contains("/x", StringComparison.OrdinalIgnoreCase)) return true;
        // An app's own name is trusted; "Manual" or "Website" beside it is a paper whatever opens it.
        if (Paper.IsMatch(name)) return true;
        // A shortcut that opens a web page or a paper through a program (a browser, a reader) by its arguments.
        string a = arguments.Trim().Trim('"');
        if (a.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Shortcuts read before, by path, with the time their file was last written: a file that has not changed is not read again.</summary>
    private static readonly Dictionary<string, (DateTime Written, string Target, string Arguments, string Name)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a shortcut points at, with what arguments, and its name as File Explorer shows it.</summary>
    private static (string Target, string Arguments, string Name) Read(string path)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (Known)
        {
            if (Known.TryGetValue(path, out var have) && have.Written == written) return (have.Target, have.Arguments, have.Name);
        }
        (string target, string arguments) = Shell.ReadShortcut(path);
        string name = Shell.DisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Trim();
        lock (Known) Known[path] = (written, target, arguments, name);
        return (target, arguments, name);
    }

    /// <summary>
    /// Every app there is, by name. Reads files and asks the shell, so it is called off the main thread, on a thread
    /// with COM set up (LauncherWorker). Starts nothing.
    /// </summary>
    public static List<LauncherApp> Scan()
    {
        var found = new List<LauncherApp>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int shortcuts = 0, leftOut = 0, twice = 0;

        foreach (string folder in Folders)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System,
                }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                shortcuts++;
                string inside = Path.GetRelativePath(folder, Path.GetDirectoryName(path) ?? folder);
                if (inside.Split('\\', '/').Any(part => LeftOutFolders.Contains(part, StringComparer.OrdinalIgnoreCase))) { leftOut++; continue; }
                (string target, string arguments, string name) = Read(path);
                if (name.Length == 0 || name == "Pocket XR" || LeftOut(name, target, arguments)) { leftOut++; continue; }
                // Twice: the same name, or two shortcuts to the same thing (everyone's and this person's).
                string key = target.Length > 0 ? target + "\n" + arguments : "";
                if (!names.Add(LauncherSearch.Folded(name)) || (key.Length > 0 && !targets.Add(key))) { twice++; continue; }
                found.Add(new LauncherApp(path, name));
            }
        }

        int store = 0;
        foreach ((string id, string name) in Shell.AppsFolder())
        {
            if (!StoreId.IsMatch(id)) continue;
            string shown = name.Trim();
            if (shown.Length == 0 || shown == "Pocket XR") continue;
            if (!names.Add(LauncherSearch.Folded(shown))) { twice++; continue; }
            found.Add(new LauncherApp(id, shown));
            store++;
        }

        found.Sort((a, b) =>
        {
            int order = Shell.StrCmpLogicalW(a.Name, b.Name);
            return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id);
        });
        Log.P($"launcher: {found.Count} apps ({found.Count - store} from {shortcuts} Start menu shortcuts, {store} Store apps; {leftOut} left out, {twice} twice)");
        return found;
    }

    /// <summary>
    /// Starts [app]. A shortcut is opened as a double click in the Start menu would (ShellExecute), and Windows is
    /// told which display it was asked for on, which is where a new app puts a first window that has no place of
    /// its own; a window the new program still opens elsewhere is then moved to that display. A Store app is
    /// started through explorer.exe and opens where Windows puts it. Call it off the main thread (LauncherWorker).
    /// </summary>
    public static bool Start(LauncherApp app, DisplayInfo? on)
    {
        try
        {
            if (!app.IsShortcut)
            {
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                explorer.ArgumentList.Add(@"shell:AppsFolder\" + app.Id);
                using Process? started = Process.Start(explorer);
                return started != null;
            }
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = SeeNoCloseProcess | SeeFlagNoUi | SeeNoAsync,
                Verb = null, File = app.Id, Show = SwShowNormal,
                Directory = Path.GetDirectoryName(app.Id),
            };
            if (on != null)
            {
                IntPtr monitor = MonitorFromPoint(new NativePoint { X = on.X + on.Width / 2, Y = on.Y + on.Height / 2 }, MonitorDefaultToNull);
                if (monitor != IntPtr.Zero)
                {
                    info.Mask |= SeeHMonitor;
                    info.IconOrMonitor = monitor;
                }
            }
            if (!ShellExecuteExW(ref info))
            {
                Log.P($"launcher: {app.Name} did not open: Windows error {Marshal.GetLastWin32Error()}");
                return false;
            }
            IntPtr process = info.Process;
            if (process != IntPtr.Zero)
            {
                if (on != null) WindowMover.Follow(process, on);
                else CloseHandle(process);
            }
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.P($"launcher: {app.Name} did not open: {e.Message}");
            return false;
        }
    }

    /// <summary>Shows a shortcut's file in File Explorer, selected.</summary>
    public static void ShowInExplorer(LauncherApp app)
    {
        if (!app.IsShortcut || !File.Exists(app.Id)) return;
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.Id}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.P($"launcher: File Explorer did not open: {e.Message}");
        }
    }

    private const uint SeeNoCloseProcess = 0x40, SeeNoAsync = 0x100, SeeFlagNoUi = 0x400, SeeHMonitor = 0x200000;
    private const int SwShowNormal = 1;
    private const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstApp;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr KeyClass;
        public uint HotKey;
        /// <summary>hIcon or, with SEE_MASK_HMONITOR, the display the app is asked for on.</summary>
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// After the launcher starts a program: its first real window, if Windows opened it on another display, is moved to
/// the floating screen the launcher was on. Only a window of the very program that was started is touched, once,
/// within eight seconds; a program that hands over to one already running (or to a helper) is left where it is.
/// </summary>
internal static class WindowMover
{
    private const int Tries = 80, TryMs = 100;
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsCaption = 0x00C00000, WsThickFrame = 0x00040000, WsExToolWindow = 0x80, WsChild = 0x40000000;
    private const uint SwpNoZOrder = 0x4, SwpNoActivate = 0x10;
    private const int SwMaximize = 3, SwRestore = 9;

    /// <summary>Takes [process] (a handle from ShellExecuteEx) and closes it when done.</summary>
    public static void Follow(IntPtr process, DisplayInfo on)
    {
        var thread = new Thread(() =>
        {
            try
            {
                uint pid = GetProcessId(process);
                for (int i = 0; i < Tries && pid != 0; i++)
                {
                    if (WaitForSingleObject(process, TryMs) == 0) break; // it ended: it handed over to another program
                    IntPtr window = FirstWindow(pid);
                    if (window == IntPtr.Zero) continue;
                    Move(window, on);
                    break;
                }
            }
            catch (Exception e)
            {
                Log.P($"launcher: the new window was not moved: {e.Message}");
            }
            finally
            {
                LauncherApps.CloseHandle(process);
            }
        }) { IsBackground = true, Name = "PocketXR launcher window" };
        thread.Start();
    }

    /// <summary>The program's first window a person would call a window: shown, its own, with a title bar or a frame to size it by.</summary>
    private static IntPtr FirstWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true; // GW_OWNER
            long style = GetWindowLongPtrW(hwnd, GwlStyle).ToInt64(), ex = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
            if ((style & WsChild) != 0 || (ex & WsExToolWindow) != 0 || (style & (WsCaption | WsThickFrame)) == 0) return true;
            if (!GetWindowRect(hwnd, out NativeRect r) || r.Right - r.Left < 120 || r.Bottom - r.Top < 80) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static void Move(IntPtr window, DisplayInfo on)
    {
        if (IsIconic(window) || !GetWindowRect(window, out NativeRect r)) return;
        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        if (on.Contains(midX, midY)) return; // already there: Windows put it where it was asked
        bool full = IsZoomed(window);
        if (full)
        {
            ShowWindow(window, SwRestore);
            if (!GetWindowRect(window, out r)) return;
        }
        int width = Math.Min(r.Right - r.Left, on.Width), height = Math.Min(r.Bottom - r.Top, on.Height);
        int x = on.X + (on.Width - width) / 2, y = on.Y + (on.Height - height) / 2;
        bool moved = SetWindowPos(window, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
        if (full) ShowWindow(window, SwMaximize);
        Log.P(moved ? "launcher: the new window was moved to the launcher's screen" : "launcher: Windows did not let the new window be moved");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint which);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int show);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

/// <summary>
/// One background thread for everything the launcher asks of the shell (the list of apps, icons, starting an app):
/// the shell's objects want a thread with COM set up as a single apartment, which the thread pool's are not.
/// </summary>
public sealed class LauncherWorker
{
    private readonly ConcurrentQueue<Action>[] queues = { new(), new(), new() };
    private readonly SemaphoreSlim ready = new(0);

    public LauncherWorker()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                ready.Wait();
                foreach (ConcurrentQueue<Action> queue in queues)
                {
                    if (!queue.TryDequeue(out Action? job)) continue;
                    try { job(); }
                    catch (Exception e) { Log.P($"launcher: background work failed: {e.Message}"); }
                    break;
                }
            }
        }) { IsBackground = true, Name = "PocketXR launcher", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Does [job] on the worker's thread. [after] 0 is done first (starting an app, reading the apps), 1 next (an icon that is on show), 2 when nothing else waits (icons read ahead).</summary>
    public void Do(Action job, int after = 0)
    {
        queues[Math.Clamp(after, 0, queues.Length - 1)].Enqueue(job);
        ready.Release();
    }
}

/// <summary>Apps' icons, read off the main thread and kept once read. Each is frozen, so any thread may show it.</summary>
public sealed class LauncherIcons
{
    private readonly LauncherWorker worker;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, ImageSource?> kept = new();
    private readonly HashSet<string> asked = new(), soon = new();
    /// <summary>Used on the worker's thread only: the icons it has read.</summary>
    private readonly HashSet<string> read = new();

    /// <summary>The icons' size in pixels: 128 suits an icon shown 72 points across up to 175 % scaling.</summary>
    public int Pixels = 128;

    /// <summary>An app's icon has been read (its id); raised on the main thread.</summary>
    public event Action<string>? Loaded;

    public LauncherIcons(LauncherWorker worker, Dispatcher dispatcher)
    {
        this.worker = worker;
        this.dispatcher = dispatcher;
    }

    /// <summary>The icon if it has been read; if not, null now, and it is read next and Loaded says when it is there.</summary>
    public ImageSource? Icon(LauncherApp app)
    {
        if (kept.TryGetValue(app.Id, out ImageSource? have)) return have;
        // On show now: read before the icons that are only read ahead (even if this one is among them).
        if (soon.Add(app.Id)) Ask(app.Id, 1);
        return null;
    }

    /// <summary>Reads [apps]' icons ahead, when nothing else waits, so they are there when the launcher opens.</summary>
    public void ReadAhead(IEnumerable<LauncherApp> apps)
    {
        foreach (LauncherApp app in apps)
        {
            if (!kept.ContainsKey(app.Id) && asked.Add(app.Id)) Ask(app.Id, 2);
        }
    }

    private void Ask(string id, int after)
    {
        asked.Add(id);
        int pixels = Pixels;
        worker.Do(() =>
        {
            if (!read.Add(id)) return; // the worker's own note: this one is read already
            ImageSource? image = Shell.Icon(id, pixels);
            dispatcher.BeginInvoke(() =>
            {
                if (!asked.Contains(id)) return; // forgotten meanwhile
                kept[id] = image;
                Loaded?.Invoke(id);
            });
        }, after);
    }

    public void Forget(ISet<string> except)
    {
        List<string> gone = asked.Where(id => !except.Contains(id)).ToList();
        if (gone.Count == 0) return;
        foreach (string id in gone)
        {
            kept.Remove(id);
            asked.Remove(id);
            soon.Remove(id);
        }
        worker.Do(() => { foreach (string id in gone) read.Remove(id); });
    }
}

/// <summary>The battery as the launcher shows it.</summary>
public sealed record LauncherBattery(int Percent, bool OnMains)
{
    /// <summary>The PC's battery; null on a PC with none.</summary>
    public static LauncherBattery? Read()
    {
        if (!GetSystemPowerStatus(out PowerStatus s)) return null;
        if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100) return null;
        return new LauncherBattery(s.BatteryLifePercent, s.AcLineStatus == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}

/// <summary>Wi-Fi as far as it can be known without asking for anything: whether a Wi-Fi adapter is up and has an address.</summary>
public enum LauncherWifi { Connected, Off }

public static class LauncherWifiReader
{
    /// <summary>Null on a PC with no Wi-Fi.</summary>
    public static LauncherWifi? Read()
    {
        try
        {
            bool any = false;
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                // Windows' own "Wi-Fi Direct" adapters are not the Wi-Fi a person means.
                if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                any = true;
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = a.Address.GetAddressBytes();
                    if (!(b[0] == 169 && b[1] == 254)) return LauncherWifi.Connected;
                }
            }
            return any ? LauncherWifi.Off : null;
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>The little of Windows' shell the launcher needs, through COM: a shortcut's target, an item's name and icon, the list of Store apps.</summary>
internal static class Shell
{
    private static Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private const uint SigdnNormalDisplay = 0, SigdnParentRelativeParsing = 0x80018001;
    private const int SiigbfIconOnly = 0x4;

    /// <summary>What a shortcut points at and with what arguments, read from its file. Nothing is looked up or started.</summary>
    public static (string Target, string Arguments) ReadShortcut(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var shell = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            var arguments = new StringBuilder(2048);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            shell.GetArguments(arguments, arguments.Capacity);
            return (target.ToString(), arguments.ToString());
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            return ("", "");
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
        }
    }

    private static IShellItem? Item(string id)
    {
        string parsing = LauncherApps.IsShortcutId(id) ? id : @"shell:AppsFolder\" + id;
        return SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref IidShellItem, out object item) == 0 ? (IShellItem)item : null;
    }

    /// <summary>A file's name as File Explorer shows it (a shortcut's without ".lnk", in the PC's language).</summary>
    public static string? DisplayName(string path)
    {
        IShellItem? item = null;
        try
        {
            item = Item(path);
            return item == null ? null : Name(item, SigdnNormalDisplay);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (item != null) Marshal.FinalReleaseComObject(item);
        }
    }

    private static string? Name(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out IntPtr text) != 0 || text == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    /// <summary>Everything in shell:AppsFolder (what the Start menu lists): each item's name there and the name it shows.</summary>
    public static List<(string Id, string Name)> AppsFolder()
    {
        var found = new List<(string, string)>();
        object? folder = null, list = null;
        try
        {
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref IidShellItem, out folder) != 0) return found;
            if (((IShellItem)folder).BindToHandler(IntPtr.Zero, ref BhidEnumItems, ref IidEnumShellItems, out list) != 0) return found;
            var items = (IEnumShellItems)list;
            while (items.Next(1, out IShellItem item, out uint got) == 0 && got == 1)
            {
                try
                {
                    string? id = Name(item, SigdnParentRelativeParsing), name = Name(item, SigdnNormalDisplay);
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) found.Add((id, name));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.P($"launcher: Store apps were not read: {e.Message}");
        }
        finally
        {
            if (list != null) Marshal.FinalReleaseComObject(list);
            if (folder != null) Marshal.FinalReleaseComObject(folder);
        }
        return found;
    }

    /// <summary>An app's icon, [pixels] across, with its own see-through edges; frozen. Null when the shell has none.</summary>
    public static ImageSource? Icon(string id, int pixels)
    {
        IShellItem? item = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            item = Item(id);
            if (item == null) return null;
            var factory = (IShellItemImageFactory)item;
            if (factory.GetImage(new NativeSize { Width = pixels, Height = pixels }, SiigbfIconOnly, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            return FromBitmap(bitmap);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (item != null) Marshal.FinalReleaseComObject(item);
        }
    }

    /// <summary>The shell's bitmap (32 bits, colour already multiplied by alpha) as a WPF picture.</summary>
    private static ImageSource? FromBitmap(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap b) == 0 || b.Width <= 0 || b.Height <= 0) return null;
        int width = b.Width, height = b.Height, stride = width * 4;
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0,
        };
        var pixels = new byte[stride * height];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        // An old icon with no alpha at all comes back with every alpha 0: it is solid, not invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { anyAlpha = true; break; }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems copy);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr handle, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
