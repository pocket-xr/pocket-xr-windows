using System;

namespace PocketXR.Core;

/// <summary>
/// How many screens Pocket XR for Windows makes at most: floating screens (virtual displays) in the glasses plugged into
/// this PC, PC screens shown from another computer, and Apollo instances for the phone's PC screens. Up to 0.4.10 it was
/// 3 everywhere; 6 since 0.4.11. The default stays as it was (2 floating screens, 3 PC screens set up for the phone).
/// </summary>
public static class ScreenCount
{
    /// <summary>The most screens of any kind.</summary>
    public const int Max = 6;

    /// <summary>The most before 0.4.11. Rows of switches (portrait) keep at least this many, so they look as before
    /// for anyone with 3 screens or fewer.</summary>
    public const int Earlier = 3;

    /// <summary>
    /// SudoVDA (Apollo's driver) makes at most its registry value <c>maxMonitors</c>
    /// (HKLM\SOFTWARE\SudoMaker\SudoVDA), 10 when it is not set (SudoVDA's README). That is for the whole PC: an
    /// Apollo's own displays count too.
    /// </summary>
    public const int SudoVdaDefault = 10;

    /// <summary>
    /// Parsec's Virtual Display Adapter: up to 16 displays per adapter, of which the open source parsec-vdd uses 8
    /// (VDD_MAX_DISPLAYS, "to avoid plugging lag"); Parsec's own app adds at most 3. Pocket XR talks to the driver as
    /// parsec-vdd does, so 8.
    /// </summary>
    public const int ParsecLimit = 8;

    /// <summary>
    /// The most displays [driverName]'s driver will make, as far as is known before asking it: SudoVDA's
    /// [sudoMaxMonitors] (null when the registry value is not set), Parsec's 8. Null for a driver not known.
    /// </summary>
    public static int? DriverLimit(string? driverName, int? sudoMaxMonitors)
    {
        if (string.IsNullOrEmpty(driverName)) return null;
        if (driverName.Contains("Sudo", StringComparison.OrdinalIgnoreCase))
            return sudoMaxMonitors is int m && m > 0 ? m : SudoVdaDefault;
        if (driverName.Contains("Parsec", StringComparison.OrdinalIgnoreCase)) return ParsecLimit;
        return null;
    }

    /// <summary>How many floating screens the pickers offer: up to 6, fewer when the driver is known to make fewer.</summary>
    public static int Offered(int? driverLimit) => Math.Clamp(driverLimit ?? Max, 1, Max);

    /// <summary>How many floating screens are made for [wanted] (the setting) with [driverLimit]: the setting, at most
    /// what the driver makes.</summary>
    public static int ToMake(int wanted, int? driverLimit) => Math.Min(Math.Clamp(wanted, 1, Max), Offered(driverLimit));

    /// <summary>The log's words when fewer than [wanted] are made because of the driver, else null.</summary>
    public static string? LimitWords(int wanted, int? driverLimit, string? driverName)
    {
        int made = ToMake(wanted, driverLimit);
        if (made >= Math.Clamp(wanted, 1, Max)) return null;
        return $"virtual displays: {wanted} asked, {made} made: {driverName ?? "the driver"} makes at most {driverLimit}" +
            (driverName != null && driverName.Contains("Sudo", StringComparison.OrdinalIgnoreCase)
                ? " (its maxMonitors in HKLM\\SOFTWARE\\SudoMaker\\SudoVDA)" : "");
    }
}
