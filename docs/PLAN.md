# Pocket XR for Windows: plan

Written 16 November 2026. It replaces the plan of 15 November, which was for the Python prototype; that prototype's
test results are kept at the end, because they are still the only tests made with the glasses on Windows.

Everything marked **to prove** has to be seen working with real glasses before it is called done. Only the XREAL
Aura has been used with Pocket XR on Windows, and only on a PC with no USB-C video, so nothing here has been seen
*in* the glasses yet.

## What it is

Your PC's apps on your XR glasses, as screens held still in space while you turn your head: the Windows version of
Pocket XR, matching Pocket XR for Mac 0.3 feature for feature wherever Windows allows. Free, open source (GPL-3.0),
no account, no paid tier. Same glasses, same rules about what is sent to them.

## How it works

```
 PC apps live on 1 to 3 floating screens (virtual displays made by a display driver)
        │  Windows treats them as real monitors: windows, full screen, Win+Shift+arrow all work
        ▼
 Desktop Duplication (DXGI), one per floating screen, straight into Direct3D textures; the pointer is drawn in
        ▼
 Direct3D 11 renderer, a borderless window over the glasses' own display, one frame per glasses refresh
        │  each floating screen is a panel in space, around the head
        │  3D side by side when the glasses are at 3840x1080
        ▲
 Head tracking from the glasses (TCP 169.254.2.1:52998 on the Aura and One family), read just before each frame
```

Input needs no tricks: the real pointer moves onto a floating screen like onto any monitor.

## The technology, and why

| Choice | Why | Cost |
| --- | --- | --- |
| **C# on .NET 8** | One language for the shared logic, the windows and the Direct3D renderer. The Mac's Swift logic ports to it nearly line for line, with its checks. Builds with the free .NET SDK alone, no Visual Studio. | A release is one .exe of about 70 MB that carries .NET with it (or a small one that needs the .NET 8 Desktop Runtime). |
| **WPF, built in code** | Windows' most settled toolkit; draws exactly what it is told (the look is custom throughout); can draw any window to a picture file with no window, which is how the design is reviewed without glasses. | Not the newest toolkit (WinUI 3), but that one cannot draw off screen and is harder to ship. |
| **Direct3D 11** (through Vortice, MIT) | What Desktop Duplication hands its pictures to with no copy; a flip-model swap chain with a waitable object gives one frame of latency. The Mac's Metal shaders port one for one. | None worth naming. |
| **DXGI Desktop Duplication** | In Windows since 8, no permission prompt, no yellow border, works on virtual displays. The prototype read each monitor at 57 pictures a second with it. | Whole displays only; the pointer has to be drawn in by Pocket XR. |
| **An existing virtual display driver** (SudoVDA first) | See the next section. | One install by an administrator, of someone else's driver. |

The first prototype was Python with no packages, because the PC had no build tools. It proved the glasses' network
link and tracking on Windows. It is not how an app for PC users ships, so the app is now C#; the prototype stays in
`pocketxr/` for its notes.

## Floating screens need a driver (the part that needs an administrator)

Windows has no way for an app to add a monitor. It takes an *indirect display driver*, and:

| | Needs |
| --- | --- |
| Installing any driver | An administrator, once. |
| A driver Windows will load on an ordinary PC | A signature Windows trusts: Microsoft's own (through its hardware programme, which needs an EV certificate, a few hundred dollars a year) or an open source foundation's. |
| A driver built at home | "Test-signing mode", a switch that weakens Windows' security for the whole PC and upsets some games' anti-cheat. Not something to ask of users. |

So Pocket XR does not carry its own driver yet. The ways, in order:

1. **Use a driver the person installs** (now). Pocket XR talks to **SudoVDA** (SudoMaker's Virtual Display Adapter,
   MIT / CC0; the one the Apollo game streaming host installs). It adds a display when asked and takes it away again
   when asked, or by itself a few seconds after the app that asked stops answering, so a crash never leaves a display
   behind. Unlike macOS, displays can be added and removed while Pocket XR runs, so changing the number of screens
   needs no restart. Pocket XR itself never needs administrator rights. **To prove**: adding a display, and capturing
   it. Checked so far (16 November, this PC, where SudoVDA is already installed): the driver opens and answers its
   version with no administrator rights. No display was added.
2. **MTT's Virtual Display Driver** (MIT, signed through the SignPath Foundation) as a second choice, later: it is
   set up through a settings file rather than asked for displays on demand.
3. **Pocket XR's own driver**, later, if the project gets a signing certificate. Weeks of work and a yearly cost.
4. **With no driver at all**: Pocket XR can still show the PC's real screens in the glasses (what the prototype did).
   Not built into the new app yet, except as the `--stand-in` test.

## What else needs care on Windows

| Thing | State |
| --- | --- |
| Administrator rights | Never asked for. Shortcuts (RegisterHotKey), the tray icon, capture, the glasses' network and "open when I sign in" (the person's own Run key) all work without. |
| Permissions | None. Windows has no Screen Recording or Accessibility prompts for this. |
| The glasses' network | Windows 11 picks its own driver ("UsbNcm Host Device") with no setup. **Seen working** with the Aura, 15 November. Windows 10 has no such driver in the box: **to check**. |
| Code signing | An unsigned .exe gets a SmartScreen warning. The plan is the same Azure signing as the PC VR helper. |
| Ctrl+Alt shortcuts | On some keyboard layouts Ctrl+Alt is AltGr, which types symbols. If that clashes, the shortcuts can be turned off in Settings; choosing other keys is not built yet. |
| A click by a pinch (hand tracking) | Windows lets an app click for the user (SendInput) without a permission, but not in windows run as administrator. |

## Glasses

| Glasses | Head tracking on Windows | 3D side by side | Notes |
| --- | --- | --- | --- |
| XREAL Aura | Seen working (15 November, prototype; the new app: see the results below) | Pocket XR asks for it; **to prove** on Windows | Mike's glasses: the first target |
| XREAL One, One Pro, One Pro+, 1S | Written, not tried on a PC | Written, not tried on a PC | Follow mode, Stabilizer off, as on Android |
| XREAL Air family, VITURE | Not written for Windows yet (USB HID) | From the glasses' own menu | The protocols are ported with their checks; the Windows USB reader is not |
| Other glasses | None: screens shown flat | No | |

What is sent to the glasses is exactly what the Mac and the Deck send, from the same allow list: 2D and 3D,
Ultrawide, screen size, Spatial Anchor, follow and the canvas recentre. Never 10079, never 10325 to 10328, never a
guessed id.

## The shared logic

`src/PocketXR.Core` is `Sources/PocketXRCore` from Pocket XR for Mac, file for file, in C#: head fusion and
prediction, draw smoothing, the still lock, layouts, curve, the display grid, the Steady mode plan and the control
wire, settings (the same file format, so settings saved on a Mac open on a PC), the tour's steps, launcher search,
controller mapping, hand gestures and calibration. `src/PocketXR.Checks` is the Mac's `Checks/`, check for check.
`docs/PORTING.md` has the rules that keep the two readable side by side.

## The windows

All in Pocket XR's look ("Light on glass"), built in code so that `PocketXR --render-ui FOLDER` draws every one to a
picture with no glasses. `docs/UI.md` describes the look and the shared parts.

- **Control panel**: status, the map of displays, the place pad, arrangements, layout, size, distance, curve, 3D,
  Steady mode.
- **Tray icon and its menu**: the Mac's menu bar menu.
- **Settings window**, **the tour** (eight steps), **the launcher**, **Pocket mode**.

## Order of proof (what Mike can do, and with what)

On this PC (no USB-C video), with the Aura on any USB port:

1. `PocketXR --usb` lists the Aura and its network link.
2. `PocketXR --imu-test 20` prints yaw and pitch that follow the head, about 1000 reports a second.
3. The app: the control panel says "XREAL Aura", the dot lights, "Your view" on the pad moves with the head; the
   settings link opens (the log says so) and Steady mode's switch becomes usable.
4. `PocketXR --stand-in 2`: the second monitor stands in for the glasses and shows the main screen as a panel that
   moves against the head. This shows tracking and drawing together, not a screen held still in the glasses.

For the picture in the glasses, one of:

| Way | Cost |
| --- | --- |
| A Windows laptop or handheld with a USB-C port that carries video | Copy the app's folder over; nothing to install but the driver for floating screens. |
| A DisplayPort to USB-C adapter of the kind sold for USB-C monitors and pen displays (a DisplayPort plug and a USB plug on the PC side, USB-C with power on the other) | An adapter to buy; which ones work with XREAL glasses is **to check** before buying. |

Then: 5. floating screens appear in Windows' display settings; 6. the glasses show them, with the pointer; 7. turning
the head leaves them in place; 8. 3D; 9. Steady mode; 10. quit puts the glasses back.

## Test results

### 16 November 2026, the new app on Mike's desktop PC, no glasses plugged in

Windows 11 Pro (26200), Radeon RX 9070 XT, two Sceptre M24 monitors at 1920x1080 and 180 Hz, .NET SDK 8.0.428.

- **Shared logic:** all 965 checks pass (`src/PocketXR.Checks`), the Mac's checks ported one for one.
- **Build:** the app builds in Release with no warnings.
- **Windows drawn to pictures** (`PocketXR --render-ui`): 73 pictures of the control panel, tray menu, settings
  (every pane, and at the smallest size), the tour (every step, and the setup step in each state), the launcher
  (12 states, made-up apps), Pocket mode and the edge hint. Looked at; words that were untrue on Windows were fixed.
- **The Space drawn off screen** (`PocketXR --render-space`): 2D, 3D side by side, curved, every layout. The
  pictures are the right way up, unmirrored, the left eye in the left half, and turning the head moves the panels the
  right way. So the Mac's maths carries over to Direct3D unchanged.
- **Capture** (`PocketXR --capture-test`): both monitors captured with no copy through memory, first picture in
  0.1 s, up to 180 pictures a second (as fast as the screen changes), the pointer's shape received.
- **The drawing loop**, in a small ordinary window in a corner: 177 frames a second on the 180 Hz monitor, the same
  with both monitors being captured.
- **The virtual display driver** (`PocketXR --driver`): SudoVDA opens and answers with no administrator rights
  (protocol 0.2.1, a test build, watchdog 3 s). **No floating screen was added**: that changes the PC's display set
  up and waits for Mike's yes (`PocketXR --driver-test`).
- **USB listing:** the PC's 16 USB devices listed with names; the glasses' detection only seen with no glasses in.
- **The launcher's app list** (read only): 220 apps from the Start menu and the Store, all with icons. No app was
  started.
- **Not run:** anything with the glasses (they were not plugged in), the app itself as a running program (its windows
  have only been drawn to pictures), a floating screen, Pocket mode, the game controller, USB HID tracking, and the
  picture in the glasses (no USB-C video on this PC).

### 17 November 2026, the Steam Deck running Windows 11 Home, with the Aura on its USB-C port

Reached over Wi-Fi (`ssh steamdeck`, Windows' OpenSSH Server turned on by Mike); the app is run in Mike's desktop
session through `scripts/deck.sh` (an SSH login cannot see the desktop).

- **First picture in the glasses from Windows.** The Aura's display ("XREAL 1 Ultra", 1920x1080 at 90 Hz, 3840x1080
  at 60 Hz in 3D) is a normal Windows display on the Deck.
- **Head tracking:** 1000 reports a second, nothing dropped (`--imu-test`). In the app, Mike: everything good apart
  from the points below.
- **Windows put the glasses in Duplicate** the first time and again after every switch to 3D (the Aura's 3D mode is
  a new monitor to Windows). Pocket XR now never draws on mirrored glasses, and asks Windows to extend (as Win+P,
  Extend; Mike said yes): done in a few milliseconds each time.
- **The Deck's own screen** (shown when there are no floating screens) is turned in hardware (Rotate270); it is
  turned back when drawn. Mike: the picture right way up; the arrow looked turned and a little off (fixed, not yet
  looked at again).
- **Floating screens through Parsec's Virtual Display Adapter**, already installed on the Deck, with no
  administrator rights: 3 screens of 1920x1080 at 60 Hz, added in about 3 s each, captured and drawn in 3D. Placed
  to the right of the Deck's screen, laid out as in the glasses; Mike: crossing onto them works. Going down off them
  landed on the glasses' display (now moved to the left of the Deck's screen; not yet tried).
- **The Aura drops off the Deck's USB** (USB and network gone, display stays): first seen about 8 s after switches to
  3D, then in 2D with nothing sent, and then with Pocket XR not running at all (on 35 s, off 30 s, on again). So it
  is not Pocket XR. The Deck was on battery (52 %), the Aura powered from its one USB-C port. Next to try: a powered
  USB-C hub or dock, the cable reseated, the glasses cooled.
- **After charging the Deck to 98 % and plugging the Aura in directly:** no drop in 75 s with Pocket XR quit, nor
  in three runs of the app through the switch to 3D. So the drops were most likely power on a low battery (not proven).
- **Parsec's screens went** 6 s after they were made in one run (keep-alive from the shared thread pool, and Parsec's
  own service runs on the Deck). Now a dedicated keep-alive thread every 30 ms, a second look before a screen counts as
  gone, and screens made again (3 times in 2 minutes at most). Clean start seen at 10:05: 2 screens, 3D, no remake.
- **Windows started remotely came up behind other windows**; they are now put on top for a moment when Windows will
  not bring them forward. Seen on the Deck: the tour in front, the control panel behind it.
- Mike, 17 Nov: crossing onto the floating screens works; "all good".

## The Python prototype's test results (15 November 2026)

### 15 November 2026, this PC, no glasses plugged in

Windows 11 Pro (26200), Radeon RX 9070 XT, two Sceptre M24 monitors at 1920x1080 and 180 Hz, Python 3.12.6.

- **Offline tests:** 13 pass (`tests/test_offline.py`): the stream framer, the fusion's directions and zero learning,
  the control link's bytes against Pocket XR's, the refused ids, the USB id rules.
- **Displays:** both monitors are listed with their own names, places and refresh rates.
- **Capture:** each monitor is read at 57 pictures a second (the limit is 60), 1920x1080.
- **Drawing:** in a test window, and full screen on the second monitor standing in for the glasses, with a made-up
  head turn: about 150 to 170 frames a second, the main screen straight ahead, the second beside it, the pointer
  drawn on its panel. Side by side drawing (two eyes) checked in a test window. Saved frames were looked at.
- **Not proven:** anything with real glasses. The USB network, the stream, the "XREAL 1 Ultra" display, the picture
  in the glasses, the steadiness of a held window, the pointer push-back from the glasses' display, 3D, `--settings`.

### 15 November 2026, the Aura on a USB data port of this PC (no video)

- **USB:** Windows lists the XREAL Aura (3318:043A) as a composite device.
- **Network link: proven.** Windows picked its own NCM driver with no setup: adapter "UsbNcm Host Device", address
  169.254.2.10 from the glasses' DHCP. The ECM function ("CDC ECM") shows an error in Device Manager, as expected:
  Windows has no ECM driver. It is not needed.
- **The Aura starts its USB network with no video going to it.**
- **Sensor stream: proven.** 7993 reports in 7.99 s of the glasses' clock, 1000 a second, from 169.254.2.1:52998,
  no bytes skipped, no wrong lengths. Gravity reads mostly along +y, as on the Deck and the phone.
- **Head tracking in the app:** in a test window on the PC, 180 frames a second with 1000 reports a second for 15 s;
  yaw and pitch moved with the glasses. The gyro zero was not learnt in that run: the glasses were never still for
  two seconds.
- **Also seen:** a "UVC Camera 0" node on the Aura. Not opened, not used.
- **Nothing was sent to the glasses.**
- **Still not proven:** whether a window looks still to someone wearing the glasses (needs the picture in the
  glasses, so USB-C video), the "XREAL 1 Ultra" display on Windows, the pointer push-back, 3D, `--settings`.

**Second test the same evening, the second monitor standing in for the glasses (`--display 1`):**

- Full screen on the 180 Hz monitor with the Aura's real tracking: 180 frames a second (167 in the first 5 s),
  1000 reports a second throughout, the main screen captured at 54 to 59 pictures a second.
- The gyro zero was learnt within the first 10 s.
- Recentre (Ctrl+Alt+R) and bigger / smaller (Ctrl+Alt+Up / Down) were used and logged.
- Mike, watching the monitor while moving the glasses: "Yes, tracks right."
- This shows the tracking follows the head the right way. It does not show a window held still in the glasses.
