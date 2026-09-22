# K30 Controller

A small Windows tray app that replaces the DigiDraw software for the **Turing KeyDial K30** and turns it into a controller for the **Claude desktop app**: switch sessions, pick the model and effort with the dial, push-to-talk, queue messages.

It talks to the K30 directly over Bluetooth LE. DigiDraw only has to stay installed: K30 Controller runs it for a few seconds after the K30 wakes, then closes it (see [How it works](#how-it-works)).

For a diagram of where each key sits, open [`docs/key-map.html`](docs/key-map.html) in a browser.

## Default mapping

| Control | Action |
|---|---|
| **K1** | Push-to-talk: holds `Ctrl+Space` while the key is held |
| **K2** | `Enter` · hold: `Ctrl+Enter` (queue the message) |
| **K3** | `Esc` |
| **K4** | Clear the focused field (`Ctrl+A`, `Backspace`) |
| **K5** | New session (`Ctrl+N`) |
| **K6** | Open Claude's model menu |
| **K7** | Open Claude's effort panel |
| **K8** | Fast mode (`Ctrl+Alt+F`) |
| **K9 / K10** | Previous / next session or tab (`Ctrl+Shift+Tab` / `Ctrl+Tab`) |
| **K11** | `Enter` · hold: `Ctrl+Enter` |
| **Roller** | Window switcher (up: next window, down: previous). Alt stays held while rolling, released when you stop |
| **Dial button** | Next dial mode · confirms Claude's "Switch model?" prompt |
| **Dial: Model** | Turn to pick a model (shown in the pop-up); it is selected when you stop |
| **Dial: Effort** | Moves Claude's effort slider, Low → Ultracode |

After any model or effort change, keyboard focus goes back to Claude's message box, so dictation and typing land where you expect.

A pop-up (styled after DicTray's voice overlay) shows the current mode and changes at the bottom centre of the monitor that holds the focused window.

## Requirements

- Windows 10 2004+ / Windows 11, Bluetooth LE
- The K30 paired in Windows Bluetooth settings
- DigiDraw installed (it is started briefly after the K30 wakes; see "How it works")
- To build: a .NET 10 SDK

## Build and install

```powershell
./build.ps1 -Install
```

This publishes a self-contained `K30.exe` (no .NET install needed to run it), installs it to `%LOCALAPPDATA%\Programs\K30Controller`, registers it to start when you sign in, and starts it. Use `./build.ps1` alone to just build into `./publish`, and `-Dotnet <path>` to use a specific SDK.

Disable DigiDraw's own startup entry (Task Manager → Startup apps → TuringTablet): K30 Controller starts it briefly when needed and closes it again, and a DigiDraw left running makes every key fire twice.

## Configuration

`k30-config.json` is created next to `K30.exe` on first run. Edit it from the tray icon (**Edit config**, then **Reload config**). The file documents itself; the main ideas:

- `"address": "auto"` connects to the first paired device named `Turing KDial…`; or set its Bluetooth address.
- Key actions are shortcuts (`ctrl+shift+tab`, `f13`, `enter`), sequences separated by commas (`ctrl+a, backspace`), `hold …` to keep keys down while the button is held, `nextMode` / `prevMode`, or `claude:model` / `claude:effort`.
- `"K2:long"` / `"K2:double"` add a second action on a long or double press (`longPressMs`, `doublePressMs`). The plain action then fires on release.
- The roller accepts shortcuts, `wheel+1` / `wheel-1`, or `alttab:next` / `alttab:prev`.
- Dial modes: `claude-model`, `claude-effort` (`max`: 0 Low … 4 Max, 5 Ultracode), `keys` (`cw` / `ccw` shortcuts), `alttab`.

## How it works

**Bluetooth.** Out of the box (and after every sleep) the K30 acts as a plain HID keyboard with built-in keys. DigiDraw sends it a start command that switches it to a vendor mode: HID goes quiet and all input arrives as notifications on a vendor GATT characteristic (service `FFE0`, characteristic `FFE1`), 14-byte packets:

```
55 54 TT 01 AA B5 B6 00 00 00 00 00 XX CS
TT = E0  key state: B5 bits = K1..K8, B6 bits = K9, K10, K11, dial button (press and release are both sent)
TT = 20  rotation:  AA = 01 roller / 00 dial, B5 = 01 up|clockwise, 02 down|counter-clockwise
CS = sum(bytes[2..12]) & 0xFF
```

The start command itself is not known yet (it is not in DigiDraw's binary as plain bytes, and `FFE2` does not echo it). So K30 Controller borrows DigiDraw: on every (re)connect, at startup and after each sleep, it runs DigiDraw for `wakeKickSeconds` (15 s by default, with a "Waking K30…" pop-up), closes it, then subscribes. The mode lasts until the device sleeps again. DigiDraw therefore has to stay installed; `digidrawPath` points to it.

**Claude desktop.** Model and effort are driven through UI Automation (the accessibility tree), not keyboard shortcuts: the composer's `Model: …` and `Effort: …` buttons, the model menu's radio items, the effort panel's slider, the `Switch model?` dialog, and the `Prompt` edit field. If a Claude update renames these, those features stop working until the names are updated in `ClaudeUi`.

## Limitations

- Windows does not let a normal app send keystrokes into apps running as administrator.
- The Claude integration matches English UI names.
- Unsigned self-built executables can be flagged by antivirus heuristics; building with the SDK as above (with version info and an icon) has worked fine.

## License

[MIT](LICENSE)
