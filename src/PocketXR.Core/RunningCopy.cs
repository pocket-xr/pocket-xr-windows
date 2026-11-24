using System;

namespace PocketXR.Core;

/// <summary>
/// One Pocket XR at a time, and the newest one: what a second start does. Pocket XR used to bring the running copy's
/// control panel forward and stop, so a newer version started while an older one was still in the tray never ran (a
/// tester ran 0.4.1 for days after installing 0.4.5 and 0.4.7, 24 Nov 2026). The running copy writes its version, file
/// and process id (Text); a second start of the same version from the same file shows the running one, as before; any
/// other start (another version, another file, or a copy from before this was written, which leaves no record) asks the
/// running one to quit, as PocketXR --quit does, and takes over.
/// </summary>
public static class RunningCopy
{
    public enum Choice { ShowRunning, Replace }

    /// <summary>The record the running copy keeps: version, file, process id, one a line.</summary>
    public static string Text(string version, string path, int pid) => $"{version}\n{path}\n{pid}\n";

    /// <summary>The running copy's version, file and process id from its record, or null if it left none (or a broken one).</summary>
    public static (string Version, string Path, int Pid)? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length < 3 || !int.TryParse(lines[2].Trim(), out int pid) || lines[0].Trim().Length == 0) return null;
        return (lines[0].Trim(), lines[1].Trim(), pid);
    }

    /// <summary>
    /// What a second start does, given the running copy's record ([record]: null when it left none), whether that
    /// record's process is still alive, and this copy's own version and file.
    /// </summary>
    public static Choice Decide((string Version, string Path, int Pid)? record, bool recordAlive, string myVersion, string myPath)
    {
        if (record is not var (version, path, _) || !recordAlive) return Choice.Replace; // an older copy that keeps no record
        bool same = version == myVersion && string.Equals(path, myPath, StringComparison.OrdinalIgnoreCase);
        return same ? Choice.ShowRunning : Choice.Replace;
    }

    /// <summary>The running copy's version for messages: its record's, or "an older version".</summary>
    public static string Describe((string Version, string Path, int Pid)? record, bool recordAlive) =>
        record is var (version, _, _) && recordAlive ? version : "an older version";
}
