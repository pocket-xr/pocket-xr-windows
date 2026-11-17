# Pocket XR for Windows

Your PC's apps on your XR glasses, as screens held still in space while you turn your head.

It is the Windows version of [Pocket XR](https://github.com/pocket-xr/pocket-xr), the free and open source spatial
desktop for Android phones, and a sibling of Pocket XR for Steam Deck and Pocket XR for Mac. Same glasses, same
rules, no account and no paid tier.

> **Preview.** Seen working on 17 November 2026 on a Steam Deck running Windows 11 with the XREAL Aura: floating
> screens in the glasses, held still by head tracking, in 3D. No other glasses, and no other PC, have been tried yet.
> See "Where it stands" below.

## How it is meant to work

Pocket XR adds 1 to 3 screens to your PC, beside its own screen. Move the pointer off the edge and it is on them:
drag apps there, click and type as usual. In your glasses, those screens float in front of you and stay put as you
turn your head. With glasses that do 3D side by side, they sit at a real depth.

Pocket XR lives in the notification area (the icons by the clock), under a small pair of glasses. A click opens the
**control panel**; a right click opens the menu.

- **Control panel**: what is plugged in, a map of the displays (click a screen to send the pointer there), a pad to
  drag the screens about and scroll to resize them, saved arrangements, the layout (side by side, main + side,
  stacked, theater), size, distance and curve, 3D and Steady mode.
- **Settings**: a live picture of your Space, eye distance, brightness, the glow around the screen you look at, edge
  fade, tracking (Body Anchor or Smooth Follow), head scroll, the tracking readings, a Glasses report, settings saved
  to and restored from a file, open when you sign in.
- **Tour**: eight steps, opens the first time.
- **Launcher** (Ctrl+Alt+Space), **Pocket mode** (Ctrl+Alt+K), a **game controller** as a remote.
- Every control has a key that works in any app, all with Ctrl+Alt: R recentre, S Steady mode, = and - size,
  ] and [ distance, . and , curve, L next layout, D 3D, P pointer to the screen you look at, Space the launcher,
  K Pocket mode, H hand tracking.

## What it needs

- Windows 10 (2004) or Windows 11, 64-bit.
- A USB-C port that carries video (DisplayPort alternate mode), or an adapter that gives the glasses both video and
  USB data. Without video the glasses show nothing; head tracking still works.
- For the floating screens: a **virtual display driver**. Windows has no way for an app to add a monitor by itself;
  it takes a driver, and a driver takes an administrator to install once. Pocket XR uses
  **Parsec's Virtual Display Driver**: free, from Parsec Cloud, no Parsec app or account needed. Pocket XR shows how
  to get it ("Add floating screens", in the control panel), and notices it by itself once it is installed. It can
  also be installed with `winget install --id Parsec.ParsecVDD -e`. SudoVDA (the driver the Apollo streaming app
  installs) works too. Without a driver, the glasses show the PC's own screen. Pocket XR itself never asks for
  administrator rights, and is not made by or affiliated with Parsec.

## Build

Needs the .NET 8 SDK.

```
dotnet run --project src/PocketXR.Checks
```

runs the checks of the shared logic (the same checks as Pocket XR for Mac; no glasses needed). Then:

```
dotnet build src/PocketXR -c Release
```

## Check things without the app

```
PocketXR --usb                  the glasses on USB, the displays, the glasses' network link
PocketXR --imu-test 20          head tracking alone for 20 s: yaw and pitch, reports a second (only reads)
PocketXR --driver               is the virtual display driver there and answering (adds no display)
PocketXR --render-ui FOLDER     every window drawn to PNG files, with no glasses and no window
PocketXR --render-space OUT.png the Space drawn off screen as the glasses would get it (add --sbs for 3D)
PocketXR --stand-in 2           a test: monitor 2 stands in for the glasses' display
```

The log is at `%LOCALAPPDATA%\Pocket XR\pocket-xr.log`; **Send logs to Mike** makes a zip of it in Downloads with
email addresses, your Windows user name and IP addresses (other than the glasses' own) taken out.

## What it sends to the glasses

Pocket XR reads the glasses' motion. On the Aura and the One series it also sends the settings Pocket XR on Android,
the Steam Deck and the Mac have already proven: 2D and 3D, Ultrawide, screen size, Spatial Anchor and follow mode. It
never sends a firmware command or a guessed one.

## Where it stands

See [docs/PLAN.md](docs/PLAN.md): what was seen working, on what, and when; what is written but has never run with
glasses; and what is not written yet.

## Licence

GPL-3.0, as Pocket XR. Pocket XR is a community project, not made by or affiliated with XREAL or VITURE.

The first Windows prototype (Python, November 2026) is kept in `pocketxr/` and `tests/` for its test notes; the app is
in `src/`.
