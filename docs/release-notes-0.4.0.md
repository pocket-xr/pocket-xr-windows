# Pocket XR for Windows 0.4.0

- **Let my phone stream this PC**, with no Apollo needed. One switch in the control panel (and the menu) makes this PC
  a host that Pocket XR on your phone streams from: this PC's desktop in your glasses on the phone, at the phone's
  window shape (a virtual display of its own, when Apollo's SudoVDA driver is on the PC), with the mouse, keyboard,
  scrolling, sound, the pointer drawn in, and the clipboard both ways. It encodes in AV1, HEVC or H.264 on the graphics
  card. On Mike's PC (AMD graphics) the phone took AV1 at 120 frames a second, with a 7 to 10 ms network round trip.
  Tried with one phone and one PC so far: NVIDIA and Intel graphics have not been tried.
- **The Pocket XR PC helper is part of the app now.** One app, one icon in the notification area. It does what the
  helper did (the phone finds this PC, pairs with no typing, starts SteamVR for PC VR, gives Apollo the phone's PIN,
  keeps your PC's own sound device after PC screens) on the same port, so phones need nothing new. If the separate
  helper is on the PC, Pocket XR stops it the first time it starts, takes it out of Windows' start list (and starts
  with Windows in its place, if the helper did), and keeps the phones it had paired. The helper's files are left where
  they are; you can delete them. When a phone shows a PIN and nothing gives it for you, Pocket XR asks you to type it.
- **Another computer in these glasses**: another computer's desktop (Apollo, Sunshine, or Pocket XR for Windows with
  the switch above) as one of your floating screens, streamed with Moonlight's protocol. It is for glasses plugged into
  this PC; glasses on your phone open PC screens from Pocket XR on the phone. With no glasses plugged in it says so,
  and this PC never streams itself.
- **Smooth sound** when your phone streams this PC: the sound is now sent at an even pace (one packet every 5 ms), as
  Sunshine does. Before, it arrived in bursts and sounded choppy.
- **XREAL XBX A01 and A01+**: head tracking over USB on their HID interface 1. Turn it on in Settings, Experimental.
  Untested: if you have one, **Send logs to Mike** in the menu helps a lot.

Not tried yet: pairing a phone that was not paired before (the "Allow" question and the PIN box), starting PC VR
through the app instead of the old helper, the Apollo setup from the menu, waking a sleeping PC from the phone (the PC
now gives its network card's address for it), and another computer in glasses plugged into this PC (the PC that tried
it has no USB-C video).

Windows asks once whether Pocket XR may use your network the first time the switch is on: allow it on private
networks so your phone can reach the PC.

## Install

1. Download `PocketXR-Windows-0.4.0.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo. It includes Moonlight's stream core (moonlight-common-c) and parts ported from Sunshine and Apollo, all GPLv3,
as Pocket XR is.
