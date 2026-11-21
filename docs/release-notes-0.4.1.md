# Pocket XR for Windows 0.4.1

- **Readability** (Settings, Picture; off by default): larger, sharp text on 1080p glasses. One floating screen drawn
  at 1440 x 810, shown just wide enough that one of its pixels covers about one of the glasses' (about 32 degrees on
  the XREAL Air, 51 on the Aura, 30 if the glasses are not known). It changes only the screen count, the screens'
  width and screens moved by hand, and turning it off puts exactly those back. Both are logged with the values before
  and after. Thanks to Dmitriy Cherdantsev, a Pocket XR for Mac tester, for the idea.

  Untested on glasses: if you try it, **Send logs to Mike** in the menu helps a lot.

- **XREAL XBX A01 and A01+, take two.** A tester's XBX A01+ sent no motion at all on the interface these glasses use
  for it. wheaney's nrealAirLinuxDriver (MIT), which XR Gaming uses, wakes these two models' control chip first and
  keeps it awake, and Pocket XR for Steam Deck 0.6.1 does the same. Pocket XR for Windows now does too:
  - it asks interface 1 alone first;
  - if nothing comes for about 6 seconds, it wakes the control chip on interface 0 (a short start-up exchange, then a
    small "still here" message every tenth of a second), asks interface 1 again, then tries interface 2;
  - it uses whichever one sends motion, and stops waking the chip when head tracking stops.
  The log names each interface and shows the first bytes each one sends, so a tester's log tells what happened.
  Thanks to Pierre Zammit for his XBX A01+ logs, which led to this.

  Untested on Windows: it is the same approach that worked on the Steam Deck for Pierre Zammit. Turn it on in
  Settings, Experimental ("Head tracking for XREAL Air, XBX A01 and VITURE (USB HID)"), and if you have one,
  **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.0.

## Install

1. Download `PocketXR-Windows-0.4.1.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
