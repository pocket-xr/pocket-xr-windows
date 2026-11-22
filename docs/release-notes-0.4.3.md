# Pocket XR for Windows 0.4.3

- **The control panel fits the screen it is on.** Its height can now be changed by dragging its edge, and it never
  runs past the bottom of the screen it is on: what does not fit scrolls. Before, its height was worked out from the
  main screen, so with Windows' "Second screen only", or screens at different scales, the bottom of the window
  (Launcher, Pocket mode, Hands and below) could be off the screen. The control panel, Settings and the tour are also
  moved back onto their screen when they open, when they move to a screen with another scale, and when the PC's
  screens change.
- **Head tracking over USB says where it is.** For glasses whose head tracking is the USB HID switch (the XREAL Air
  family, the XBX A01 and A01+, VITURE), the status line now says "head tracking off: turn it on in Settings,
  Experimental" while that switch is off, and the glasses report says whether the switch is on and what the reader is
  doing (which interface it is trying, or that samples are arriving).

Thanks to Pierre Zammit for his report and screenshots, which led to both.

Untested: the window on a "Second screen only" setup or screens of different scales (checked by sums only), and the
XBX's head tracking on Windows (if you have one, turn the switch on and use **Send logs to Mike** in the menu).

Everything else is as in 0.4.2.

## Install

1. Download `PocketXR-Windows-0.4.3.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
