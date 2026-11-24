# Pocket XR for Windows

Your PC's apps on your XR glasses, as screens held still in space while you turn your head.

It is the Windows version of [Pocket XR](https://github.com/pocket-xr/pocket-xr), the free and open source spatial
desktop for Android phones, and a sibling of Pocket XR for Steam Deck and Pocket XR for Mac. Same glasses, same
rules, no account and no paid tier.

> **What has been tried.** Seen working on 17 November 2026 on a Steam Deck running Windows 11 with the XREAL Aura: floating
> screens in the glasses, held still by head tracking, in 3D. On 18 November 2026 a tester reported the XREAL 1S working on
> Windows 11 for hours, in normal and Steady mode. On 23 November 2026 a tester's XREAL XBX A01+ streamed head tracking
> on Windows (USB HID, after its control chip is woken; "your view moves"); its floating screens in the glasses are not
> yet seen working (his copy was 0.4.1, from before its display was recognised in 0.4.2). The XBX A01 is untested.
> Other glasses have not been tried yet.
> See "Where it stands" below.

## How it is meant to work

Pocket XR adds 1 to 3 screens to your PC, beside its own screen. Move the pointer off the edge and it is on them:
drag apps there, click and type as usual. In your glasses, those screens float in front of you and stay put as you
turn your head. With glasses that do 3D side by side, they sit at a real depth.

Pocket XR lives in the notification area (the icons by the clock), under a small pair of glasses. A click opens the
**control panel**; a right click opens the menu.

- **Control panel**: what is plugged in, a map of the displays (click a screen to send the pointer there), a pad to
  drag the screens about and scroll to resize them, saved arrangements, the layout (side by side, main + side,
  stacked, theater), size, distance and curve, 3D and Steady mode (with its size and the gap between screens).
  Steady mode is remembered: if it was on, it comes back on when the glasses connect.
- **Settings**: a live picture of your Space, eye distance, brightness, the glow around the screen you look at, edge
  fade, tracking (Body Anchor or Smooth Follow), head scroll, the tracking readings, a Glasses report, settings saved
  to and restored from a file, open when you sign in.
- **Taps on the glasses** (Settings, Head tracking; off by default; untested with real taps): tap the frame twice to
  recentre, three times for the next layout (the closest to the Steam Deck's "switch view": the layout is the one view
  change every model has, the same as Ctrl+Alt+L). Read from the glasses' own motion sensor, for every model whose head
  tracking Pocket XR reads, with the Steam Deck's detector and thresholds: a jolt of 0.8 g or more from the 30 ms
  average, quiet before it, back within 40 ms, while the head turns slower than 60 degrees a second, in runs of 2 or 3.
  Asked for by Pierre Zammit.
- **Text size** (Settings, Picture: Standard, 125%, 150%): larger text on every screen, the screens as wide as before.
  Each screen keeps its 1920 x 1080 pixels and Windows draws it at 125% or 150% (its own per-display scale), so it has
  the room the Mac's 1536 x 864 and 1280 x 720 give and stays sharp. Also for the screens your phone streams. Logged
  for each screen. Set and read back on a virtual display (SudoVDA) on a PC; not yet seen in glasses.
- **Readability** (Settings, Picture; off by default; untested on glasses), as on the Mac: one floating screen at
  1440 x 810, 34 degrees wide, about 42 of its pixels a degree, as a 1080p pair of glasses like the XREAL Air shows (the
  usual screens are 48). Parsec's virtual display driver offers no 1440 x 810, so with it the nearest 16:9 size it
  offers is used (1600 x 900, 37.8 degrees wide) and the width is set from it, still about 42 pixels a degree. It changes only the screen
  count, the screens' width and screens moved by hand, keeps what they were, and puts exactly those back when turned
  off; both are logged with the values before and after. A phone streaming this PC with Readability on gets a screen
  three quarters of its size, drawn larger. After an idea by Dmitriy Cherdantsev, a Pocket XR for Mac tester.
- **Tour**: eight steps, opens the first time.
- **Launcher** (Ctrl+Alt+Space), **Pocket mode** (Ctrl+Alt+K), a **game controller** as a remote.
- **Hand tracking** (Ctrl+Alt+H): a webcam that faces you makes an air trackpad (move your hand, pinch to click), and
  glasses with a camera Windows can open aim a hand ray. Palm to the camera and a short pinch opens the launcher; a held
  pinch recentres. It runs Google's MediaPipe hand models on the PC, with ONNX Runtime; the picture never leaves the PC.
- **Another computer in these glasses** (the menu and the control panel): another computer's desktop as one
  of the floating screens, streamed with Moonlight's protocol from Apollo, Sunshine or Pocket XR for Windows on that
  computer. For glasses plugged into this PC. Glasses on your phone open PC screens from Pocket XR on the phone. With
  no glasses plugged into this PC it says so instead of opening anything, and this PC never streams itself.
- **Let my phone stream this PC** (the control panel and the menu): this PC's screens on your phone's glasses, with no
  Apollo needed. It also does what the separate Pocket XR PC helper did (finding this PC, pairing with no typing,
  starting SteamVR for PC VR, Apollo's PINs), so that helper is no longer needed: on first start Pocket XR stops it,
  takes it out of Windows' start list, and keeps the phones it had paired.
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
- For **Let my phone stream this PC**: a graphics card that encodes video (AV1, HEVC or H.264). Tried on AMD
  graphics only; NVIDIA and Intel are untested. With SudoVDA on the PC, each stream gets a display of the phone's
  window shape; without it, the main display is streamed, scaled. Windows asks once whether Pocket XR may use the
  network: allow it on private networks.
- PC screens and the host use Moonlight's stream core, `native/moonlight` (moonlight-common-c, GPL-3.0), built into
  `moonlight-pxr.dll` with Zig's C compiler by `native/moonlight/build.ps1`.

What is new in each version: [docs/release-notes-0.4.5.md](docs/release-notes-0.4.5.md) and the notes beside it.

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
PocketXR --hands-test FILE      the hand detector on a picture: the hand's 21 points
PocketXR --hands-camera 15 C920 a camera and the hand detector alone for 15 s: what they see
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

## Thanks

- Pierre Zammit, for his XBX A01+ logs, which led to XBX A01 and A01+ support (0.4.0) and to waking their control chip
  first (0.4.1), and to finding its display (0.4.2); for his report of the control panel running off a second
  screen (0.4.3); for asking for taps on the glasses (0.4.4); and for the logs that showed his head tracking working
  and led to clearer logs (0.4.5).

## Licence

GPL-3.0, as Pocket XR. Pocket XR is a community project, not made by or affiliated with XREAL or VITURE.

The hand models in `src/PocketXR/Hands/Models` are Google's MediaPipe hand landmarker, Apache 2.0 (see the README
there). ONNX Runtime is Microsoft's, MIT licence.

The first Windows prototype (Python, November 2026) is kept in `pocketxr/` and `tests/` for its test notes; the app is
in `src/`.
