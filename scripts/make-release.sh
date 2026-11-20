#!/bin/bash
# Builds Pocket XR for Windows' download: one self-contained PocketXR.exe (no .NET to install), a short read-me and the
# licence, zipped as dist/PocketXR-Windows-<version>.zip. Runs the checks first and stops if any fails.
# Publishes nothing.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$LOCALAPPDATA/PocketXR-tools/dotnet/dotnet.exe}"
VERSION="$(tr -d '[:space:]' < VERSION)"
NAME="PocketXR-Windows-$VERSION"

"$DOTNET" run --project src/PocketXR.Checks -c Release

# PC screens and the host need Moonlight's stream core, built by native/moonlight/build.ps1 (not kept in git).
if [ ! -f native/moonlight/bin/moonlight-pxr.dll ]; then
    echo "native/moonlight/bin/moonlight-pxr.dll is missing: run native/moonlight/build.ps1 first" >&2
    exit 1
fi

rm -rf "dist/$NAME" "dist/$NAME.zip"
"$DOTNET" publish src/PocketXR -c Release -r win-x64 --self-contained true -p:Version="$VERSION" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -o "dist/$NAME" -nologo -v q

# ONNX Runtime's package also brings its .lib files, which only a C++ build would use.
rm -f "dist/$NAME"/*.lib
if [ ! -f "dist/$NAME/moonlight-pxr.dll" ]; then
    echo "moonlight-pxr.dll did not reach the download" >&2
    exit 1
fi
cp LICENSE "dist/$NAME/LICENSE.txt"
{
    echo "Pocket XR for Windows includes:"
    echo
    echo "- Google's MediaPipe hand landmarker models, converted to ONNX. Copyright Google LLC, Apache License 2.0:"
    echo "  https://www.apache.org/licenses/LICENSE-2.0"
    echo "- ONNX Runtime. Copyright Microsoft Corporation, MIT licence: https://github.com/microsoft/onnxruntime"
    echo "- moonlight-common-c (moonlight-pxr.dll), Moonlight's stream core. GPL-3.0, as Pocket XR:"
    echo "  https://github.com/moonlight-stream/moonlight-common-c"
    echo "  with ENet (Copyright Lee Salzman, MIT licence) and nanors (Copyright Joseph Calderon, MIT licence)."
    echo "- Parts of this PC's host ported from Sunshine (https://github.com/LizardByte/Sunshine) and Apollo"
    echo "  (https://github.com/ClassicOldSong/Apollo), GPL-3.0."
    echo "- Concentus (Opus in C#), BSD licence: https://github.com/lostromb/concentus"
    echo "- NAudio, MIT licence: https://github.com/naudio/NAudio"
} > "dist/$NAME/THIRD-PARTY.txt"
cat > "dist/$NAME/READ ME.txt" <<EOF
Pocket XR for Windows $VERSION

Your PC's apps on your XR glasses, as screens floating in front of you that stay put as you turn your head.

1. Plug your glasses into a USB-C port that carries video.
2. Open PocketXR.exe. It is not signed yet: if Windows says "Windows protected your PC", choose More info, then
   Run anyway.
3. A short tour opens the first time. Pocket XR lives in the notification area by the clock, under a pair of glasses.

Floating screens need Parsec's free Virtual Display Driver: the control panel shows how to get it ("How to add
floating screens"). Without it, the glasses show your PC's own screen.

Tried so far only with the XREAL Aura, on a Steam Deck running Windows 11. Hand tracking (Ctrl+Alt+H) has been
tried with a webcam that faces you (the air trackpad); the hand ray through glasses' cameras has not. If something is
wrong, use "Send logs to Mike" in the menu.

Phone: "Let my phone stream this PC" in the control panel lets Pocket XR on your phone show this PC in your glasses,
with no Apollo needed. It also does what the separate Pocket XR PC helper did, so that helper is no longer needed.
Windows asks once whether Pocket XR may use your network: allow it on private networks.

Free and open source (GPL-3.0, see LICENSE.txt). Not made by or affiliated with XREAL, VITURE or Parsec.
EOF

(cd dist && powershell -NoProfile -Command "Compress-Archive -Path '$NAME\\*' -DestinationPath '$NAME.zip' -Force")
ls -la "dist/$NAME" "dist/$NAME.zip"
sha256sum "dist/$NAME.zip"
