using System;
using PocketXR.Checks;

// Pocket XR's core checks: the same checks as Checks/ in Pocket XR for Mac, run against the C# port of the shared
// logic. No glasses and no window are needed. Exit code 0 means every check passed.
//
//     dotnet run --project src/PocketXR.Checks

SettingsChecks.Run();
TrackingChecks.Run();
LayoutChecks.Run();
GlassesChecks.Run();
TourChecks.Run();
ControllerChecks.Run();
LauncherChecks.Run();
HandChecks.Run();
PcScreenChecks.Run();
ReadabilityChecks.Run();
TapChecks.Run();
PierreChecks.Run();

Console.WriteLine(Check.Failures == 0
    ? $"core checks: all {Check.Checks} passed"
    : $"core checks: {Check.Failures} of {Check.Checks} FAILED");
return Check.Failures == 0 ? 0 : 1;
