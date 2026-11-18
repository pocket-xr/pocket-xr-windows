# Pocket XR for Windows 0.2.0

**Hand tracking.** Point at your screens and pinch to click, with no mouse in your hand.

## Hand tracking

Turn it on with **Ctrl+Alt+H**, the **Hands** button in the launcher, or Settings, then Hands.

- **Air trackpad**: with a webcam that faces you, your hand moves the pointer over the screen you are looking at, as on
  a trackpad in the air. Thumb and first finger together is a press; apart again is a click; pinch and move to drag.
  **Set your reach** puts the middle of the screen where your hand is comfortable.
- **Hand ray**: with glasses that have a camera Windows can open, your hand aims the pointer like a ray, with a
  five-dot calibration.
- **Look and pinch**: the pointer goes where you look, and a pinch anywhere clicks.
- Palm to the camera and a short pinch opens the launcher; pinch and hold recentres.
- Settings, then Hands, shows the camera's picture with your hand on it, so you can see what it sees.

It runs Google's MediaPipe hand models (the same ones as the Android app) on your PC. The camera's picture never
leaves your PC and is never kept. Windows asks once whether Pocket XR may use the camera (Settings, Privacy, Camera).

**What has been tried:** the air trackpad, on a desktop PC with a Logitech C920 webcam (18 November 2026): about 25
hand readings a second, each in under 20 ms. The hand ray and Look and pinch have not been tried on a PC yet: the hand
ray needs glasses whose camera Windows can open, and XREAL's Aura firmware 16.1.04 and later turns the Aura's camera
off. If you try it, **Send logs to Mike** in the menu helps a lot, whether it works or not.

Have the light on your hand, not behind it. Clicks reach every app except one run as administrator.

## Install

1. Download `PocketXR-Windows-0.2.0.zip` and unzip it anywhere (over 0.1.0 is fine: your settings are kept).
2. Open `PocketXR.exe`. It is not signed yet, so Windows may say "Windows protected your PC": choose **More info**,
   then **Run anyway**.

Everything else is as in 0.1.0: see its notes for what you need and what is in it. Still only the XREAL Aura has been
used with the Windows version.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec or Google. The hand models are
Google's (Apache 2.0) and ONNX Runtime is Microsoft's (MIT): see THIRD-PARTY.txt.
