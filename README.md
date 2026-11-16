# Pocket XR for Windows (prototype)

Your PC's screens on your XR glasses, as windows held still in space while you turn your head.

It is the Windows version of [Pocket XR](https://github.com/pocket-xr/pocket-xr), the free and open source spatial
desktop for Android phones, and a sibling of Pocket XR for Steam Deck. GPL-3.0, no account, no paid tier.

**This is a prototype, not a release.** See [docs/PLAN.md](docs/PLAN.md) for what is proven and what is only built.

## Run

Needs Windows 11 and Python 3.10 or newer. Nothing else is installed.

```
python -m pocketxr.probe      does Windows see the glasses, their display and their sensor stream?
python -m pocketxr            the PC's screens in space on the glasses
python -m pocketxr --window --fake      a test window on the PC, with a made-up head turn, no glasses
python tests\test_offline.py  checks that need no glasses
```

Keys, from anywhere:

| Key | What it does |
| --- | --- |
| Ctrl+Alt+R | Recentre: the main screen comes in front of you |
| Ctrl+Alt+Up / Down | Bigger / smaller |
| Ctrl+Alt+PgUp / PgDn | Further / nearer (seen in 3D) |
| Ctrl+Alt+1 / 2 / 3 | How many screens are shown |
| Ctrl+Alt+D | 3D side by side on or off (only with `--settings`) |
| Ctrl+Alt+Q | Quit |

Hold still for a moment now and then: the head tracking learns its zero point while you are still.

## What is sent to the glasses

By default nothing. The sensor stream is only read. With `--settings`, Pocket XR sets the glasses up as the Steam
Deck version does (Spatial Anchor and Ultrawide off, Follow mode, 3D side by side), reads each change back, and puts
back what it changed when it quits.

The log is at `%LOCALAPPDATA%\Pocket XR\pocket-xr.log`.
