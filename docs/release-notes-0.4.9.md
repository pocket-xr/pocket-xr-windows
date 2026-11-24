# Pocket XR for Windows 0.4.9

From Pierre Zammit's list on 0.4.8 (XREAL XBX A01+ on Windows). Thanks, Pierre.

- **This PC's screen: left, below, right or above** (Settings, Space). Where your PC's own screen sits next to the
  floating screens on the desktop, for the pointer: going off that edge of them reaches it. Below suits a laptop under
  the glasses' screens (the floating screens go above it, centred). Left is as before.
- **Win+Shift+S works on a floating screen.** Windows' snipping puts a still, dimmed picture over every monitor,
  the glasses' display too, so the glasses showed a still picture and you couldn't see your snip. Pocket XR now hides
  that part on the glasses' display, and the Space stays live while you snip.
- **Use the glasses' buttons** (Settings, Glasses, Experimental), for glasses tracked over USB: Pocket XR lets go of
  the glasses so their own buttons work, for brightness say; head tracking and taps pause until you turn it off. On the
  XBX A01+ the buttons come back only when the glasses reset themselves, which can take 1 to 3 minutes: wait for them
  to blink off and on.
- **Softer taps count, and a Tap sensitivity slider** (Settings, Head tracking, 25% to 300%). Taps now use the Steam
  Deck's newer reading: a sudden jolt of 0.25 g with a sharp start, the frame's ringing after it left out, taps 0.2 to
  0.7 seconds apart. Higher counts softer taps, lower only firm ones; pressing the glasses' own buttons can count as a
  tap when it is high (on a Steam Deck, 25% was just enough that they didn't). Each jolt is written to the log.
- **Distance and eye distance say why they do nothing in 2D.** They change only the 3D side by side picture. Pocket XR
  can switch the Aura and One series to 3D, but not the Air family or the XBX on a PC, so with those in 2D (their
  display 1920 x 1080) these controls are turned off with the reason. If your glasses have a 3D mode of their own,
  turning it on there brings them back.
- **Portrait screens** (Settings, Space): any floating screen turned on its side, 1080 x 1920, for code or long pages.
  Windows turns it, so apps lay out tall.

Untested: all of these in glasses; portrait screens with Parsec's driver (tried with Apollo's); This PC's sides on a
real desktop (checked by sums); the glasses' buttons on Windows; and taps with real taps on Windows. If you try them,
**Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.8.

## Install

1. Download `PocketXR-Windows-0.4.9.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`; it closes an older Pocket XR that is still running. It is not signed yet, so Windows may say
   "Windows protected your PC": choose **More info**, then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
