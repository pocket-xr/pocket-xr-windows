# Pocket XR for Windows 0.4.2

- **The XREAL XBX A01+'s display is found.** Windows names this model's display "xbx a01+", with no maker's name in
  it, so Pocket XR did not see it as the glasses: the glasses showed a picture, but the tour said Windows did not see
  their display yet. Pocket XR now knows "XBX" in a display's name, and also takes a display named after the
  plugged-in glasses' own USB product name as the glasses, ignoring case, for any model that names its display that
  way. Thanks to Pierre Zammit for his report and log, which led to this.

  Untested with XBX glasses on Windows: if you have one, **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.1 (Readability, and the XBX's head tracking).

## Install

1. Download `PocketXR-Windows-0.4.2.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
