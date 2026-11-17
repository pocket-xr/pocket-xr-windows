#!/bin/bash
# Builds Pocket XR for Windows' download: one self-contained PocketXR.exe (no .NET to install), a short read-me and the
# licence, zipped as dist/PocketXR-Windows-<version>-preview.zip. Runs the checks first and stops if any fails.
# Publishes nothing.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$LOCALAPPDATA/PocketXR-tools/dotnet/dotnet.exe}"
VERSION="$(tr -d '[:space:]' < VERSION)"
NAME="PocketXR-Windows-$VERSION-preview"

"$DOTNET" run --project src/PocketXR.Checks -c Release

rm -rf "dist/$NAME" "dist/$NAME.zip"
"$DOTNET" publish src/PocketXR -c Release -r win-x64 --self-contained true -p:Version="$VERSION" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -o "dist/$NAME" -nologo -v q

cp LICENSE "dist/$NAME/LICENSE.txt"
cat > "dist/$NAME/READ ME.txt" <<EOF
Pocket XR for Windows $VERSION (preview)

Your PC's apps on your XR glasses, as screens floating in front of you that stay put as you turn your head.

1. Plug your glasses into a USB-C port that carries video.
2. Open PocketXR.exe. It is not signed yet: if Windows says "Windows protected your PC", choose More info, then
   Run anyway.
3. A short tour opens the first time. Pocket XR lives in the notification area by the clock, under a pair of glasses.

Floating screens need Parsec's free Virtual Display Driver: the control panel shows how to get it ("How to add
floating screens"). Without it, the glasses show your PC's own screen.

Tried so far only with the XREAL Aura, on a Steam Deck running Windows 11. If something is wrong, use
"Send logs to Mike" in the menu.

Free and open source (GPL-3.0, see LICENSE.txt). Not made by or affiliated with XREAL, VITURE or Parsec.
EOF

(cd dist && powershell -NoProfile -Command "Compress-Archive -Path '$NAME\\*' -DestinationPath '$NAME.zip' -Force")
ls -la "dist/$NAME" "dist/$NAME.zip"
sha256sum "dist/$NAME.zip"
