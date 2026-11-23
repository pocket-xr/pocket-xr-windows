# Pocket XR for Windows 0.4.5

- **XREAL XBX A01+: head tracking works for one tester.** With "Head tracking for XREAL Air, XBX A01 and VITURE (USB
  HID)" on (Settings, Experimental), Pierre Zammit's XBX A01+ streams its motion on Windows once Pocket XR has woken its
  control chip, and the view moves with his head. The "Which glasses work" list in Settings now says so.
- **Why his glasses showed no Space.** His logs came from a copy of 0.4.1 that had been running since before 0.4.2 came
  out. 0.4.1 did not know the XBX A01+'s display (Windows names it "xbx a01+"), so it drew nothing on the glasses: they
  showed what Windows sends them, and the control panel said 0 fps. 0.4.2 and later know that display. If you have an
  XBX: quit Pocket XR from its menu and start this version.
- **Clearer logs, so the next one shows where the Space goes:**
  - with glasses on USB but none of the PC's displays theirs, the log lists the displays Windows has (once per change);
  - when the Space starts, the log says which display it is drawn on, where, at what size and refresh, and from which
    screens; its frame rate follows after 10 seconds and then once a minute;
  - the logs' about.txt says when the running copy started, so a copy left running since before an update is plain.

Thanks to Pierre Zammit for his logs and screenshots.

Untested: the XBX's floating screens in the glasses with 0.4.2 or later (the next logs will show it), the XBX A01, and
Windows' "Second screen only" with Pocket XR.

Everything else is as in 0.4.4.

## Install

1. Download `PocketXR-Windows-0.4.5.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
   Quit the running Pocket XR first (its menu, "Quit and put the glasses back").
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
