using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PocketXR.Core;

/// <summary>
/// Pocket XR's log: %LOCALAPPDATA%\Pocket XR\pocket-xr.log, kept under 4 MB (the old half moves to pocket-xr.1.log).
/// </summary>
public sealed class Log
{
    public static readonly Log Shared = new();

    public string Folder { get; }
    public string File { get; private set; }
    private string baseName = "pocket-xr";
    private readonly object gate = new();
    private FileStream? stream;
    private const long MaxBytes = 4 << 20;

    /// <summary>Also print each line (the --imu-test run and debugging from a terminal).</summary>
    public bool Echo;

    private Log()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pocket XR");
        File = Path.Combine(Folder, "pocket-xr.log");
        try { Directory.CreateDirectory(Folder); } catch (Exception) { }
        Open();
    }

    private void Open()
    {
        try
        {
            stream = new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception)
        {
            stream = null;
        }
    }

    public void Write(string line)
    {
        string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        lock (gate)
        {
            if (Echo) Console.Error.Write(text);
            if (stream == null) return;
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                if (stream.Length > MaxBytes) Rotate();
            }
            catch (Exception) { }
        }
    }

    private void Rotate()
    {
        try
        {
            stream?.Dispose();
            string old = Path.Combine(Folder, baseName + ".1.log");
            if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
            System.IO.File.Move(File, old);
        }
        catch (Exception) { }
        Open();
    }

    /// <summary>
    /// This process logs to its own file (%LOCALAPPDATA%\Pocket XR\NAME.log): a PC screen runs as its own process, and
    /// two processes appending to one file wrote over each other's lines (20 Nov 2026).
    /// </summary>
    public void UseFile(string name)
    {
        lock (gate)
        {
            try { stream?.Dispose(); } catch (Exception) { }
            baseName = name;
            File = Path.Combine(Folder, name + ".log");
            Open();
        }
    }

    /// <summary>Shortcut used everywhere: Log.P("...").</summary>
    public static void P(string line) => Shared.Write(line);
}
