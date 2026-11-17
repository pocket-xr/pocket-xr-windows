# Pocket XR for Windows 0.1.0 (preview)

Your PC's apps on your XR glasses, as screens floating in front of you that stay put as you turn your head. Free and
open source (GPL-3.0), no account, no paid tier. The Windows version of Pocket XR, with the Android, Steam Deck and Mac
versions.

**This is a preview.** It has been used with one pair of glasses on one PC: the **XREAL Aura on a Steam Deck running
Windows 11** (17 November 2026). There it showed floating screens in the glasses, held still by head tracking, in 3D.
Nothing else has been tried yet. If you try it with other glasses or another PC, please use **Send logs to Mike** in
the menu, whether it works or not.

## What you need

- Windows 11 or Windows 10 (version 2004 or later), 64-bit. Windows 10 has not been tried.
- A USB-C port that carries video (DisplayPort), or an adapter that gives the glasses both video and USB data.
- For floating screens: **Parsec's Virtual Display Driver**, free from Parsec, no Parsec app or account needed.
  Pocket XR shows you how to get it (control panel, "How to add floating screens"), and notices it by itself once it
  is installed. Windows asks an administrator once, because it adds a driver. Without it, the glasses show your PC's
  own screen. Pocket XR is not made by or affiliated with Parsec.

## Install

1. Download `PocketXR-Windows-0.1.0-preview.zip` and unzip it anywhere.
2. Open `PocketXR.exe`. The file is not signed yet, so Windows may say "Windows protected your PC": choose
   **More info**, then **Run anyway**.
3. A short tour opens the first time. Pocket XR lives in the notification area by the clock, under a pair of glasses.

## What is in it

- 1 to 3 floating screens beside your PC's own screen; move the pointer off its edge to reach them.
- Held still by head tracking; 3D side by side; Body Anchor and Smooth Follow; head scroll; Recentre.
- Layouts (side by side, main + side, stacked, theater), size, distance, curve; move all screens or one by dragging
  in the control panel; saved arrangements.
- Steady mode on the XREAL Aura (the glasses hold the picture themselves).
- Keyboard shortcuts in any app, all changeable in Settings (Ctrl+Alt and a key to begin with).
- A launcher for your apps, Pocket mode (your PC's screen goes dark, everything in the glasses), a game controller
  as a remote.

## Not yet

- Glasses other than the XREAL Aura: the XREAL One series, Air family and VITURE are written for but not tried on a
  PC.
- Hand tracking is not in the Windows version yet.
- Pocket mode, game controllers and the launcher starting apps are built but not yet tried with glasses.

## What it sends to the glasses

Pocket XR reads the glasses' motion. On the Aura and the One series it also sends the settings Pocket XR on Android has
already proven: 2D and 3D, Ultrawide, screen size, Spatial Anchor and follow mode, and puts them back when it quits.
It never sends a firmware command or a guessed one.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE or Parsec.
