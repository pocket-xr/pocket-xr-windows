# Moonlight's stream core for PC screens

Pocket XR for Windows streams another computer's desktop (Apollo or Sunshine) into a PC screen with
[moonlight-common-c](https://github.com/moonlight-stream/moonlight-common-c), Moonlight's protocol core, under the
GPLv3 (the same licence as Pocket XR; `moonlight-common-c/LICENSE.txt`). It is the copy Pocket XR for Android uses
(commit 874ac95, with its `enet` and `nanors` submodules), unchanged.

Pocket XR's own files here:

- `pxr_moonlight.c`: a flat C interface for the C# app (`src/PocketXR/PcScreens/Moonlight.cs`).
- `pxr_crypto_bcrypt.c`: moonlight-common-c's crypto on Windows' own CNG, in place of `src/PlatformCrypto.c` and
  OpenSSL, so nothing else needs bundling.

Built into `moonlight-pxr.dll` (x64) by `build.ps1`, which uses [Zig](https://ziglang.org)'s C compiler.
The DLL is copied beside `PocketXR.exe` by the app's build.
