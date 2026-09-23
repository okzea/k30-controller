# K30 Controller

A small Windows tray app that replaces the DigiDraw software for the **Turing KeyDial K30** and turns it into a controller for the **Claude desktop app**: switch sessions, pick the model and effort with the dial, push-to-talk, queue messages.

It talks to the K30 directly over Bluetooth LE and does everything DigiDraw did, so DigiDraw is not needed.

For a diagram of where each key sits, open [`docs/key-map.html`](docs/key-map.html) in a browser.

## Default mapping

| Control | Action |
|---|---|
| **K1** | Push-to-talk: holds `Ctrl+Space` while the key is held |
| **K2** | `Enter` · hold: `Ctrl+Enter` (queue the message) |
| **K3** | `Esc` |
| **K4** | Clear the focused field (`Ctrl+A`, `Backspace`) |
| **K5** | New session (`Ctrl+N`) |
| **K6** | Mark the session as read/unread (`Ctrl+Alt+U`) |
| **K7** | Archive the session (`Ctrl+Alt+A`) |
| **K8** | Fast mode (`Ctrl+Alt+F`) |
| **K9 / K10** | Previous / next session or tab (`Ctrl+Shift+Tab` / `Ctrl+Tab`) |
| **K11** | `Tab` (accept Claude's suggested reply) |
| **Roller** | Window switcher (up: next window, down: previous), see below |
| **Dial button** | Next dial mode · confirms Claude's "Switch model?" prompt · hold: the key sheet (see below) |
| **Dial: Effort** (default) | Moves Claude's effort slider, Low → Ultracode |
| **Dial: Model** | Turn to pick a model (shown in the pop-up); it is selected when you stop |

After any model or effort change, keyboard focus goes back to Claude's message box, so dictation and typing land where you expect.

A pop-up (styled after DicTray's voice overlay) shows the current mode and changes at the bottom centre of the monitor that holds the focused window.

## Key sheet

Hold the dial button to see what every control does in the app you're in. It shows the picture of the K30 with a label for each key, the dial and the roller: the key's name ("New tab"), with its shortcut and any long or double press underneath. It appears on every monitor, in the same style as the pop-up. The next press of any key closes it and still does that key's job, so you can look and then press; turning the dial or the roller also closes it, and it closes on its own after 12 seconds.

The names come from `labels` in `k30-config.json` (the **Name** column in Settings). An app profile's keys use its own names, and a key it changes without naming shows its shortcut instead. The action is `showKeys`, mapped to `Dial:long` by default.

## Window switcher

The roller has its own switcher rather than Windows' Alt-Tab. The first click shows the list of open windows on every monitor, so it's in front of you wherever you're looking. It's laid out like macOS's app switcher: a row of large icons with each app's name underneath. It scrolls sideways when there are more windows than fit. By default it's laid out as a timeline, like a browser's Back and Forward. The present is on the right, the windows you went to before it are to the left, and the highlight moves the way you roll. Moving with the roller never reorders the timeline, so rolling back to a window and then forward returns you where you were. Only going to a window some other way (a click, the taskbar, Alt-Tab) moves it to the present end. The ends don't wrap around, and a dot marks the window you're in. Choose Alt-Tab's order instead (most recent on the left) in **Settings → Window switcher → Order**. Each further click moves the highlight, and it switches once you stop rolling (after 400 ms by default). The dial button switches immediately, and any other key cancels.

You choose which apps it offers. In **Settings → Window switcher**, drag apps between *Shown* and *Hidden*, and decide whether apps in neither list (ones you open later) are shown or hidden.

## Per-application profiles

Every key except **K1** (push-to-talk) and the **roller** (window switcher) can be remapped per application: while a given app is in the foreground, its own `keys` and `dialModes` take over; anywhere else, the default mapping above applies. This is checked fresh on every key press and every dial turn, so it's safe to switch apps mid-gesture — the gesture that was in progress is simply abandoned rather than continuing to steer the wrong app.

Shipped out of the box:

| | K2 | K5 | K6 | K7 | K8 | K9 | K10 | K11 | Dial |
|---|---|---|---|---|---|---|---|---|---|
| **Vivaldi** | New tab | Close tab | Reopen closed tab | Quick Commands | Focus address bar | *(default)* | *(default)* | Bookmark page | Navigate (back/forward) · Scroll |
| **Outlook** | Send | New message | Mark as read | Delete | Mark as unread | Previous message | Next message | Go to calendar | Messages (prev/next) |
| **Codex** (the ChatGPT desktop app's Codex workspace) | Enter | New chat | Clear unread | Archive chat | Model picker | *(default)* | *(default)* | Toggle Activity view | Font size |

*(default)* means that key falls through unchanged to the mapping above (K9/K10's `Ctrl+Shift+Tab`/`Ctrl+Tab` already cycle tabs in both apps; K3/K4 aren't overridden either — Esc and clear-field are broadly correct everywhere, except Outlook's K4, which is remapped to Flag message since select-all-and-backspace is destructive in an email draft.)

A profile is keyed by process name (Task Manager's Details tab, without `.exe`): Vivaldi is `vivaldi`, the new Outlook for Windows is `olk` (not classic Outlook's `OUTLOOK`), and the Codex/ChatGPT desktop app is `chatgpt`. Add more the same way — see `appProfiles` in `k30-config.json`.

## Requirements

- Windows 10 2004+ / Windows 11, Bluetooth LE
- The K30 paired in Windows Bluetooth settings
- To build: a .NET 10 SDK

## Build and install

```powershell
./build.ps1 -Install
```

This publishes a self-contained app (no .NET install needed to run it), installs it to `%LOCALAPPDATA%\Programs\K30Controller`, registers a Scheduled Task (`K30Controller`) that starts it ~20 s after you sign in, and starts it now. Use `./build.ps1` alone to just build into `./publish`, and `-Dotnet <path>` to use a specific SDK.

Published as a plain folder of files rather than a single bundled `.exe`: a single-file build re-extracts its native libraries to a temp folder on every launch, and that "unpacks itself and runs" pattern is exactly what antivirus real-time protection is most suspicious of at boot — on a machine with Bitdefender installed alongside Windows Defender, autostart failed completely and silently (no crash, nothing in `k30.log`) with the single-file build, and a Scheduled Task with a short delay survives the boot-time scan storm better than the `HKCU…\Run` key does.

Quit DigiDraw and disable its startup entry (Task Manager → Startup apps → TuringTablet), or uninstall it: if both run, every key fires twice.

## Configuration

To open **Settings**, use the tray menu, double-click the tray icon, or launch `K30.exe` again while it runs. It has three pages:

- **Buttons & dial**: every key's press, long press and double press, the roller and the dial modes, for all apps or for one app. A record button captures a shortcut from the keyboard. A picture of the K30 beside the fields lights up the key for the field you point at, and clicking a key jumps to its shortcut.
- **Window switcher**: the drag-and-drop lists described above.
- **General**: the device address and the press timings.

It follows Windows' light or dark mode, using WPF's built-in Windows 11 (Fluent) theme, so there's no extra dependency. Saving applies the changes straight away.

Everything is stored in `k30-config.json`, next to `K30.exe`. Settings edits that file in place and keeps the previous version as `k30-config.json.bak`. You can still edit the file by hand, then choose **Reload config file** from the tray menu. The file documents itself; the main ideas:

- `"address": "auto"` connects to the first paired device named `Turing KDial…`; or set its Bluetooth address.
- `hidWake`, `wakeCommands`, `wakeRepeat`: the controller-mode switch sent on every (re)connect (see How it works). `wakeKickSeconds` / `digidrawPath`: optional fallback that runs DigiDraw instead.
- Key actions are shortcuts (`ctrl+shift+tab`, `f13`, `enter`), sequences separated by commas (`ctrl+a, backspace`), `hold …` to keep keys down while the button is held, `nextMode` / `prevMode`, or `claude:model` / `claude:effort`.
- `"K2:long"` / `"K2:double"` add a second action on a long or double press (`longPressMs`, `doublePressMs`). The plain action then fires on release.
- Scrolling (`wheel+1` / `wheel-1`) always scrolls the window you're working in, even when the mouse pointer rests on another one: the pointer is moved there for the scroll and straight back.
- The roller accepts `switch:next` / `switch:prev` (the switcher), shortcuts, `wheel+1` / `wheel-1`, or `alttab:next` / `alttab:prev` (Windows' own Alt-Tab).
- `switcher`: `mode` is `allow` (only the listed `apps`) or `block` (every app except them). `commitMs` is the delay before it switches.
- Dial modes: `claude-model`, `claude-effort` (`max`: 0 Low … 4 Max, 5 Ultracode), `keys` (`cw` / `ccw` shortcuts), `alttab`.
- `appProfiles`: per-application `keys` and `dialModes` overrides — see [Per-application profiles](#per-application-profiles).

## How it works

**Bluetooth.** Out of the box (and after every sleep) the K30 acts as a plain HID keyboard with built-in keys. Switched to controller mode, its HID keys go quiet and all input arrives as notifications on a vendor GATT characteristic (service `FFE0`, characteristic `FFE1`), 14-byte packets:

```
55 54 TT 01 AA B5 B6 00 00 00 00 00 XX CS
TT = E0  key state: B5 bits = K1..K8, B6 bits = K9, K10, K11, dial button (press and release are both sent)
TT = 20  rotation:  AA = 01 roller / 00 dial, B5 = 01 up|clockwise, 02 down|counter-clockwise
CS = sum(bytes[2..12]) & 0xFF
```

**The switch.** The K30's third HID service is a *System Multi-Axis Controller* collection (usage page `0x01`, usage `0x0E`) with a 3-byte **feature report, ID 5**. It reads `05 10 0E` in built-in-keys mode. Writing `00 00` to it (`HidD_SetFeature`, which becomes an ATT write to handle `0x0040`) switches the K30 to controller mode, and it then reads `05 00 00`. The mode lasts until the device sleeps or powers off, so K30 Controller does this on every (re)connect.

It also sends DigiDraw's status queries to characteristic `FFE2` (8-byte writes `CD xx 00 00 00 00 00 00`). The K30 answers them with indications on `FFE2`, and only in controller mode:

| Query | Answer | Meaning |
|---|---|---|
| `CD B5` | `14 B5` + ASCII | device name (`Turing_T253_…`) |
| `CD B4` | `14 B4 …` | device info |
| `CD BD` | `06 BD 64 50` | battery (`0x50` = 80 %) |
| `CD C0`, `CD D3`, `CD CC` | `06 CC 00 …` | status, sent periodically by DigiDraw |

This was worked out from Windows Bluetooth traces (`wpr` with Microsoft's `BluetoothStack.wprp`) of DigiDraw switching the device. The old approach, running DigiDraw briefly, is still available as a fallback: set `wakeKickSeconds` > 0.

**Claude desktop.** Model and effort are driven through UI Automation (the accessibility tree), not keyboard shortcuts: the composer's `Model: …` and `Effort: …` buttons, the model menu's radio items, the effort panel's slider, the `Switch model?` dialog, and the `Prompt` edit field. If a Claude update renames these, those features stop working until the names are updated in `ClaudeUi`.

**Per-application profiles.** Deliberately not UI Automation: just `GetForegroundWindow` + `GetWindowThreadProcessId`, cheap enough to call on every key press and dial turn. K1 and the roller never consult a profile at all — `KeyAction` special-cases K1's index, and the roller's handler reads `cfg.RollerUp`/`RollerDown` directly, bypassing the whole profile-lookup path — so there's no config that can move them, not just a convention not to.

## Limitations

- Windows does not let a normal app send keystrokes into apps running as administrator.
- The Claude integration matches English UI names.
- Unsigned self-built executables can be flagged by antivirus heuristics; building with the SDK as above (with version info and an icon) has worked fine.

## License

[MIT](LICENSE)
