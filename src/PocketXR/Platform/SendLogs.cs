using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PocketXR.Core;

namespace PocketXR.Platform;

/// <summary>
/// "Send logs to Mike": one zip of Pocket XR's logs in Downloads, with emails, the Windows user name and IP addresses
/// (other than the glasses' own link-local ones) taken out, then an email to Mike with the zip shown in File Explorer
/// to attach. Nothing is sent until the person sends that email.
/// </summary>
public static class SendLogs
{
    public const string Email = "connect@mikehaye.com";

    /// <summary>When this copy of Pocket XR started: a log from a copy left running since before an update says so
    /// (a tester's 0.4.1 ran from 22 to 23 Nov after 0.4.3 was out).</summary>
    private static string StartedAt()
    {
        try { return Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return "at a time Windows did not give"; }
    }

    public static void Run(string version)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string downloads = Downloads();
        string zip = Path.Combine(downloads, $"pocketxr-windows-logs-{stamp}.zip");
        try
        {
            using (FileStream file = File.Create(zip))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (string path in Directory.GetFiles(Log.Shared.Folder, "*.log"))
                {
                    string text;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        text = reader.ReadToEnd();
                    Write(archive, Path.GetFileName(path), Clean(text));
                }
                string about =
                    "Pocket XR for Windows logs, sent with Send logs to Mike\n" +
                    $"Version: {version} (the copy running now, started {StartedAt()})\n" +
                    $"Windows: {RuntimeInformation.OSDescription}\n" +
                    $"PC: {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors\n" +
                    "Taken out of every line: email addresses, the Windows user name, and IP addresses other than the glasses' own link (169.254.x.x).\n";
                Write(archive, "about.txt", about);
            }
        }
        catch (Exception e)
        {
            Log.P($"send logs: the zip could not be made: {e.Message}");
            return;
        }
        Log.P($"send logs: {Path.GetFileName(zip)}");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
            string subject = Uri.EscapeDataString("Pocket XR for Windows logs");
            string body = Uri.EscapeDataString($"Hi Mike,\n\nWhat happened (and roughly when):\n\n\n(Attach {Path.GetFileName(zip)} from Downloads.)");
            Process.Start(new ProcessStartInfo($"mailto:{Email}?subject={subject}&body={body}") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.P($"send logs: the email could not be opened: {e.Message}");
        }
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex IpPattern = new(@"\b(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);

    /// <summary>Emails, the user name and IP addresses out, except the glasses' own link-local ones.</summary>
    public static string Clean(string text) => Clean(text, Environment.UserName);

    public static string Clean(string text, string userName)
    {
        string t = EmailPattern.Replace(text, "<email>");
        t = IpPattern.Replace(t, m =>
        {
            string a = m.Groups[1].Value, b = m.Groups[2].Value;
            return (a == "169" && b == "254") || a == "127" ? m.Value : "<ip>";
        });
        // A Windows path carries the account's name (C:\Users\name\...).
        if (userName.Length >= 2)
            t = Regex.Replace(t, @"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
        return t;
    }

    /// <summary>The person's Downloads folder (it can be moved, so Windows is asked).</summary>
    public static string Downloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                string? path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            }
        }
        catch (Exception) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
