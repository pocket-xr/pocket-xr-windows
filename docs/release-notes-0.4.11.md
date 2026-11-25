# Pocket XR for Windows 0.4.11

Up to six screens, where there were three. Nothing changes for anyone who leaves the counts as they are: 2 floating
screens at first, and 3 PC screens for the phone when Apollo is set up, as before.

- **Up to six floating screens** (Settings, Space: Screens 1 to 6; the menu; + in the control panel). Four to six
  have layouts of their own: four, five or six across; main + sides (the others in a column each side of the main
  screen); two rows; and theater with further flanks. One to three screens are laid out exactly as before. Portrait
  has a switch for each screen (screens 4 to 6 under the row's words, so it fits), and the **Main display** picker
  offers each of the six.
- **Only as many as the driver makes.** SudoVDA (Apollo's driver) makes at most its `maxMonitors` (registry,
  HKLM\SOFTWARE\SudoMaker\SudoVDA; 10 when not set), and an Apollo's own screens count towards that too. Parsec's driver
  makes 8 the way Pocket XR asks it (Parsec's own app stops at 3). The pickers offer no more than the driver's limit;
  if the driver still refuses one part way, the screens it did make are kept, and the log says how many and why.
- **Up to six PC screens for your phone** (the menu: Set up Apollo for PC screens, or asked from the phone). The setup
  now asks how many PC screens, 1 to 6; Enter keeps what is set up (3 the first time). PC screens 2 and 3 stay on
  48989 and 49989; 4 to 6 are on 51989, 52989 and 53989, skipping 50989, where Pocket XR's own host is. Choosing fewer
  stops the extra Apollos and keeps their settings as `.off` files, so choosing more again brings them back on their
  old ports with their phones still paired. The phone can name the number itself (`"screens"` in its setup request);
  Pocket XR for Android does not send it yet, so the setup asks.
- **The control panel's Place pad fits every layout.** Three stacked screens (and now two rows) ran under the pad's
  hint and its −, +, Nearer and Further buttons; a theater with an odd number of flanks ran off its side. The pad now
  draws the screens as large as fits inside it and clear of its words and buttons, and holds that size during a drag.
- **Another computer in these glasses** opens up to six PC screens, and finds a computer's six Apollo ports at once.

Untested: nothing has run with more than 3 screens live, in glasses or on a desktop. The layouts, the driver limits,
the ports and the setup script's table were checked by sums and against the setup script's own text; the setup's
count, its `.off` switching and its port choice were tried on a made-up Apollo folder, not with Apollo running six
instances; and the four-to-six layouts were looked at only in pictures drawn by the app. Pocket XR for Android still
shows at most 3 PC screens from one PC. If you try six, **Send logs to Mike** in the menu helps a lot.

Everything else is as in 0.4.10.

## Install

1. Download `PocketXR-Windows-0.4.11.zip` and unzip it anywhere (over an older version is fine: your settings are kept).
2. Open `PocketXR.exe`; it closes an older Pocket XR that is still running. It is not signed yet, so Windows may say
   "Windows protected your PC": choose **More info**, then **Run anyway**.

Pocket XR is a community project, not made by or affiliated with XREAL, VITURE, Parsec, Google, Moonlight, Sunshine or
Apollo.
