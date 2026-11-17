using System.Text;
using PocketXR.Core;

namespace PocketXR.Checks;

public static class SettingsChecks
{
    public static void Run()
    {
        // An older file with only some keys takes the defaults for the rest.
        byte[] old = Encoding.UTF8.GetBytes("{\"screens\": 3, \"widthDeg\": 55}");
        SpaceSettings? s = SpaceSettings.TryDecode(old);
        Check.That(s?.Screens == 3, "screens read");
        Check.That(s?.WidthDeg == 55, "width read");
        Check.That(s?.Distance == 1.4, "distance defaults");
        Check.That(s?.TrackingMode == TrackingMode.BodyAnchor, "tracking mode defaults");

        var wild = new SpaceSettings();
        wild.Screens = 9;
        wild.Curve = -1;
        wild.IpdMm = 10;
        SpaceSettings c = wild.Clamped;
        Check.That(c.Screens == 3 && c.Curve == 0 && c.IpdMm == 54, "values brought inside their ranges");

        SpaceSettings? back = SpaceSettings.TryDecode(new SpaceSettings().Encode());
        Check.That(back == new SpaceSettings(), "a round trip keeps every value");
    }
}
