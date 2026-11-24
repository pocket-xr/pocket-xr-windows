# Pocket XR for Windows 0.4.10

From Pierre Zammit's report on 0.4.9 (XREAL XBX A01+ on Windows, a laptop below the glasses' screens). Thanks, Pierre.

- **A portrait screen is drawn the shape Windows has it.** In 0.4.9, turning Portrait off left the picture tall in the
  glasses, with Windows' landscape desktop squeezed into it; turning it on again could do the opposite, a portrait
  desktop drawn wide with a squashed taskbar. The Space took each screen's shape from what it last read of the
  displays, and read them again only when the glasses' own display moved too, which it did the first time but not
  after. Now the Space is laid out again whenever a floating screen changes shape or size, from Portrait or from
  Windows' own Display settings, and each screen is drawn the shape of the picture Windows gives for it even before
  then. The log says when it catches a screen whose shape changed.
- **Placing the screens works with a floating screen as the main display.** Pocket XR moved the floating screens
  beside this PC's screen without keeping the main display at the top left of the desktop, and Windows refused
  ("Windows would not move the displays: error 87" in the log) once a floating screen was the main one. Whichever
  display is main now stays main when the screens are placed.
- **Main display** (Settings, Space: This PC, Screen 1, 2 or 3): which screen is Windows' main display, where the
  taskbar and new windows go. Every display moves together, so they stay joined the way they were. A main display
  chosen in Windows' own Settings is shown here too, rather than changed back. The log says each change.

Untested: all of these in glasses, and on a PC: the drawing and the main display were checked by sums (the checks
replay Pierre's two portrait switches and his refused move), not with a turned screen or a change of main display on
a real desktop. If you try them, **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.9.

## Install

1. Download `PocketXR-Windows-0.4.10.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`; it closes an older Pocket XR that is still running. It is not signed yet, so Windows may say
   "Windows protected your PC": choose **More info**, then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
