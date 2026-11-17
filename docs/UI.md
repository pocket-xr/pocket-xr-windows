# Building Pocket XR's windows on Windows

Pocket XR for Windows is C# on .NET 8 with WPF, built in code (no XAML files), so that every window can also be drawn
to a PNG file with no window and no glasses (`PocketXR --render-ui FOLDER`). The reference for what each window holds,
its words and its behaviour is Pocket XR for Mac (`pocket-xr-mac/Sources/PocketXR`, read only). The Windows windows
match it feature for feature and are redesigned only where Windows differs.

## The look: "Light on glass"

Always dark. Page `#0C0D10`, pad `#15171C`, cards `#1A1D24` with a 1 px white rim at 14 % and a 12 px corner radius.
One accent, `#5A93FF` (progress, the main button, ticks, the focus ring); `#8FB6FF` means "you are holding this".
Ink at 92 %, 64 % and 40 % white. Light marks focus and what is held; nothing casts a shadow, nothing bounces: no
springs, no overshoot. Motion is 0.12 s (quick), 0.28 s (enter), 0.16 s (exit) on the curves in `Look`. Rows are 44
high, controls 32, the type scale is 26 / 16 / 15 / 14 / 12.5 / 11. Plain words, British spelling ("Recentre",
"colour"), sentences a person who is not a programmer understands. Calm: few things on screen, generous space, one
main action per window.

Everything shared is in `src/PocketXR/Ui`:

- `Theme.cs`: brushes (`Theme.Page`, `Card`, `Accent`, `Ink`, `Ink2`, `Ink3`, `Rim`, ...), fonts, sizes,
  `Theme.Animate`, `Theme.AnimateColor`, `Theme.Arrive(element, order)` (a card's fade and rise), `Theme.Static`
  (true while drawing pictures: nothing animates or waits), `Theme.ReduceMotion`, and `Glyph` (Windows' icon font).
- `Kit.cs`: `Binder`, `Press` (base for anything clickable: hover, press, focus ring, keys, screen reader name),
  `PxButton`, `IconButton`, `Toggle`, `PxSlider`, `Segmented`, `Swatch`, `Draw` (text for `OnRender`), and `Kit.*`
  builders: `Text`, `Title`, `Heading`, `Body`, `Line`, `Small`, `Icon`, `Card`, `Plate`, `Divider`, `Row`,
  `ToggleRow`, `StackedRow`, `Note`, `SliderRow`, `Chip`, `Dot`, `KeyCap`, `Keys`, `Reading`, `Across`, `Down`.
- `Shell.cs`: `PxWindow` (the dark window frame with its own 30 px strip) and `UiRender.WindowPng` / `UiRender.Png`.

`src/PocketXR/Windows/ControlPanel.cs` and `Pictures.cs` are the worked example: read them before writing a window.
`src/PocketXR/AppModel.cs` has `SettingsStore` (`store.S` to read, `store.Update(s => ...)` to change), `AppState`
(what is true now), `IAppActions` (what a window can ask the app to do), `Shortcut.All` and `SettingsFormat`.

## How a window is put together

- A static class per window in `src/PocketXR/Windows/<Name>.cs` with a method that builds the content from
  `(SettingsStore store, AppState state, IAppActions actions, Binder binder)` and a method that makes the `PxWindow`
  and wires `store.Changed` and `state.Changed` to `binder.Sync()`.
- Controls take `() => value` and `v => store.Update(...)`. They register themselves with the `Binder`, which shows
  the value again on every `Sync()`. A part whose rows change with the state is rebuilt with a fresh inner `Binder`
  (see `SpaceCard` in `ControlPanel.cs`).
- Pictures you draw yourself are `FrameworkElement`s with `OnRender` (see `MapView` and `PadView`).
- Every clickable thing has a `Label` (its screen reader name), works from the keyboard (Tab, Space, Enter, arrows
  for sliders) and shows the focus ring only when focus came from the keyboard. `Press` does this for you.
- Each window has a `Pictures` method that draws it in several made-up but possible states with `UiRender.WindowPng`,
  using `new SettingsStore(persist: false)`, `Pictures.State(...)` and `new NoActions()`.

## Rules

- Do not change `Theme.cs`, `Kit.cs`, `Shell.cs`, `ControlPanel.cs` or the files in `PocketXR.Core`. If a shared
  part is missing, write it in a new file of your own in `Ui/` and say so in your report; if something in the shared
  files is wrong, report it.
- `AppModel.cs`: you may add (never rename or remove) fields to `AppState` and methods to `IAppActions` and
  `NoActions`, with small exact `Edit` calls, reading the file again just before each edit, because others add to it
  at the same time. Say what you added.
- Words: take the Mac app's words. "Mac" becomes "PC" where the computer is meant, "menu bar" becomes "notification
  area" (the icons by the clock), Control-Option shortcuts become Ctrl+Alt (see `Shortcut.All`), System Settings
  becomes Windows Settings. Never promise what has not been seen working: only the XREAL Aura has been used with
  Pocket XR for Windows, and only its head tracking; say "not tried on a PC yet" where the Mac text says "not tried on
  a Mac". List every sentence you wrote or changed in your report.
- Nothing may be sent to glasses, nothing installed, no administrator rights, no display settings changed, no files
  outside the repo and your scratch folder. No git commands that change anything.
- Build with `C:\Users\mhaye\AppData\Local\PocketXR-tools\dotnet\dotnet.exe` (not on PATH), in a private project in
  your own scratch folder that includes the source files by path (copy
  `C:\Users\mhaye\AppData\Local\Temp\claude\1f2dbda9645a3cbf\8d1b6434-8cbf-4171-a9e2-af031a31f70a\scratchpad\ui-preview\ui-preview.csproj`
  and its `Program.cs`, add your files, and call your pictures method). Do not build inside `src/`. Write files with
  the Write tool: Git Bash heredocs mangle backslashes on this machine.
- Look at your own pictures (the Read tool shows a PNG) and keep improving until each one is something you would put
  in front of a designer: aligned, nothing cut off or overlapping, no stray default Windows styling, text that reads
  well, a clear order of importance. Check every state, not one.
