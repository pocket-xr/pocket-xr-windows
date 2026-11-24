# Pocket XR for Windows 0.4.7

- **Your phone's PC screen size, made on this PC.** Pocket XR for Android 1.6.14 lets each PC screen ask for a size:
  its window's shape (as before), 1920 x 1080, 1920 x 1200 or 2560 x 1600. When your phone streams this PC ("Let my
  phone stream this PC"), Pocket XR now makes this PC's screen at exactly that size. If the virtual display driver
  does not offer it, it uses the nearest size the driver offers of the same shape, and if none is, it keeps the screen
  as it comes and scales the picture to the size asked. It never fails over a size, and the log says what it did and
  what the driver offers.

  With Apollo's driver (SudoVDA), tried on a PC: screens of 1920 x 1200, 2560 x 1600 and 1920 x 1080 were each made at
  exactly that size.

Untested: Parsec's virtual display driver (its own list has 1920 x 1200 and 2560 x 1600, so they should be made
exactly; a phone window's own shape is not on its list, so that screen is scaled), and a real phone streaming at
1920 x 1200 or 2560 x 1600. If you try them, **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.6.

## Install

1. Download `PocketXR-Windows-0.4.7.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
   Quit the running Pocket XR first (its menu, "Quit and put the glasses back").
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
