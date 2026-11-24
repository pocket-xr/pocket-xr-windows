# Pocket XR for Windows 0.4.8

- **A new version now replaces the one already running.** Before, starting Pocket XR while it was already in the
  notification area only brought the running copy's control panel forward, so a newer version started while an older
  one was still running never ran at all: a tester kept running 0.4.1 after installing 0.4.5 and 0.4.7, and his XBX
  A01+'s display (which 0.4.2 and later know) was never found. Now a new version (or a copy from another folder) asks
  the running one to quit, waits while it puts the glasses back, and carries on. Starting the same copy again still just
  brings its control panel forward.
- **The version running is shown** in the control panel and on the Glasses page, and the log says which file started.
- **Why no display was taken:** with glasses plugged in but no display of theirs, the Glasses page, the log and the
  glasses report now say why each of the PC's displays was not taken (not a glasses name, no name, a virtual display,
  or a copy of another screen).

**If you are on 0.4.7 or older:** just start 0.4.8; it asks the old copy to quit by itself (tried with an older copy
on a PC: it quit in under a second). If it says the old copy is still running, quit it once yourself: right-click
Pocket XR's glasses icon by the clock and choose **Quit and put the glasses back** (or end PocketXR.exe in Task
Manager), then start 0.4.8 again.

Thanks to Pierre Zammit, whose report led to this.

Untested: the take-over with glasses plugged in and showing the Space (it waits up to 45 seconds for the old copy to
put them back), and the new "why" lines with real glasses.

Everything else is as in 0.4.7.

## Install

1. Download `PocketXR-Windows-0.4.8.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
