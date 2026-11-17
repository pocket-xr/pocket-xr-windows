#!/bin/bash
# Runs Pocket XR on the Steam Deck (Windows) from this PC, over SSH (`ssh steamdeck`, key login).
# An SSH login cannot see the Deck's desktop, so scripts/deck-run.ps1 (copied next to the app) starts it in the
# person's own desktop session.
#
#   scripts/deck.sh copy                  copy publish/win-x64/PocketXR.exe and the helper to the Deck
#   scripts/deck.sh run --usb             run a check in the desktop session and print what it says
#   scripts/deck.sh start [args]          start the app in the desktop session (it keeps running)
#   scripts/deck.sh stop                  quit the app as its own Quit does (the glasses are put back first)
#   scripts/deck.sh log [lines]           the app's log on the Deck
set -euo pipefail
cd "$(dirname "$0")/.."
HELPER='powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\mhaye\PocketXR\deck-run.ps1'

case "${1:-}" in
    copy)
        "$0" stop
        ssh steamdeck 'powershell -NoProfile -Command "New-Item -ItemType Directory -Force C:\Users\mhaye\PocketXR | Out-Null"'
        scp -q publish/win-x64/PocketXR.exe scripts/deck-run.ps1 "steamdeck:C:/Users/mhaye/PocketXR/"
        echo "copied"
        ;;
    run) shift; ssh steamdeck "$HELPER run $*" ;;
    start) shift; ssh steamdeck "$HELPER start $*" ;;
    stop)
        # The app's own Quit: the glasses are put back first. Only if it is still running 40 s later is it ended.
        ssh steamdeck "$HELPER run --quit" >/dev/null || true
        ssh steamdeck 'powershell -NoProfile -Command "$p = Get-Process PocketXR -ErrorAction SilentlyContinue; if ($p) { $p | Wait-Process -Timeout 40 -ErrorAction SilentlyContinue }; if (Get-Process PocketXR -ErrorAction SilentlyContinue) { Stop-Process -Name PocketXR; echo "ended (it did not quit by itself in 40 s)" } else { echo "quit" }"'
        ;;
    log) ssh steamdeck "powershell -NoProfile -Command \"Get-Content (Join-Path \$env:LOCALAPPDATA 'Pocket XR\\pocket-xr.log') -Tail ${2:-40}\"" ;;
    *) sed -n '2,10p' "$0" ;;
esac
