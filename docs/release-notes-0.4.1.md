# Pocket XR for Windows 0.4.1

- **XREAL XBX A01 and A01+, take two.** On Pocket XR for Steam Deck, a tester's XBX A01+ sent no motion at all on
  the interface these glasses use for it. wheaney's nrealAirLinuxDriver (MIT), which XR Gaming uses, wakes these two
  models' control chip first and keeps it awake. Pocket XR for Windows now does the same, as the Steam Deck's 0.6.1
  does:
  - it asks interface 1 alone first;
  - if nothing comes for about 6 seconds, it wakes the control chip on interface 0 (a short start-up exchange, then a
    small "still here" message every tenth of a second), asks interface 1 again, then tries interface 2;
  - it uses whichever one sends motion, and stops waking the chip when head tracking stops.
  The log names each interface and shows the first bytes each one sends, so a tester's log tells what happened.
  Thanks to Pierre Zammit for his XBX A01+ logs, which led to this.

  Untested: not yet run on either model, on any system. Turn it on in Settings, Experimental ("Head tracking for XREAL
  Air, XBX A01 and VITURE (USB HID)"), and if you have one, **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.0.

## Install

1. Download `PocketXR-Windows-0.4.1.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
