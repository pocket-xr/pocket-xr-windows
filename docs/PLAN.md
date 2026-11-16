# Pocket XR for Windows: plan

Written 15 November 2026, before any test with glasses on Windows. Everything marked **to prove** has to be seen
working on Mike's PC with his XREAL Aura before it is called done. Test results go at the end.

## What it is

Your PC's screens on your XR glasses, as windows held still in space while you turn your head. It is the Windows
version of Pocket XR, like the Steam Deck and Mac versions: free, open source (GPL-3.0), no account, no paid tier.
Same glasses, same rules about what is sent to them.

## How it works

```
 PC screens (real monitors now, virtual displays later)
        │  Desktop Duplication (DXGI), one per screen, at most 60 pictures a second
        ▼
 One borderless window, full screen on the glasses' own display, one frame per glasses refresh
        │  each PC screen is a panel in space around the head
        │  3D side by side when the glasses are at 3840x1080
        ▲
 Head tracking from the glasses (TCP 169.254.2.1:52998, about 1000 reports a second)
```

**Input needs no tricks.** The real mouse and keyboard keep working on the PC's screens. Desktop Duplication leaves
the pointer out of its pictures, so Pocket XR draws its own arrow on the panel. The pointer is kept off the glasses'
own display, where it could not be found.

## The stack, and why

| Choice | Why | Cost |
| --- | --- | --- |
| Python 3 with `ctypes`, no packages | This PC has Python 3.12 and no C++ or .NET build tools. The Deck's tracking, sensor, control and Space code is Mike's own Python, tuned on his Aura, and moves over unchanged. | Not how a finished Windows app ships. A release would be a frozen .exe (PyInstaller) or a rewrite in C# or C++. |
| OpenGL through `opengl32.dll` | The Deck draws this way; the drawing code ports as it is. | Captured pictures cross from Direct3D to OpenGL through memory (about 8 MB a picture at 1080p). Fine at 60 a second; a Direct3D renderer would avoid it. |
| DXGI Desktop Duplication | In Windows since 8, no permission prompt, no yellow border, works on virtual displays too. | Whole screens only. Single app windows need Windows.Graphics.Capture, later. |

## Extra screens

Windows has no way for an app to add a monitor by itself. It needs an indirect display driver (IDD).

| Way | Cost |
| --- | --- |
| **No driver: show the real monitors** | Nothing to install. This is the prototype. The screens are also still on the desk. |
| **Use an existing open source driver** (Virtual Display Driver, MIT) | One admin install by the user, of someone else's driver. Pocket XR would turn its screens on and off. This PC already has it, and SudoVDA, installed. **Recommended next step.** |
| Our own driver | Windows Driver Kit, weeks of work, and paid driver signing (an EV certificate or Microsoft's attestation). Not now. |

The prototype shows up to three screens that are not the glasses, the main one in the middle. A virtual display that
is already on shows like any other screen. Turning virtual displays on and off from Pocket XR is the next step, with
Mike's OK, because it changes the PC's display setup.

## The glasses' network link (the biggest unknown)

The glasses are a composite USB device with two network functions: CDC NCM (169.254.2.x) and CDC ECM (169.254.1.x).
Windows 11 has its own NCM driver (UsbNcm.sys); it has no ECM driver. So expect one new network adapter,
"UsbNcm Host Device", with 169.254.2.10 from the glasses' DHCP. Reports differ on whether Windows picks the NCM
driver by itself for every device, so **to prove**:

1. With the Aura plugged in, Device Manager shows USB 3318:043A and a new network adapter.
2. The adapter gets a 169.254.2.x address (or any 169.254 address).
3. `python -m pocketxr.probe` reads the stream and prints about 1000 reports a second.

If Windows does not pick the driver, the fix to try is choosing "UsbNcm Host Device" by hand in Device Manager
(an admin step, Mike's call).

This PC has several adapters with 169.254 addresses, so Pocket XR does not trust the routing table: it binds its
sockets to the local address on the glasses' link.

**The picture:** the glasses need DisplayPort over USB-C. This PC's graphics card may have no USB-C port, so the
picture may need an adapter that carries both DisplayPort and USB data. **To check with Mike.**

## Head tracking

The Deck's `fusion.py`, unchanged: gyro integration, tilt pulled toward gravity, gyro zero learnt only from two
seconds of real stillness, the first stillness in the first 5 s also recentres. 3DoF only: the Aura gives a PC host
orientation, not position.

## What is sent to the glasses

By default nothing: the sensor stream is only read. The Deck's settings control (`settingsctl.py`: Spatial Anchor
off, Ultrawide off, Follow, 3D side by side, each read back, put back on quit) is ported with the same allow list,
but runs only with `--settings`, and only after Mike says so. Never 10079, never 10325 to 10328, never a guessed id.

## Order of proof

1. **Find the glasses:** USB id, display name, network link, stream rate (`pocketxr.probe`).
2. **Head tracking:** the probe prints yaw and pitch; they follow the head and hold still.
3. **One screen in space:** the main screen as a panel on the glasses, with the pointer.
4. **Space:** several screens, recentre (Ctrl+Alt+R), bigger and smaller, nearer and further.
5. **2D / 3D:** with `--settings`, the glasses switch to 3840x1080 and each eye gets its own view.

## Risks

| Risk | What happens then |
| --- | --- |
| Windows does not bring up the glasses' network | No head tracking until the driver is chosen by hand. First thing tested. |
| No DisplayPort to the glasses from this PC | Tracking can be proven, the picture only on a monitor standing in for the glasses. |
| Python too slow for 120 frames a second | Lower capture rate; then the rewrite comes sooner. |
| The pointer or windows land on the glasses' display | The pointer is pushed back; windows are the user's to move for now. |

## Sources

- Microsoft's list of USB class drivers in Windows (UsbNcm.sys): https://learn.microsoft.com/windows-hardware/drivers/usbcon/supported-usb-classes
- Microsoft's NCM driver source: https://github.com/microsoft/NCM-Driver-for-Windows
- Desktop Duplication: https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api
- Virtual Display Driver (MIT): https://github.com/VirtualDrivers/Virtual-Display-Driver
- XREAL One and Aura stream (MIT): https://github.com/0xcaff/xr-tools
- Pocket XR for Steam Deck, `docs/FEASIBILITY.md`, and Pocket XR for Mac, `docs/PLAN.md`

## Test results

None yet.
