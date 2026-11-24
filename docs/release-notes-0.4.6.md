# Pocket XR for Windows 0.4.6

- **Text size: Standard, 125% and 150%** (Settings, Picture), as on the Mac. Larger text on every screen, the screens
  as wide as before, so the side screens do not move further out. Each screen keeps its 1920 x 1080 pixels and
  Windows draws it at 125% or 150% (its own scale for that display), so text is larger and every pixel is still there:
  it stays sharp. It works on the floating screens and on the screen your phone streams from this PC, and changes
  straight away.
- **Readability now matches the Mac:** one screen at 1440 x 810, 34 degrees wide, about 42 of its pixels a degree,
  as 1080p glasses like the XREAL Air show (the usual screens are 48). Parsec's virtual display driver offers no
  1440 x 810, so with it Readability uses 1600 x 900, 37.8 degrees wide, still about 42 pixels a degree. Before, when
  the driver refused 1440 x 810, the screen stayed at 1920 x 1080 and text was no larger. A phone streaming this PC
  with Readability on gets a screen three quarters of its size, drawn larger. Turning it off puts your screens back
  as they were.
- Each screen's size and scale are written to the log.

Untested: both in glasses, and both with Parsec's driver. They were checked on a PC with Apollo's driver (SudoVDA),
where Windows' scale was set to 125%, 150% and back to 100% and read back right each time. If you try them, **Send
logs to Mike** in the menu helps a lot.

Readability is after an idea by Dmitriy Cherdantsev, a Pocket XR for Mac tester.

Everything else is as in 0.4.5.

## Install

1. Download `PocketXR-Windows-0.4.6.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
   Quit the running Pocket XR first (its menu, "Quit and put the glasses back").
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
