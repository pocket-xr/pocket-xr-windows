using System;
using System.Runtime.CompilerServices;

namespace PocketXR.Checks;

/// <summary>
/// Pocket XR's core checks: each area has its own file with one static Run method, called from Program.cs.
/// The same checks as Checks/ in Pocket XR for Mac.
/// </summary>
public static class Check
{
    public static int Failures;
    public static int Checks;

    public static void That(bool ok, string what, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Checks++;
        if (!ok)
        {
            Failures++;
            Console.WriteLine($"FAIL {System.IO.Path.GetFileName(file)}:{line}: {what}");
        }
    }

    public static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
}
