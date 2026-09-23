// K30 Controller — replaces the DigiDraw software for the Turing KeyDial K30.
// Talks to the K30 over its custom BLE service (FFE0/FFE1), maps keys to shortcuts,
// and gives the dial switchable modes (click the dial button to cycle).
//
// FFE1 packet (14 bytes): 55 54 TT 01 AA B5 B6 00 00 00 00 00 XX CS
//   TT=E0 key state: B5 bits = K1..K8, B6 bits = K9, K10, K11, dial button (press + release both sent)
//   TT=20 rotation:  AA=01 roller / 00 dial, B5=01 up|clockwise / 02 down|counter-clockwise
//   CS = sum(bytes[2..12]) & 0xFF
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using IAsyncOp = Windows.Foundation;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

static class Program {
    public const string OpenSettingsEvent = "K30Controller.OpenSettings";
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    [STAThread]
    static void Main(string[] args) {
        bool created;
        using (var mutex = new Mutex(true, "K30Controller.SingleInstance", out created)) {
            if (!created) {
                // Launching K30.exe again while it runs opens the settings window — except from the logon
                // task (--autostart), which should never pop anything up.
                if (!args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)) {
                    AllowSetForegroundWindow(-1 /* ASFW_ANY: we have the foreground, let the running instance take it */);
                    EventWaitHandle ev;
                    if (EventWaitHandle.TryOpenExisting(OpenSettingsEvent, out ev)) using (ev) ev.Set();
                }
                return;
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // same as DicTray's overlay
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new K30App());
        }
    }
}

// ---------------------------------------------------------------- config

class DialMode {
    public string Name;
    public string Type;      // "keys" | "menu" | "alttab" | "claude-model" | "claude-effort"
    public string Cw, Ccw;   // keys: shortcut per tick; menu: navigation key per tick
    public string Open;      // menu: shortcut that opens the menu
    public string Confirm;   // menu: key sent after the dial stops
    public int IdleMs = 800;
    public int Max = 4;      // claude-effort: highest slider step the dial may reach (4 = Max, 5 = Ultracode)
}

/// A per-application override: while the named process is in the foreground, its Keys override the
/// global ones for the same key name, and — if non-empty — its DialModes replace the dial's mode list
/// entirely. K1 (push-to-talk) and the roller have no override path at all: they only ever read the
/// global Config, by construction, not by convention — see K30App.KeyAction and K30App.Roller.
class AppProfile {
    public string Label;
    public Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // what each key does, in words, for the key sheet
    public List<DialMode> DialModes = new List<DialMode>();
}

class Config {
    public ulong Address;
    public Dictionary<string, string> Keys = new Dictionary<string, string>();
    public Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // same key names as Keys
    public string RollerUp, RollerDown;
    public List<DialMode> DialModes = new List<DialMode>();
    public Dictionary<string, AppProfile> AppProfiles = new Dictionary<string, AppProfile>(StringComparer.OrdinalIgnoreCase);
    public string SwitcherMode = "block";   // "allow": only SwitcherApps; anything else: everything except SwitcherApps
    public HashSet<string> SwitcherApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int SwitcherCommitMs = 400;
    public bool SwitcherHistoryOnLeft = true; // false: Alt-Tab's order, most recent window first on the left
    public int LongPressMs = 450, DoublePressMs = 300;
    public string DigiDrawPath = @"%APPDATA%\TuringTablet\TuringTablet.exe";
    public int WakeKickSeconds = 0;
    public int RollerIdleMs = 350;
    public List<string> WakeCommands = new List<string> { "CD C0", "CD D3", "CD CC", "CD C0", "CD B5", "CD B4", "CD D3", "CD BD" };
    public bool HidWake = true;
    public int WakeRepeat = 1;

    public const string Default = @"{
  ""_help_address"": ""'auto' connects to the first paired Bluetooth device named 'Turing KDial…'. Or give its address, e.g. 'AA:BB:CC:DD:EE:FF'."",
  ""address"": ""auto"",

  ""_help_wake"": ""After power-up or sleep the K30 is back on its built-in keys. On every (re)connect K30 Controller switches it to controller mode: it writes 00 00 to HID feature report 5 of the K30's multi-axis collection (hidWake) and sends DigiDraw's status queries to FFE2 (wakeCommands), wakeRepeat times. Fallback: wakeKickSeconds > 0 runs DigiDraw (digidrawPath) that long instead."",
  ""hidWake"": true,
  ""wakeCommands"": [""CD C0"", ""CD D3"", ""CD CC"", ""CD C0"", ""CD B5"", ""CD B4"", ""CD D3"", ""CD BD""],
  ""wakeRepeat"": 1,
  ""wakeKickSeconds"": 0,
  ""digidrawPath"": ""%APPDATA%\\TuringTablet\\TuringTablet.exe"",

  ""_help_keys"": ""Shortcut syntax: ctrl+shift+i, alt+tab, win+tab, esc, enter, up, down, f13... Separate several with commas to send them in order (ctrl+a, backspace). Prefix with 'hold ' to keep the keys down while the button is held (push-to-talk). 'nextMode' / 'prevMode' cycle the dial mode. 'claude:model' / 'claude:effort' open Claude's model menu / effort panel. 'showKeys' shows the key sheet: a picture of the K30 with what each key does in the app you're in. Add 'K2:long' or 'K2:double' for a second action on a long or double press (the plain action then fires on release)."",
  ""longPressMs"": 450,
  ""doublePressMs"": 300,
  ""keys"": {
    ""K1"":  ""hold ctrl+space"",
    ""K2"":  ""enter"",
    ""K2:long"":  ""ctrl+enter"",
    ""K3"":  ""esc"",
    ""K4"":  ""ctrl+a, backspace"",
    ""K5"":  ""ctrl+n"",
    ""K6"":  ""ctrl+alt+u"",
    ""K7"":  ""ctrl+alt+a"",
    ""K8"":  ""ctrl+alt+f"",
    ""K9"":  ""ctrl+shift+tab"",
    ""K10"": ""ctrl+tab"",
    ""K11"": ""tab"",
    ""Dial"": ""nextMode"",
    ""Dial:long"": ""showKeys""
  },
  ""_help_labels"": ""What each key does, in words: the key sheet (hold the dial button, 'showKeys') shows these, with the shortcut underneath. Same key names as 'keys'. App profiles have their own 'labels' for the keys they change."",
  ""labels"": {
    ""K1"": ""Push-to-talk"", ""K2"": ""Send"", ""K2:long"": ""Queue message"", ""K3"": ""Stop / cancel"", ""K4"": ""Clear field"",
    ""K5"": ""New session"", ""K6"": ""Read / unread"", ""K7"": ""Archive session"", ""K8"": ""Fast mode"",
    ""K9"": ""Previous session"", ""K10"": ""Next session"", ""K11"": ""Accept suggestion"", ""Dial"": ""Next dial mode"", ""Dial:long"": ""This key sheet""
  },

  ""_help_roller"": ""switch:next / switch:prev use K30 Controller's own window switcher (see 'switcher'). Also: any shortcut, wheel+1 / wheel-1 to scroll, or alttab:next / alttab:prev for Windows' own Alt-Tab (Alt stays held while rolling and is released rollerIdleMs after the last click)."",
  ""rollerIdleMs"": 350,
  ""roller"": { ""up"": ""switch:next"", ""down"": ""switch:prev"" },

  ""_help_switcher"": ""The roller's window switcher. Its list appears on every monitor; each roller click moves the highlight the way you roll (switch:next right, switch:prev left), and it switches commitMs after the last click (the dial button switches right away; any other key cancels). historyOnLeft true: a timeline like a browser's Back/Forward, the present on the right and the windows you went to before it going left; rolling never reorders it, so back then forward returns you where you were, and only going to a window another way (click, taskbar) moves it to the present end. false: Alt-Tab's order, wrapping around. mode 'allow': only windows of the apps in 'apps'. mode 'block': every window except those apps. Apps are process names as in appProfiles (Task Manager > Details, without '.exe'), e.g. claude, vivaldi, olk, chatgpt, WindowsTerminal."",
  ""switcher"": { ""mode"": ""block"", ""apps"": [], ""commitMs"": 400, ""historyOnLeft"": true },

  ""_help_dialModes"": ""type 'keys': cw/ccw sent per click. type 'alttab': holds Alt while turning. type 'claude-model': turn to pick a model, it is selected when the dial rests for idleMs (press the dial to confirm Claude's 'Switch model?' prompt). type 'claude-effort': moves Claude's effort slider; 'max' is the highest step reachable (0 Low, 1 Medium, 2 High, 3 Extra, 4 Max, 5 Ultracode). type 'menu': first click sends 'open', next clicks send cw/ccw, 'confirm' is sent when the dial rests."",
  ""dialModes"": [
    { ""name"": ""Effort"",          ""type"": ""claude-effort"", ""idleMs"": 1200, ""max"": 5 },
    { ""name"": ""Model"",           ""type"": ""claude-model"",  ""idleMs"": 900 }
  ],

  ""_help_appProfiles"": ""Per-application overrides, keyed by the process name Task Manager's Details tab shows (no '.exe'). While that process has focus: its 'keys' override the ones above for the same key, and its 'dialModes', if it has any, replace the dial's mode list. Re-checked each time you turn or press the dial, so switching apps mid-gesture is safe. K1 (push-to-talk) and the roller have no per-app override at all — they always use the mapping above, everywhere."",
  ""appProfiles"": {
    ""vivaldi"": {
      ""label"": ""Vivaldi"",
      ""keys"": {
        ""K2"":  ""ctrl+t"",
        ""K5"":  ""ctrl+w"",
        ""K6"":  ""ctrl+shift+t"",
        ""K7"":  ""f2"",
        ""K8"":  ""ctrl+l"",
        ""K11"": ""ctrl+d""
      },
      ""labels"": { ""K2"": ""New tab"", ""K5"": ""Close tab"", ""K6"": ""Reopen closed tab"", ""K7"": ""Quick Commands"", ""K8"": ""Address bar"", ""K11"": ""Bookmark page"" },
      ""dialModes"": [
        { ""name"": ""Navigate"", ""type"": ""keys"", ""cw"": ""alt+right"", ""ccw"": ""alt+left"" },
        { ""name"": ""Scroll"",   ""type"": ""keys"", ""cw"": ""wheel-1"",   ""ccw"": ""wheel+1"" }
      ]
    },
    ""olk"": {
      ""label"": ""Outlook"",
      ""keys"": {
        ""K2"":  ""ctrl+enter"",
        ""K4"":  ""insert"",
        ""K5"":  ""ctrl+n"",
        ""K6"":  ""ctrl+q"",
        ""K7"":  ""delete"",
        ""K8"":  ""ctrl+u"",
        ""K9"":  ""ctrl+shift+,"",
        ""K10"": ""ctrl+shift+."",
        ""K11"": ""ctrl+2""
      },
      ""labels"": { ""K2"": ""Send"", ""K4"": ""Flag message"", ""K5"": ""New message"", ""K6"": ""Mark as read"", ""K7"": ""Delete"", ""K8"": ""Mark as unread"", ""K9"": ""Previous message"", ""K10"": ""Next message"", ""K11"": ""Calendar"" },
      ""dialModes"": [
        { ""name"": ""Messages"", ""type"": ""keys"", ""cw"": ""ctrl+shift+."", ""ccw"": ""ctrl+shift+,"" }
      ]
    },
    ""chatgpt"": {
      ""label"": ""Codex"",
      ""keys"": {
        ""K2"":  ""enter"",
        ""K5"":  ""ctrl+n"",
        ""K6"":  ""shift+esc"",
        ""K7"":  ""ctrl+shift+a"",
        ""K8"":  ""ctrl+shift+m"",
        ""K11"": ""ctrl+alt+u""
      },
      ""labels"": { ""K2"": ""Send"", ""K5"": ""New chat"", ""K6"": ""Clear unread"", ""K7"": ""Archive chat"", ""K8"": ""Model picker"", ""K11"": ""Activity view"" },
      ""dialModes"": [
        { ""name"": ""Font size"", ""type"": ""keys"", ""cw"": ""ctrl+="", ""ccw"": ""ctrl+-"" }
      ]
    }
  }
}
";

    public static Config Load(string path) {
        if (!File.Exists(path)) File.WriteAllText(path, Default);
        return FromJson(File.ReadAllText(path));
    }

    public static Config FromJson(string json) {
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using (var doc = JsonDocument.Parse(json, opts)) {
            var root = doc.RootElement;
            var c = new Config();
            JsonElement a;
            string addr = root.TryGetProperty("address", out a) && a.ValueKind == JsonValueKind.String ? a.GetString().Trim() : "";
            c.Address = addr.Length == 0 || addr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : Convert.ToUInt64(addr.Replace(":", ""), 16);
            if (root.TryGetProperty("longPressMs", out a) && a.ValueKind == JsonValueKind.Number) c.LongPressMs = a.GetInt32();
            if (root.TryGetProperty("doublePressMs", out a) && a.ValueKind == JsonValueKind.Number) c.DoublePressMs = a.GetInt32();
            if (root.TryGetProperty("digidrawPath", out a) && a.ValueKind == JsonValueKind.String) c.DigiDrawPath = a.GetString();
            if (root.TryGetProperty("wakeKickSeconds", out a) && a.ValueKind == JsonValueKind.Number) c.WakeKickSeconds = a.GetInt32();
            if (root.TryGetProperty("rollerIdleMs", out a) && a.ValueKind == JsonValueKind.Number) c.RollerIdleMs = a.GetInt32();
            if (root.TryGetProperty("wakeRepeat", out a) && a.ValueKind == JsonValueKind.Number) c.WakeRepeat = Math.Max(1, a.GetInt32());
            if (root.TryGetProperty("hidWake", out a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)) c.HidWake = a.GetBoolean();
            if (root.TryGetProperty("wakeCommands", out a) && a.ValueKind == JsonValueKind.Array) {
                c.WakeCommands.Clear();
                foreach (var w in a.EnumerateArray()) if (w.ValueKind == JsonValueKind.String) c.WakeCommands.Add(w.GetString());
            }
            foreach (var kv in root.GetProperty("keys").EnumerateObject()) c.Keys[kv.Name.ToUpperInvariant()] = kv.Value.GetString();
            ReadLabels(root, c.Labels);
            JsonElement roller;
            if (root.TryGetProperty("roller", out roller)) { c.RollerUp = Str(roller, "up"); c.RollerDown = Str(roller, "down"); }
            JsonElement sw;
            if (root.TryGetProperty("switcher", out sw) && sw.ValueKind == JsonValueKind.Object) {
                c.SwitcherMode = (Str(sw, "mode") ?? "block").Trim().ToLowerInvariant();
                JsonElement apps;
                if (sw.TryGetProperty("apps", out apps) && apps.ValueKind == JsonValueKind.Array)
                    foreach (var app in apps.EnumerateArray())
                        if (app.ValueKind == JsonValueKind.String) {
                            string n = app.GetString().Trim();
                            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                            c.SwitcherApps.Add(n);
                        }
                JsonElement cm;
                if (sw.TryGetProperty("commitMs", out cm) && cm.ValueKind == JsonValueKind.Number) c.SwitcherCommitMs = cm.GetInt32();
                if (sw.TryGetProperty("historyOnLeft", out cm) && (cm.ValueKind == JsonValueKind.True || cm.ValueKind == JsonValueKind.False)) c.SwitcherHistoryOnLeft = cm.GetBoolean();
            }
            c.DialModes = ParseDialModes(root.GetProperty("dialModes"));
            if (c.DialModes.Count == 0) throw new Exception("dialModes is empty");

            JsonElement profiles;
            if (root.TryGetProperty("appProfiles", out profiles) && profiles.ValueKind == JsonValueKind.Object) {
                foreach (var prof in profiles.EnumerateObject()) {
                    var ap = new AppProfile { Label = Str(prof.Value, "label") ?? prof.Name };
                    JsonElement pk;
                    if (prof.Value.TryGetProperty("keys", out pk) && pk.ValueKind == JsonValueKind.Object)
                        foreach (var kv in pk.EnumerateObject()) ap.Keys[kv.Name.ToUpperInvariant()] = kv.Value.GetString();
                    ReadLabels(prof.Value, ap.Labels);
                    JsonElement pd;
                    if (prof.Value.TryGetProperty("dialModes", out pd) && pd.ValueKind == JsonValueKind.Array)
                        ap.DialModes = ParseDialModes(pd);
                    c.AppProfiles[prof.Name] = ap;
                }
            }
            return c;
        }
    }

    static void ReadLabels(JsonElement owner, Dictionary<string, string> into) {
        JsonElement labels;
        if (!owner.TryGetProperty("labels", out labels) || labels.ValueKind != JsonValueKind.Object) return;
        foreach (var kv in labels.EnumerateObject())
            if (kv.Value.ValueKind == JsonValueKind.String && kv.Value.GetString().Trim().Length > 0)
                into[kv.Name.ToUpperInvariant()] = kv.Value.GetString().Trim();
    }

    static List<DialMode> ParseDialModes(JsonElement arr) {
        var list = new List<DialMode>();
        foreach (var m in arr.EnumerateArray()) {
            var d = new DialMode {
                Name = Str(m, "name") ?? "Mode", Type = (Str(m, "type") ?? "keys").ToLowerInvariant(),
                Cw = Str(m, "cw"), Ccw = Str(m, "ccw"), Open = Str(m, "open"), Confirm = Str(m, "confirm") ?? "enter"
            };
            JsonElement v;
            if (m.TryGetProperty("idleMs", out v)) d.IdleMs = v.GetInt32();
            if (m.TryGetProperty("max", out v)) d.Max = v.GetInt32();
            list.Add(d);
        }
        return list;
    }

    static string Str(JsonElement e, string k) {
        JsonElement v;
        return e.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

// ---------------------------------------------------------------- keyboard / mouse output

static class Output {
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern short VkKeyScan(char ch);

    const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, MOUSEEVENTF_WHEEL = 0x800;
    static readonly IntPtr Tag = new IntPtr(0x4B3330); // "K30", marks our own injected input

    static readonly Dictionary<string, ushort> Names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) {
        {"ctrl",0xA2},{"control",0xA2},{"shift",0xA0},{"alt",0xA4},{"win",0x5B},
        {"tab",0x09},{"space",0x20},{"enter",0x0D},{"return",0x0D},{"esc",0x1B},{"escape",0x1B},
        {"backspace",0x08},{"delete",0x2E},{"del",0x2E},{"insert",0x2D},{"home",0x24},{"end",0x23},
        {"pgup",0x21},{"pageup",0x21},{"pgdn",0x22},{"pagedown",0x22},
        {"up",0x26},{"down",0x28},{"left",0x25},{"right",0x27},
        {"volup",0xAF},{"voldown",0xAE},{"mute",0xAD},{"playpause",0xB3},{"next",0xB0},{"prev",0xB1},
    };
    static readonly HashSet<ushort> Extended = new HashSet<ushort> { 0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x2D,0x2E,0x5B,0x5C,0x6F,0xA3,0xA5 };

    public static ushort[] Parse(string combo) {
        var vks = new List<ushort>();
        foreach (var raw in combo.Split('+')) {
            string p = raw.Trim();
            if (p.Length == 0) { vks.Add(0xBB); continue; } // "ctrl++" -> '+'/'=' key
            ushort vk;
            if (Names.TryGetValue(p, out vk)) { vks.Add(vk); continue; }
            if (p.Length > 1 && (p[0] == 'f' || p[0] == 'F')) {
                int n;
                if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) { vks.Add((ushort)(0x70 + n - 1)); continue; }
            }
            if (p.Length == 1) {
                short s = VkKeyScan(p[0]);
                if (s != -1) { vks.Add((ushort)(s & 0xFF)); continue; }
            }
            throw new Exception("Unknown key '" + p + "' in '" + combo + "'");
        }
        return vks.ToArray();
    }

    /// The config name for a virtual key, the inverse of Parse: the settings window's shortcut recorder uses it.
    public static string NameFor(ushort vk) {
        switch (vk) {
            case 0x11: case 0xA2: case 0xA3: return "ctrl";
            case 0x10: case 0xA0: case 0xA1: return "shift";
            case 0x12: case 0xA4: case 0xA5: return "alt";
            case 0x5B: case 0x5C: return "win";
        }
        foreach (var kv in Names) if (kv.Value == vk) return kv.Key; // first entry wins: enter, esc, delete, pgup…
        if (vk >= 0x70 && vk <= 0x87) return "f" + (vk - 0x70 + 1);
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString().ToLowerInvariant();
        uint ch = MapVirtualKey(vk, 2 /* MAPVK_VK_TO_CHAR: the unshifted character, e.g. ',' or '=' */) & 0x7FFF;
        return ch != 0 ? ((char)ch).ToString().ToLowerInvariant() : null;
    }

    static INPUT Key(ushort vk, bool up) {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        var i = new INPUT { type = 1 };
        i.u.ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = Tag };
        return i;
    }

    static void Send(IEnumerable<INPUT> inputs) {
        var arr = inputs.ToArray();
        if (arr.Length > 0) SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Down(ushort[] vks) { Send(vks.Select(v => Key(v, false))); }
    public static void Up(ushort[] vks) { Send(vks.Reverse().Select(v => Key(v, true))); }
    public static void Tap(ushort[] vks) { Send(vks.Select(v => Key(v, false)).Concat(vks.Reverse().Select(v => Key(v, true)))); }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    /// Scrolls the window you're working in. Windows sends the wheel to the window under the pointer,
    /// wherever the keyboard focus is, so when the pointer rests on another window, the pointer is moved
    /// to the middle of the active window for the scroll and straight back, in a single batch of input
    /// so nothing can come in between.
    public static void Wheel(int clicks) {
        var wheel = new INPUT { type = 0 };
        wheel.u.mi = new MOUSEINPUT { mouseData = unchecked((uint)(clicks * 120)), dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = Tag };
        POINT at;
        RECT r;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetCursorPos(out at) || IsIconic(fg) || !GetWindowRect(fg, out r) ||
            GetAncestor(WindowFromPoint(at), 2 /* GA_ROOT */) == fg) {
            Send(new[] { wheel });
            return;
        }
        var middle = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        Send(new[] { MoveTo(middle), wheel, MoveTo(at) });
    }

    static INPUT MoveTo(POINT p) {
        // Absolute coordinates span the whole virtual desktop (all monitors), 0…65535 on each axis.
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var i = new INPUT { type = 0 };
        i.u.mi = new MOUSEINPUT {
            dx = (int)Math.Round((p.X - vx) * 65535.0 / Math.Max(1, vw - 1)),
            dy = (int)Math.Round((p.Y - vy) * 65535.0 / Math.Max(1, vh - 1)),
            dwFlags = 0x0001 | 0x8000 | 0x4000, // MOVE | ABSOLUTE | VIRTUALDESK
            dwExtraInfo = Tag
        };
        return i;
    }

    /// Runs a configured action string: "ctrl+n", "wheel+1", or a sequence like "ctrl+a, backspace".
    public static void Run(string action) {
        if (string.IsNullOrWhiteSpace(action)) return;
        foreach (var step in action.Split(',')) {
            string s = step.Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("wheel", StringComparison.OrdinalIgnoreCase)) Wheel(int.Parse(s.Substring(5)));
            else Tap(Parse(s));
        }
    }
}

// ---------------------------------------------------------------- K30 HID wake (vendor-mode switch)

/// DigiDraw switches the K30 out of its built-in-keys mode by writing a 2-byte report of zeros to the K30's
/// "system multi-axis controller" HID collection (seen as ATT writes of 00 00 to handle 0x0040 in a
/// Bluetooth trace). This sends the same report: a feature report if the collection has one, else an output report.
static class K30Hid {
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr p, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }

    const int HidpOutput = 1, HidpFeature = 2, HidpStatusSuccess = 0x110000, CapsStructSize = 72;

    static System.Collections.Generic.IEnumerable<string> K30Paths() {
        Guid hid; HidD_GetHidGuid(out hid);
        IntPtr set = SetupDiGetClassDevs(ref hid, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        try {
            for (int i = 0; ; i++) {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref d)) yield break;
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                string path;
                try {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero);
                    path = Marshal.PtrToStringUni(buf + 4);
                } finally { Marshal.FreeHGlobal(buf); }
                if (path.IndexOf("023866", StringComparison.OrdinalIgnoreCase) >= 0) yield return path;
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static System.Collections.Generic.List<byte> ReportIds(IntPtr pp, int type, ushort valueCount, ushort buttonCount) {
        var ids = new System.Collections.Generic.List<byte>();
        if (valueCount > 0) {
            var raw = new byte[valueCount * CapsStructSize]; ushort n = valueCount;
            if (HidP_GetValueCaps(type, raw, ref n, pp) == HidpStatusSuccess)
                for (int i = 0; i < n; i++) if (!ids.Contains(raw[i * CapsStructSize + 2])) ids.Add(raw[i * CapsStructSize + 2]);
        }
        if (buttonCount > 0) {
            var raw = new byte[buttonCount * CapsStructSize]; ushort n = buttonCount;
            if (HidP_GetButtonCaps(type, raw, ref n, pp) == HidpStatusSuccess)
                for (int i = 0; i < n; i++) if (!ids.Contains(raw[i * CapsStructSize + 2])) ids.Add(raw[i * CapsStructSize + 2]);
        }
        if (ids.Count == 0) ids.Add(0);
        return ids;
    }

    static string GetFeature(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte id, int len) {
        var buf = new byte[len]; buf[0] = id;
        return HidD_GetFeature(h, buf, len) ? BitConverter.ToString(buf).Replace("-", " ") : "read failed " + Marshal.GetLastWin32Error();
    }

    /// Returns true if a report was written to the multi-axis collection.
    public static bool Wake(Action<string> log) {
        bool sent = false;
        foreach (var path in K30Paths()) {
            using (var q = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                if (q.IsInvalid) continue;
                IntPtr pp;
                if (!HidD_GetPreparsedData(q, out pp)) continue;
                try {
                    var caps = new byte[64];
                    HidP_GetCaps(pp, caps);
                    ushort usage = BitConverter.ToUInt16(caps, 0), page = BitConverter.ToUInt16(caps, 2);
                    ushort inLen = BitConverter.ToUInt16(caps, 4), outLen = BitConverter.ToUInt16(caps, 6), featLen = BitConverter.ToUInt16(caps, 8);
                    // after Usage, UsagePage, 3 lengths (10 bytes) and Reserved[17] (34 bytes): 10 counts
                    ushort outBtn = BitConverter.ToUInt16(caps, 52), outVal = BitConverter.ToUInt16(caps, 54);
                    ushort featBtn = BitConverter.ToUInt16(caps, 58), featVal = BitConverter.ToUInt16(caps, 60);
                    log(string.Format("hid 0x{0:X2}/0x{1:X2} in={2} out={3} feature={4}", page, usage, inLen, outLen, featLen));
                    if (page != 0x01 || (usage != 0x0E && usage != 0x08)) continue; // only the (system) multi-axis controller collection

                    using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                        if (h.IsInvalid) { log("hid wake: cannot open multi-axis collection (" + Marshal.GetLastWin32Error() + ")"); continue; }
                        if (featLen > 0) {
                            foreach (var id in ReportIds(pp, HidpFeature, featVal, featBtn)) {
                                log("hid wake: feature id=" + id + " before = " + GetFeature(h, id, featLen));
                                var buf = new byte[featLen]; buf[0] = id;
                                bool ok = HidD_SetFeature(h, buf, buf.Length);
                                log("hid wake: feature id=" + id + " set 00.. -> " + (ok ? "ok" : "failed " + Marshal.GetLastWin32Error()));
                                string after = GetFeature(h, id, featLen);
                                log("hid wake: feature id=" + id + " after  = " + after + (after.StartsWith(id.ToString("X2") + " 00 00") ? "" : "  (WARNING: not 00 00, the K30 may stay on its built-in keys)"));
                                sent |= ok;
                            }
                        } else if (outLen > 0) {
                            foreach (var id in ReportIds(pp, HidpOutput, outVal, outBtn)) {
                                var buf = new byte[outLen]; buf[0] = id;
                                bool ok = HidD_SetOutputReport(h, buf, buf.Length);
                                log("hid wake: output id=" + id + " len=" + outLen + " -> " + (ok ? "ok" : "failed " + Marshal.GetLastWin32Error()));
                                sent |= ok;
                            }
                        } else log("hid wake: multi-axis collection has no feature/output report");
                    }
                } finally { HidD_FreePreparsedData(pp); }
            }
        }
        return sent;
    }
}

// ---------------------------------------------------------------- Claude desktop UI (via UI Automation)

/// Drives the Claude desktop composer's "Model: …" and "Effort: …" controls through the
/// accessibility tree, so no keyboard focus or shortcut is needed. All calls block; run them on UiaWorker.
class ClaudeUi {
    static readonly string[] SkipSubtrees = { "Chat messages", "Sidebar" };
    public static readonly string[] EffortLabels = { "Low", "Medium", "High", "Extra", "Max", "Ultracode" };

    public List<string> Models;           // cached model list (read once from the menu)
    AutomationElement effortButton, effortSlider;
    int effortValue, effortStart;

    static AutomationElement Window() {
        foreach (var p in Process.GetProcessesByName("Claude")) {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "Claude") return AutomationElement.FromHandle(p.MainWindowHandle); }
            catch { }
        }
        return null;
    }

    static AutomationElement Find(AutomationElement root, Func<AutomationElement.AutomationElementInformation, bool> pred) {
        if (root == null) return null;
        var w = TreeWalker.ControlViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0) {
            var e = stack.Pop();
            AutomationElement.AutomationElementInformation c;
            try { c = e.Current; } catch { continue; }
            if (pred(c)) return e;
            if (Array.IndexOf(SkipSubtrees, c.Name) >= 0) continue;
            var kids = new List<AutomationElement>();
            try { for (var k = w.GetFirstChild(e); k != null; k = w.GetNextSibling(k)) kids.Add(k); } catch { }
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
        return null;
    }

    static AutomationElement WaitFind(Func<AutomationElement> f, int ms) {
        var sw = Stopwatch.StartNew();
        do {
            var e = f();
            if (e != null) return e;
            Thread.Sleep(40);
        } while (sw.ElapsedMilliseconds < ms);
        return null;
    }

    static AutomationElement Button(AutomationElement root, string prefix) {
        return Find(root, c => c.ControlType == ControlType.Button && !c.IsOffscreen && (c.Name ?? "").StartsWith(prefix));
    }

    static void Expand(AutomationElement e) { ((ExpandCollapsePattern)e.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); }
    static void Collapse(AutomationElement e) {
        try { ((ExpandCollapsePattern)e.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch { }
    }

    // ---- model

    /// Current model name from the composer button, or null if Claude / a session isn't visible.
    public string CurrentModel() {
        var b = Button(Window(), "Model: ");
        return b == null ? null : b.Current.Name.Substring("Model: ".Length);
    }

    public List<string> ReadModels() {
        var root = Window();
        var b = Button(root, "Model: ");
        if (b == null) return null;
        Expand(b);
        var menu = WaitFind(() => Find(root, c => c.ControlType == ControlType.Menu && !c.IsOffscreen), 1500);
        var list = new List<string>();
        if (menu != null) {
            var w = TreeWalker.ControlViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(menu);
            var found = new List<AutomationElement>();
            while (stack.Count > 0) {
                var e = stack.Pop();
                if (e.Current.ControlType == ControlType.RadioButton) { found.Add(e); continue; }
                var kids = new List<AutomationElement>();
                for (var k = w.GetFirstChild(e); k != null; k = w.GetNextSibling(k)) kids.Add(k);
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
            list.AddRange(found.Select(e => e.Current.Name));
        }
        Collapse(b);
        return list.Count > 0 ? list : null;
    }

    /// Selects a model. Returns "ok", "confirm" (Claude shows its "Switch model?" prompt) or an error text.
    public string SelectModel(string name) {
        var root = Window();
        var b = Button(root, "Model: ");
        if (b == null) return "Claude composer not found";
        Expand(b);
        var item = WaitFind(() => Find(root, c => c.ControlType == ControlType.RadioButton && !c.IsOffscreen && c.Name == name), 1500);
        if (item == null) { Collapse(b); return "'" + name + "' not in the model menu"; }
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000) {
            Thread.Sleep(60);
            if (SwitchDialog(root) != null) return "confirm";
            var now = Button(root, "Model: ");
            if (now != null && now.Current.Name == "Model: " + name) return "ok";
        }
        return "no response from Claude";
    }

    static AutomationElement SwitchDialog(AutomationElement root) {
        return Find(root, c => c.ControlType == ControlType.Window && c.Name == "Switch model?");
    }

    public bool ConfirmSwitch() {
        var dlg = SwitchDialog(Window());
        if (dlg == null) return false;
        var ok = Find(dlg, c => c.ControlType == ControlType.Button && c.Name == "Switch model");
        if (ok == null) return false;
        ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return true;
    }

    public bool OpenMenu(string prefix) {
        var b = Button(Window(), prefix);
        if (b == null) return false;
        Expand(b);
        return true;
    }

    // ---- composer focus
    // Opening/closing the model menu or effort panel leaves keyboard focus on that button, so dictation
    // (DicTray) or typing would no longer land in the message box. We remember the composer ("Prompt")
    // when a gesture starts and hand focus back to it when the gesture ends.

    AutomationElement composer;
    public AutomationElement Composer { get { return composer; } }

    static bool IsComposer(AutomationElement e) {
        try {
            var c = e.Current;
            return c.ControlType == ControlType.Edit && (c.Name == "Prompt" || (c.ClassName ?? "").Contains("ProseMirror"));
        } catch { return false; }
    }

    static int ClaudePid() {
        foreach (var p in Process.GetProcessesByName("Claude")) {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "Claude") return p.Id; } catch { }
        }
        return 0;
    }

    /// Remembers the composer if it currently has keyboard focus. Returns it (or null).
    public AutomationElement RememberComposer() {
        try {
            var f = AutomationElement.FocusedElement;
            composer = f != null && IsComposer(f) ? f : null;
        } catch { composer = null; }
        return composer;
    }

    /// Gives focus back to the remembered composer. Without one, only refocuses the composer when focus is
    /// still somewhere inside Claude, so we never pull focus away from another app.
    public void RestoreComposer() { RestoreComposer(composer); }

    public void RestoreComposer(AutomationElement target) {
        try {
            // Menus hand focus back to their trigger button as they close; let that happen first.
            Thread.Sleep(120);
            // Only act while the user is still in Claude: never pull focus away from another app.
            var f = AutomationElement.FocusedElement;
            int pid = ClaudePid();
            if (f == null || pid == 0 || f.Current.ProcessId != pid) return;
            if (target == null) {
                target = Find(Window(), c => c.ControlType == ControlType.Edit && !c.IsOffscreen && c.Name == "Prompt");
                if (target == null) return;
            }
            for (int i = 0; i < 3; i++) {
                if (i > 0) Thread.Sleep(150);
                try { target.SetFocus(); }
                catch {
                    target = Find(Window(), c => c.ControlType == ControlType.Edit && !c.IsOffscreen && c.Name == "Prompt");
                    if (target == null) return;
                    continue;
                }
                Thread.Sleep(60);
                var now = AutomationElement.FocusedElement;
                if (now != null && IsComposer(now)) return;
            }
        } catch { }
    }

    /// For menus opened by a key (K6/K7) and used by hand: once the menu, panel or "Switch model?"
    /// prompt is gone, focus goes back to the composer.
    public void RefocusWhenClosed(string prefix, AutomationElement remembered) {
        var t = new Thread(() => {
            var root = Window();
            bool seen = false;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 60000) {
                Thread.Sleep(300);
                bool open;
                try {
                    open = prefix == "Model: "
                        ? Find(root, c => (c.ControlType == ControlType.Menu && !c.IsOffscreen) || (c.ControlType == ControlType.Window && c.Name == "Switch model?")) != null
                        : Find(root, c => c.ControlType == ControlType.Window && c.Name == "Effort") != null;
                } catch { return; }
                if (open) seen = true;
                else if (seen) { RestoreComposer(remembered); return; }
            }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    // ---- effort

    /// Opens the effort panel and returns the current slider step, or -1.
    public int EffortBegin() {
        var root = Window();
        effortButton = Button(root, "Effort: ");
        if (effortButton == null) return -1;
        Expand(effortButton);
        effortSlider = WaitFind(() => Find(root, c => c.ControlType == ControlType.Slider && !c.IsOffscreen && c.Name == "Effort"), 1500);
        if (effortSlider == null) { Collapse(effortButton); return -1; }
        effortValue = effortStart = (int)((RangeValuePattern)effortSlider.GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value;
        return effortValue;
    }

    /// Moves the slider by delta (clamped to 0..max, or to where it started if that was higher). Returns the new step.
    public int EffortStep(int delta, int max) {
        if (effortSlider == null) return -1;
        max = Math.Max(max, effortStart);
        var rv = (RangeValuePattern)effortSlider.GetCurrentPattern(RangeValuePattern.Pattern);
        int v = Math.Max((int)rv.Current.Minimum, Math.Min(Math.Min(max, (int)rv.Current.Maximum), effortValue + delta));
        if (v != effortValue) { rv.SetValue(v); effortValue = v; }
        return effortValue;
    }

    public void EffortEnd() {
        if (effortButton != null) Collapse(effortButton);
        effortButton = null; effortSlider = null;
    }
}

/// Single background thread for UI Automation calls (keeps them off the WinForms thread, in order).
class UiaWorker {
    readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
    public UiaWorker(Action<Exception> onError) {
        var t = new Thread(() => {
            foreach (var a in queue.GetConsumingEnumerable()) {
                try { a(); } catch (Exception e) { onError(e); }
            }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }
    public void Post(Action a) { queue.Add(a); }
}

// ---------------------------------------------------------------- foreground app detection

/// Which process owns the window currently in the foreground — used to pick a per-application
/// profile. Deliberately simple (no UI Automation): fast enough to call on every key press.
static class AppDetect {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// The foreground window's process name (e.g. "vivaldi", no ".exe"), or null if it can't be read.
    public static string ForegroundProcessName() {
        try {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
        } catch { return null; }
    }
}

// ---------------------------------------------------------------- window switcher

/// The switcher's timeline: top-level windows in the order you went to them, oldest first. Moving
/// through it with the roller never reorders it, so rolling back and then forward returns you where
/// you were, like a browser's Back and Forward. Only going to a window some other way (a click, the
/// taskbar, Alt-Tab) is a new visit, which moves that window to the present end. Windows' own
/// most-recently-used order can't do this: every switch, including the switcher's, reshuffles it.
class SwitchHistory {
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    readonly List<IntPtr> visits = new List<IntPtr>();
    WinEventProc proc; // kept in a field so the hook's delegate isn't garbage-collected
    IntPtr ours;       // the window the switcher is activating: its foreground event isn't a visit
    DateTime oursAt;

    /// Starts listening for foreground changes; must run on a thread with a message loop (the UI thread).
    public void Start() {
        proc = OnForeground;
        SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        Visit(GetForegroundWindow());
    }

    void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) {
        if (idObject != 0 /* OBJID_WINDOW */) return;
        var h = GetAncestor(hwnd, 3 /* GA_ROOTOWNER: a dialog counts as its app's main window */);
        if (h == ours && (DateTime.UtcNow - oursAt).TotalSeconds < 3) { ours = IntPtr.Zero; return; }
        Visit(h);
    }

    void Visit(IntPtr h) {
        if (h == IntPtr.Zero) return;
        visits.Remove(h);
        visits.Add(h);
        if (visits.Count > 400) visits.RemoveAt(0);
    }

    /// The switcher is about to bring this window forward: that's moving along the timeline, not a visit.
    public void SwitchingTo(IntPtr h) { ours = h; oursAt = DateTime.UtcNow; }

    /// Puts a snapshot (most recent first) in timeline order, oldest first: windows not visited since
    /// K30 Controller started come first, in Windows' recency order, then the visited ones as visited.
    public List<SwitchTarget> Order(List<SwitchTarget> mostRecentFirst) {
        visits.RemoveAll(h => !IsWindow(h));
        var byHwnd = mostRecentFirst.ToDictionary(t => t.Hwnd);
        var visited = new HashSet<IntPtr>(visits);
        var order = mostRecentFirst.Where(t => !visited.Contains(t.Hwnd)).Reverse().ToList();
        foreach (var h in visits) { SwitchTarget t; if (byHwnd.TryGetValue(h, out t)) order.Add(t); }
        return order;
    }
}

class SwitchTarget {
    public IntPtr Hwnd;
    public string Process;  // process name without ".exe", matched against the switcher's allow/block list
    public string Label;    // the app's friendly name (file description), shown in bold
    public string Title;    // the window's title
    public Icon Icon;       // the app's standard icon (the settings window uses it)
    public Bitmap Image;    // the same icon at 256 px, drawn large in the switcher
}

/// Lists the windows Alt-Tab would show, in the same most-recently-used order (EnumWindows walks the
/// Z-order top down), and brings one to the foreground.
static class WindowList {
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000, WsExNoActivate = 0x08000000;
    static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Icon> Icons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Bitmap> Images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

    public static IntPtr Foreground() { return GetForegroundWindow(); }

    public static List<SwitchTarget> Snapshot(Func<string, bool> include) {
        var list = new List<SwitchTarget>();
        int own = Environment.ProcessId;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            long ex = GetWindowLongPtr(h, -20 /* GWL_EXSTYLE */).ToInt64();
            if ((ex & WsExAppWindow) == 0 && (ex & (WsExToolWindow | WsExNoActivate)) != 0) return true;
            int cloaked;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "Progman" || cls.ToString() == "Shell_TrayWnd") return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == own) return true;
            string path = ImagePath(pid);
            string proc = path != null ? Path.GetFileNameWithoutExtension(path) : null;
            if (proc == null || !include(proc)) return true;
            var title = new StringBuilder(len + 1);
            GetWindowText(h, title, title.Capacity);
            bool frameHost = proc.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase); // Store apps
            list.Add(new SwitchTarget {
                Hwnd = h, Process = proc, Title = title.ToString(),
                Label = frameHost ? title.ToString() : LabelFor(path, proc),
                Icon = frameHost ? null : IconFor(path),
                Image = frameHost ? null : ImageFor(path)
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// Brings a window to the foreground. Windows only lets the app that received the last input event
    /// do that, so an injected Alt press (the standard trick, also what Alt-Tab itself relies on) comes
    /// first. Returns whether the window really is in front afterwards.
    public static bool Activate(IntPtr h) {
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        Output.Down(new ushort[] { 0xA4 });
        SetForegroundWindow(h);
        Output.Up(new ushort[] { 0xA4 });
        if (GetForegroundWindow() == h) return true;
        SwitchToThisWindow(h, true);
        Thread.Sleep(40);
        return GetForegroundWindow() == h;
    }

    static string ImagePath(uint pid) {
        IntPtr p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION: works for admin processes too */, false, pid);
        if (p == IntPtr.Zero) return null;
        try {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
        } finally { CloseHandle(p); }
    }

    static string LabelFor(string path, string proc) {
        string label;
        if (Labels.TryGetValue(path, out label)) return label;
        try { label = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(label)) label = proc;
        Labels[path] = label;
        return label;
    }

    static Icon IconFor(string path) {
        Icon icon;
        if (Icons.TryGetValue(path, out icon)) return icon;
        try { icon = Icon.ExtractAssociatedIcon(path); } catch { icon = null; }
        Icons[path] = icon;
        return icon;
    }

    /// The exe's icon extracted at 256 px (sharp at the switcher's large size on high-DPI monitors too),
    /// falling back to the standard 32 px one for files that don't carry a large icon.
    static Bitmap ImageFor(string path) {
        Bitmap bmp;
        if (Images.TryGetValue(path, out bmp)) return bmp;
        try {
            using (var big = Icon.ExtractIcon(path, 0, 256)) bmp = big != null ? big.ToBitmap() : null;
        } catch { bmp = null; }
        if (bmp == null) { var small = IconFor(path); if (small != null) bmp = small.ToBitmap(); }
        if (bmp != null) bmp = Trim(bmp);
        Images[path] = bmp;
        return bmp;
    }

    /// Crops an icon to its visible pixels. Apps leave very different amounts of empty margin around
    /// their logos; without it, every logo fills its tile the same way and they all look the same size.
    static Bitmap Trim(Bitmap src) {
        int w = src.Width, h = src.Height, minX = w, minY = h, maxX = -1, maxY = -1, solid = 0;
        var data = src.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try {
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++) {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                    if (row[x * 4 + 3] > 24) { // alpha: ignore faint shadows and antialiasing haze
                        solid++;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            }
        } finally { src.UnlockBits(data); }
        if (maxX < 0) return src;
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var crop = cw == w && ch == h ? src : src.Clone(new Rectangle(minX, minY, cw, ch), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (crop != src) src.Dispose();
        // An icon that is already a filled (rounded) square, like Vivaldi's or Claude's, becomes the tile
        // itself instead of sitting inside one: SwitcherOsd checks this flag.
        float aspect = (float)cw / ch;
        crop.Tag = aspect > 0.9f && aspect < 1.1f && solid >= 0.88f * cw * ch;
        return crop;
    }
}

/// The switcher's list, laid out like macOS's app switcher: a dark card centred on one monitor (K30App
/// shows one per monitor) with a horizontal row of large icons, each app's name underneath, and the
/// selected tile highlighted. When there are more windows than fit, the row scrolls sideways to keep the
/// selection in view, with arrows on the side that has more. Never takes focus.
class SwitcherOsd : Form {
    static readonly Color BackgroundColor = Color.FromArgb(15, 15, 15);
    static readonly Color BorderColor = Color.FromArgb(31, 255, 255, 255);
    static readonly Color LabelColor = Color.White;
    static readonly Color DimColor = Color.FromArgb(150, 255, 255, 255);
    static readonly Color SelectedFill = Color.FromArgb(60, 255, 255, 255);
    static readonly Color PlateTop = Color.FromArgb(62, 62, 66);
    static readonly Color PlateBottom = Color.FromArgb(40, 40, 44);
    static readonly Color PlateRim = Color.FromArgb(40, 255, 255, 255);
    const int TileWidth = 140, TileHeight = 156, IconSize = 96, Pad = 22, DotSpace = 10, MaxTiles = 9, Radius = 22;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HwndTopmost = new IntPtr(-1);

    List<SwitchTarget> items = new List<SwitchTarget>();
    int selected, visible, current = -1; // current: the window you were in, marked with a dot
    float scale;
    Font labelFont, arrowFont, initialFont;

    public SwitcherOsd() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BackgroundColor;
        Opacity = 0.94;
        DoubleBuffered = true;
        SetScale(1f);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x08000000 | 0x80 | 0x8; // TRANSPARENT | NOACTIVATE | TOOLWINDOW | TOPMOST
            return cp;
        }
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x02E0) return; // WM_DPICHANGED: ShowList sizes the card for its monitor itself
        base.WndProc(ref m);
    }

    void SetScale(float s) {
        if (labelFont != null && Math.Abs(s - scale) < 0.01f) return;
        scale = s;
        if (labelFont != null) { labelFont.Dispose(); arrowFont.Dispose(); initialFont.Dispose(); }
        labelFont = new Font("Segoe UI Semibold", 14f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        arrowFont = new Font("Segoe UI", 26f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        initialFont = new Font("Segoe UI Semibold", 44f * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int S(float v) { return (int)Math.Round(v * scale); }

    public void ShowList(List<SwitchTarget> list, int sel, int current, Screen screen) {
        items = list;
        selected = sel;
        this.current = current;
        SetScale(Osd.ScaleFor(screen));
        var area = screen.WorkingArea;
        int fit = Math.Max(1, (int)((area.Width * 0.92 - S(Pad * 2)) / S(TileWidth)));
        visible = Math.Min(items.Count, Math.Min(MaxTiles, fit));
        int w = S(Pad * 2) + visible * S(TileWidth), h = S(Pad * 2 + TileHeight + DotSpace);
        int x = area.X + (area.Width - w) / 2, y = area.Y + (area.Height - h) / 2;
        if (!Visible) Show();
        SetWindowPos(Handle, HwndTopmost, x, y, w, h, 0x10 | 0x40); // NOACTIVATE | SHOWWINDOW
        using (var path = Osd.RoundedRect(new Rectangle(0, 0, w, h), S(Radius))) {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
        Invalidate();
    }

    public void Select(int sel) { selected = sel; Invalidate(); }

    /// The same rounded tile behind every app's icon, macOS style: every app gets one shape and size,
    /// whatever its own logo looks like. A soft top-to-bottom gradient with a faint rim, like a raised key.
    void DrawPlate(Graphics g, Rectangle r) {
        using (var path = Osd.RoundedRect(r, (int)(r.Width * 0.225f)))
        using (var fill = new LinearGradientBrush(r, PlateTop, PlateBottom, LinearGradientMode.Vertical))
        using (var rim = new Pen(PlateRim, Math.Max(1f, scale))) {
            g.FillPath(fill, path);
            g.DrawPath(rim, path);
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var path = Osd.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(Radius)))
        using (var border = new Pen(BorderColor, 1f))
            g.DrawPath(border, path);

        if (visible == 0) return;
        // Scroll so the selection sits in the middle where possible, like the vertical list did.
        int first = Math.Max(0, Math.Min(selected - visible / 2, items.Count - visible));
        int tw = S(TileWidth), th = S(TileHeight), icon = S(IconSize), top = S(Pad);
        using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var labelBrush = new SolidBrush(LabelColor))
        using (var dimBrush = new SolidBrush(DimColor))
        using (var selBrush = new SolidBrush(SelectedFill)) {
            for (int v = 0; v < visible; v++) {
                int i = first + v;
                var t = items[i];
                int x = S(Pad) + v * tw;
                if (i == selected)
                    using (var sp = Osd.RoundedRect(new Rectangle(x + S(4), top, tw - S(8), th), S(16)))
                        g.FillPath(selBrush, sp);
                var iconRect = new Rectangle(x + (tw - icon) / 2, top + S(14), icon, icon);
                if (t.Image != null && t.Image.Tag is bool && (bool)t.Image.Tag) {
                    // Already a square tile: fill the plate's shape with it, so it matches the others' outline.
                    using (var clip = Osd.RoundedRect(iconRect, (int)(icon * 0.225f))) {
                        var state = g.Save();
                        g.SetClip(clip);
                        g.DrawImage(t.Image, iconRect);
                        g.Restore(state);
                    }
                    using (var clip = Osd.RoundedRect(iconRect, (int)(icon * 0.225f)))
                    using (var rim = new Pen(PlateRim, Math.Max(1f, scale)))
                        g.DrawPath(rim, clip);
                } else if (t.Image != null) {
                    DrawPlate(g, iconRect);
                    // The (trimmed) logo sits inside the plate at a fixed size, keeping its proportions.
                    int inner = (int)(icon * 0.66f);
                    float k = Math.Min((float)inner / t.Image.Width, (float)inner / t.Image.Height);
                    float iw = t.Image.Width * k, ih = t.Image.Height * k;
                    g.DrawImage(t.Image, new RectangleF(iconRect.X + (icon - iw) / 2, iconRect.Y + (icon - ih) / 2, iw, ih));
                } else {
                    // No icon (Store apps): the plate with the name's first letter.
                    DrawPlate(g, iconRect);
                    string initial = string.IsNullOrEmpty(t.Label) ? "?" : t.Label.Substring(0, 1).ToUpperInvariant();
                    g.DrawString(initial, initialFont, labelBrush, iconRect, fmt);
                }
                var nameRect = new RectangleF(x + S(8), iconRect.Bottom + S(6), tw - S(16), th - (iconRect.Bottom - top) - S(10));
                g.DrawString(t.Label, labelFont, i == selected ? labelBrush : dimBrush, nameRect, fmt);
                // "You are here": a small dot under the window you were in, like the Dock's running-app dot.
                if (i == current) {
                    int d = S(6);
                    g.FillEllipse(labelBrush, x + (tw - d) / 2, top + th + S(DotSpace) / 2 - d / 2 + S(2), d, d);
                }
            }
            // More windows beyond the edges: an arrow in the margin on that side.
            if (first > 0)
                g.DrawString("‹", arrowFont, dimBrush, new RectangleF(0, 0, S(Pad), Height), fmt);
            if (first + visible < items.Count)
                g.DrawString("›", arrowFont, dimBrush, new RectangleF(Width - S(Pad), 0, S(Pad), Height), fmt);
        }
    }
}

// ---------------------------------------------------------------- on-screen display

/// Pop-up styled after DicTray's voice overlay (scripts/windows-voice-overlay): dark rounded card at the
/// bottom centre of the monitor holding the focused window, with a coloured status dot.
class Osd : Form {
    static readonly Color BackgroundColor = Color.FromArgb(15, 15, 15);
    static readonly Color BorderColor = Color.FromArgb(31, 255, 255, 255);
    static readonly Color TitleColor = Color.White;
    static readonly Color DetailColor = Color.FromArgb(189, 255, 255, 255);
    public static readonly Color Red = Color.FromArgb(255, 59, 48);
    public static readonly Color Green = Color.FromArgb(52, 199, 89);
    public static readonly Color Blue = Color.FromArgb(0, 122, 255);
    public static readonly Color Teal = Color.FromArgb(90, 200, 250);
    public static readonly Color Gray = Color.FromArgb(142, 142, 147);
    public static readonly Color Orange = Color.FromArgb(255, 149, 0);
    public static readonly Color Purple = Color.FromArgb(175, 82, 222);

    const int BaseWidth = 292, MaxWidth = 560, BaseHeight = 78, EdgeMargin = 18, Radius = 16;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    static readonly IntPtr HwndTopmost = new IntPtr(-1);
    const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    string title = "", sub = "";
    Color accent = Teal;
    float scale = 1f;
    Font titleFont, detailFont;
    readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();

    public string Title { get { return title; } }

    public Osd() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BackgroundColor;
        Opacity = 0.88;
        DoubleBuffered = true;
        Size = new Size(BaseWidth, BaseHeight);
        SetScale(1f);
        hide.Tick += (s, e) => { hide.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x08000000 | 0x80 | 0x8; // TRANSPARENT | NOACTIVATE | TOOLWINDOW | TOPMOST
            return cp;
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x02E0) return; // WM_DPICHANGED: Flash() sizes the card for its monitor itself
        base.WndProc(ref m);
    }

    void SetScale(float s) {
        if (titleFont != null && Math.Abs(s - scale) < 0.01f) return;
        scale = s;
        if (titleFont != null) titleFont.Dispose();
        if (detailFont != null) detailFont.Dispose();
        titleFont = new Font("Segoe UI", 15f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        detailFont = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int S(float v) { return (int)Math.Round(v * scale); }

    /// The monitor holding the focused element (via UI Automation, like DicTray's focused-window bounds),
    /// else the one under the mouse, else the primary one. The UIA lookup runs off the UI thread with a
    /// short timeout so a hung target app (or our own tray menu) can't stall the pop-up.
    static Screen TargetScreen() {
        try {
            var t = Task.Run(() => {
                var el = AutomationElement.FocusedElement;
                if (el == null) return Rectangle.Empty;
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 1 || r.Height < 1) return Rectangle.Empty;
                return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            });
            if (t.Wait(200) && !t.Result.IsEmpty) return Screen.FromRectangle(t.Result);
        } catch { }
        return Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen;
    }

    internal static float ScaleFor(Screen screen) {
        try {
            var b = screen.Bounds;
            var mon = MonitorFromPoint(new Point(b.X + b.Width / 2, b.Y + b.Height / 2), 2 /* NEAREST */);
            uint dx, dy;
            if (GetDpiForMonitor(mon, 0 /* effective */, out dx, out dy) == 0 && dx > 0) return dx / 96f;
        } catch { }
        return 1f;
    }

    public void Flash(string t, string s, int ms = 1400, Color? dot = null) {
        title = t ?? ""; sub = s ?? "";
        if (dot.HasValue) accent = dot.Value;

        var screen = TargetScreen();
        SetScale(ScaleFor(screen));
        int textWidth = Math.Max(
            S(38 + 18) + TextRenderer.MeasureText(title, titleFont).Width,
            S(18 + 18) + TextRenderer.MeasureText(sub, detailFont).Width);
        int w = Math.Max(S(BaseWidth), Math.Min(S(MaxWidth), textWidth));
        int h = S(BaseHeight);
        var area = screen.WorkingArea;
        int x = area.X + (area.Width - w) / 2;
        int y = area.Y + area.Height - h - S(EdgeMargin);

        if (!Visible) Show();
        // Re-assert topmost on every appearance: another topmost window may have been raised above us.
        SetWindowPos(Handle, HwndTopmost, x, y, w, h, SwpNoActivate | SwpShowWindow);
        UpdateRegion();
        Invalidate();
        hide.Stop(); hide.Interval = ms; hide.Start();
    }

    void UpdateRegion() {
        using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), S(Radius))) {
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }

    internal static GraphicsPath RoundedRect(Rectangle rect, int radius) {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (rect.Width <= d || rect.Height <= d) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(Radius)))
        using (var border = new Pen(BorderColor, 1f))
            g.DrawPath(border, path);
        using (var dot = new SolidBrush(accent))
            g.FillEllipse(dot, S(18), S(21), S(12), S(12));
        using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
        using (var tb = new SolidBrush(TitleColor))
        using (var db = new SolidBrush(DetailColor)) {
            g.DrawString(title, titleFont, tb, new RectangleF(S(38), S(16), Width - S(56), S(22)), fmt);
            g.DrawString(sub, detailFont, db, new RectangleF(S(18), S(42), Width - S(36), S(26)), fmt);
        }
    }
}

// ---------------------------------------------------------------- app

class K30App : ApplicationContext {
    static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
    static readonly string ConfigPath = Path.Combine(Dir, "k30-config.json");
    static readonly string LogPath = Path.Combine(Dir, "k30.log");
    static readonly string[] KeyNames = { "K1", "K2", "K3", "K4", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "DIAL" };

    readonly Control ui = new Control();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Osd osd = new Osd();
    readonly System.Windows.Forms.Timer idle = new System.Windows.Forms.Timer();
    Config cfg;
    int mode;
    // The dial-mode list `mode` currently indexes into: cfg.DialModes, or one app profile's DialModes.
    // Only re-resolved at the start of a live dial interaction (RefreshDialProfile) — see its comment.
    List<DialMode> currentModes;
    string activeDialProfileKey; // the AppProfiles key behind currentModes, or null for the global list
    int lastMask;
    bool menuOpen, altHeld;
    readonly Dictionary<int, ushort[]> held = new Dictionary<int, ushort[]>();
    BluetoothLEDevice dev;
    GattCharacteristic ffe1, ffe2;
    bool connected;

    readonly ClaudeUi claude = new ClaudeUi();
    readonly UiaWorker uia;
    // model dial gesture: turning moves modelPending through modelList; applied when the dial rests
    bool modelActive, modelLoaded, awaitingSwitchConfirm;
    List<string> modelList;
    int modelCurrent, modelPending, modelDelta;
    // effort dial gesture: Claude's effort panel stays open while turning
    bool effortActive;
    // window switcher (roller switch:next / switch:prev): the list stays up while rolling, commits when it rests
    readonly List<SwitcherOsd> switchers = new List<SwitcherOsd>(); // one list per monitor, all showing the same thing
    readonly System.Windows.Forms.Timer switchTimer = new System.Windows.Forms.Timer();
    List<SwitchTarget> switchList;
    int switchIndex, switchCurrent; // the highlight, and the window you were in when the list opened (-1: none)
    readonly SwitchHistory history = new SwitchHistory();
    // key sheet: one per monitor, closed by the next K30 input or after a while
    readonly List<KeySheet> sheets = new List<KeySheet>();
    readonly System.Windows.Forms.Timer sheetTimer = new System.Windows.Forms.Timer();
    bool switchActive;

    public K30App() {
        ui.CreateControl();
        var h = ui.Handle; // force handle so BeginInvoke works from BLE threads
        uia = new UiaWorker(e => { Log("uia: " + e); UI(() => osd.Flash("Claude control failed", e.Message, 2500, Osd.Red)); });
        idle.Tick += (s, e) => { idle.Stop(); FinishDialGesture(); };
        switchTimer.Tick += (s, e) => { switchTimer.Stop(); CommitSwitch(); };
        sheetTimer.Tick += (s, e) => HideKeySheet();

        tray.Icon = MakeIcon();
        tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMode(); };
        tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        BuildMenu();

        LoadConfig(false);
        Log("started");
        Task.Run(() => ConnectLoop());
        ListenForOpenSettings();
        history.Start();
    }

    /// A second launch of K30.exe signals this event instead of starting another instance (see Main).
    void ListenForOpenSettings() {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenSettingsEvent);
        var t = new Thread(() => { while (true) { ev.WaitOne(); UI(OpenSettings); } });
        t.IsBackground = true;
        t.Start();
    }

    void OpenSettings() {
        var running = new List<RunningApp>();
        try {
            foreach (var w in WindowList.Snapshot(p => true))
                if (!running.Any(r => r.Process.Equals(w.Process, StringComparison.OrdinalIgnoreCase)))
                    running.Add(new RunningApp {
                        Process = w.Process, Icon = w.Icon,
                        Label = w.Process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? "Windows Store apps" : w.Label
                    });
        } catch (Exception e) { Log("settings: " + e.Message); }
        try {
            SettingsHost.Open(ConfigPath, running, Screen.FromPoint(Cursor.Position).WorkingArea, () => UI(() => LoadConfig(true)));
        } catch (Exception e) {
            Log("settings: " + e);
            MessageBox.Show("Couldn't open the settings: " + e.Message, "K30 Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void BuildMenu() {
        var m = tray.ContextMenuStrip;
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem(connected ? "K30 connected" : "K30 not connected") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        if (cfg != null)
            for (int i = 0; i < cfg.DialModes.Count; i++) {
                int idx = i;
                m.Items.Add(new ToolStripMenuItem("Dial: " + cfg.DialModes[i].Name, null, (s, e) => { activeDialProfileKey = null; SetMode(idx, cfg.DialModes); }) { Checked = idx == mode && currentModes == cfg.DialModes });
            }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Settings…", null, (s, e) => OpenSettings()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add("Reload config file", null, (s, e) => LoadConfig(true));
        m.Items.Add("Exit", null, (s, e) => Quit());
        tray.Text = "K30 Controller — " + (connected ? "connected" : "waiting for K30");
    }

    void LoadConfig(bool announce) {
        try {
            cfg = Config.Load(ConfigPath);
            foreach (var kv in cfg.Keys) Validate(kv.Value);
            foreach (var p in cfg.AppProfiles.Values) foreach (var kv in p.Keys) Validate(kv.Value);
            currentModes = cfg.DialModes;
            activeDialProfileKey = null;
            if (mode >= currentModes.Count) mode = 0;
            if (announce) osd.Flash("Config reloaded", cfg.DialModes.Count + " dial modes, " + cfg.AppProfiles.Count + " app profiles", 1400, Osd.Teal);
        } catch (Exception e) {
            Log("config error: " + e.Message);
            MessageBox.Show("k30-config.json: " + e.Message, "K30 Controller", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (cfg == null) { File.WriteAllText(ConfigPath + ".broken", File.ReadAllText(ConfigPath)); File.WriteAllText(ConfigPath, Config.Default); cfg = Config.Load(ConfigPath); }
        }
        BuildMenu();
    }

    internal static void Validate(string action) {
        if (action == null) return;
        string a = action.Trim();
        if (a.StartsWith("hold ", StringComparison.OrdinalIgnoreCase)) a = a.Substring(5);
        if (a.Equals("nextMode", StringComparison.OrdinalIgnoreCase) || a.Equals("prevMode", StringComparison.OrdinalIgnoreCase) || a.StartsWith("wheel", StringComparison.OrdinalIgnoreCase) || a.Length == 0) return;
        if (a.Equals("claude:model", StringComparison.OrdinalIgnoreCase) || a.Equals("claude:effort", StringComparison.OrdinalIgnoreCase)) return;
        if (a.Equals("showKeys", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var step in a.Split(',')) {
            string s = step.Trim();
            if (s.Length > 0 && !s.StartsWith("wheel", StringComparison.OrdinalIgnoreCase)) Output.Parse(s);
        }
    }

    void UI(Action a) { ui.BeginInvoke(a); }

    // ---------------- BLE

    void ConnectLoop() {
        while (true) {
            try {
                if (!connected) TryConnect();
            } catch (Exception e) {
                Log("connect: " + e.Message);
                SetConnected(false);
            }
            Thread.Sleep(4000);
        }
    }

    void TryConnect() {
        if (dev == null) {
            if (cfg.Address != 0) {
                dev = W(BluetoothLEDevice.FromBluetoothAddressAsync(cfg.Address));
            } else {
                // "address": "auto" → the first paired BLE device named like the K30 ("Turing KDial K30-…").
                var paired = W(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)));
                var info = paired.FirstOrDefault(d => (d.Name ?? "").IndexOf("KDial", StringComparison.OrdinalIgnoreCase) >= 0);
                if (info == null) throw new Exception("no paired 'Turing KDial' device — pair the K30 in Windows Bluetooth settings");
                dev = W(BluetoothLEDevice.FromIdAsync(info.Id));
            }
            if (dev == null) throw new Exception("K30 not paired / not found");
            dev.ConnectionStatusChanged += (d, o) => {
                Log("connection: " + d.ConnectionStatus);
                if (d.ConnectionStatus != BluetoothConnectionStatus.Connected) SetConnected(false);
            };
        }
        var sr = OpenVendorService();

        // After power-up or sleep the K30 is back on its built-in HID keys; only DigiDraw knows the command
        // that switches it to vendor mode. Let DigiDraw send it, then take over.
        for (int round = 0; round < cfg.WakeRepeat; round++) {
            if (round > 0) Thread.Sleep(700);
            if (cfg.HidWake) { try { K30Hid.Wake(Log); } catch (Exception e) { Log("hid wake: " + e.Message); } }
            if (cfg.WakeCommands.Count > 0) SendWakeCommands(sr.Services[0]);
        }
        if (WakeKick(sr.Services[0])) sr = OpenVendorService();

        var cr = W(sr.Services[0].GetCharacteristicsForUuidAsync(new Guid("0000ffe1-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached));
        if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) throw new Exception("FFE1 unavailable (" + cr.Status + ")");
        var c = cr.Characteristics[0];
        c.ValueChanged += OnNotify;
        var st = W(c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
        if (st != GattCommunicationStatus.Success) { c.ValueChanged -= OnNotify; throw new Exception("subscribe failed: " + st); }
        if (ffe1 != null) ffe1.ValueChanged -= OnNotify;
        ffe1 = c;
        Log("subscribed to FFE1");
        SetConnected(true);
    }

    GattDeviceServicesResult OpenVendorService() {
        var sr = W(dev.GetGattServicesForUuidAsync(new Guid("0000ffe0-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached));
        if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0) throw new Exception("FFE0 service unavailable (" + sr.Status + ") — device asleep?");
        return sr;
    }

    /// Writes the configured commands to FFE2 (the K30's command characteristic), the way DigiDraw does:
    /// 8-byte packets "CD xx 00 00 00 00 00 00". The K30 answers some of them with indications on FFE2,
    /// which are logged ("ffe2 <-").
    void SendWakeCommands(GattDeviceService svc) {
        var cr = W(svc.GetCharacteristicsForUuidAsync(new Guid("0000ffe2-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached));
        if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) { Log("ffe2 unavailable (" + cr.Status + ")"); return; }
        var c = cr.Characteristics[0];
        if (ffe2 != null) ffe2.ValueChanged -= OnFfe2;
        c.ValueChanged += OnFfe2;
        try { W(c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate)); } catch (Exception e) { Log("ffe2 indicate: " + e.Message); }
        ffe2 = c;
        foreach (var cmd in cfg.WakeCommands) {
            var bytes = new byte[8];
            var parts = cmd.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < 8; i++) bytes[i] = Convert.ToByte(parts[i], 16);
            var w = new DataWriter();
            w.WriteBytes(bytes);
            var st = W(c.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse));
            Log("ffe2 -> " + BitConverter.ToString(bytes).Replace("-", " ") + " (" + st + ")");
            Thread.Sleep(250);
        }
    }

    void OnFfe2(GattCharacteristic s, GattValueChangedEventArgs e) {
        var r = DataReader.FromBuffer(e.CharacteristicValue);
        var b = new byte[e.CharacteristicValue.Length];
        r.ReadBytes(b);
        Log("ffe2 <- " + BitConverter.ToString(b).Replace("-", " "));
    }

    DateTime lastKick = DateTime.MinValue;

    /// Runs DigiDraw for a few seconds so it sends the K30 its vendor-mode command, then closes it.
    /// Returns true when it ran (the caller then reopens the service it had to release).
    bool WakeKick(GattDeviceService held) {
        if (cfg.WakeKickSeconds <= 0 || string.IsNullOrWhiteSpace(cfg.DigiDrawPath)) return false;
        string exe = Environment.ExpandEnvironmentVariables(cfg.DigiDrawPath);
        if (!File.Exists(exe)) { Log("wake kick skipped: DigiDraw not found at " + exe); return false; }
        if ((DateTime.UtcNow - lastKick).TotalSeconds < 60) return false; // never loop on a flaky link
        lastKick = DateTime.UtcNow;

        UI(() => osd.Flash("Waking K30…", "Switching it to controller mode (" + cfg.WakeKickSeconds + " s)", cfg.WakeKickSeconds * 1000 + 3000, Osd.Orange));
        Log("wake kick: starting DigiDraw");
        // Release our hold on the vendor service so DigiDraw can open it.
        if (ffe1 != null) { ffe1.ValueChanged -= OnNotify; ffe1 = null; }
        try { held.Dispose(); } catch { }

        try {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            Thread.Sleep(cfg.WakeKickSeconds * 1000);
        } catch (Exception e) {
            Log("wake kick: could not start DigiDraw: " + e.Message);
        } finally {
            foreach (var name in new[] { "TuringTablet", "TuringDriver", "TabletServer" })
                foreach (var p in Process.GetProcessesByName(name)) { try { p.Kill(); } catch (Exception e) { Log("wake kick: could not close " + name + ": " + e.Message); } }
            Thread.Sleep(2000);
        }
        Log("wake kick: done");
        return true;
    }

    void SetConnected(bool on) {
        if (connected == on) return;
        connected = on;
        ui.BeginInvoke((Action)(() => {
            BuildMenu();
            osd.Flash(on ? "K30 connected" : "K30 disconnected", on ? "Dial: " + currentModes[mode].Name : "Reconnecting automatically…", 1400, on ? Osd.Green : Osd.Gray);
            if (!on) ReleaseAll();
        }));
    }

    void OnNotify(GattCharacteristic s, GattValueChangedEventArgs e) {
        var r = DataReader.FromBuffer(e.CharacteristicValue);
        var b = new byte[e.CharacteristicValue.Length];
        r.ReadBytes(b);
        if (b.Length < 14 || b[0] != 0x55 || b[1] != 0x54) return;
        int sum = 0;
        for (int i = 2; i <= 12; i++) sum += b[i];
        if ((sum & 0xFF) != b[13]) return;
        ui.BeginInvoke((Action)(() => {
            try { Handle(b); } catch (Exception ex) { Log("handle: " + ex.Message); }
        }));
    }

    static T W<T>(IAsyncOp.IAsyncOperation<T> op) {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (op.Status == IAsyncOp.AsyncStatus.Started) {
            if (DateTime.UtcNow > deadline) { op.Cancel(); throw new TimeoutException("BLE timeout"); }
            Thread.Sleep(5);
        }
        if (op.Status != IAsyncOp.AsyncStatus.Completed) throw op.ErrorCode ?? new Exception("async " + op.Status);
        return op.GetResults();
    }

    // ---------------- input handling (UI thread)

    void Handle(byte[] b) {
        if (b[2] == 0xE0) {
            int mask = b[5] | (b[6] << 8);
            int changed = mask ^ lastMask;
            lastMask = mask;
            // The key sheet closes on the next press (which still does its job, so you can look, then
            // press), but not on the release of the long press that opened it.
            if ((changed & mask) != 0) HideKeySheet();
            for (int i = 0; i < KeyNames.Length; i++) {
                int bit = 1 << i;
                if ((changed & bit) == 0) continue;
                if ((mask & bit) != 0) KeyDown(i); else KeyUp(i);
            }
        } else if (b[2] == 0x20) {
            HideKeySheet();
            bool forward = b[5] == 0x01;
            if (b[4] == 0x01) Roller(forward ? cfg.RollerUp : cfg.RollerDown);
            else Dial(forward);
        }
    }

    // ---- press gestures
    // A key with a "Kn:long" and/or "Kn:double" entry fires its plain action on release instead of on press,
    // so the release (or a second press) can decide which action it was.

    class Press {
        public bool Down, LongFired, AwaitingSecond, SecondPress;
        public readonly System.Windows.Forms.Timer LongTimer = new System.Windows.Forms.Timer();
        public readonly System.Windows.Forms.Timer DoubleTimer = new System.Windows.Forms.Timer();
    }
    readonly Dictionary<int, Press> presses = new Dictionary<int, Press>();

    /// Resolves a key's action: the foreground app's profile if it overrides this key, else the global
    /// mapping. K1 (index 0, push-to-talk) is exempt — it never consults a profile, by construction.
    string KeyAction(int i, string suffix) {
        string name = KeyNames[i] + suffix;
        if (i != 0) {
            var profile = ActiveAppProfile();
            string pa;
            if (profile != null && profile.Keys.TryGetValue(name, out pa) && !string.IsNullOrWhiteSpace(pa)) return pa.Trim();
        }
        string a;
        return cfg.Keys.TryGetValue(name, out a) && !string.IsNullOrWhiteSpace(a) ? a.Trim() : null;
    }

    AppProfile ActiveAppProfile() {
        string name = AppDetect.ForegroundProcessName();
        AppProfile p;
        return name != null && cfg.AppProfiles.TryGetValue(name, out p) ? p : null;
    }

    void KeyDown(int i) {
        string longAction = KeyAction(i, ":LONG"), doubleAction = KeyAction(i, ":DOUBLE");
        if (longAction == null && doubleAction == null) { Perform(i, KeyAction(i, ""), true); return; }
        var p = GetPress(i);
        p.Down = true; p.LongFired = false;
        if (p.AwaitingSecond) {
            p.DoubleTimer.Stop(); p.AwaitingSecond = false; p.SecondPress = true;
            Gesture(i, doubleAction, "Double press");
            return;
        }
        p.SecondPress = false;
        if (longAction != null) { p.LongTimer.Stop(); p.LongTimer.Interval = cfg.LongPressMs; p.LongTimer.Start(); }
    }

    Press GetPress(int i) {
        Press p;
        if (presses.TryGetValue(i, out p)) return p;
        p = new Press();
        var press = p;
        press.LongTimer.Tick += (s, e) => {
            press.LongTimer.Stop();
            if (press.Down) { press.LongFired = true; Gesture(i, KeyAction(i, ":LONG"), "Long press"); }
        };
        press.DoubleTimer.Tick += (s, e) => {
            press.DoubleTimer.Stop();
            if (press.AwaitingSecond) { press.AwaitingSecond = false; Perform(i, KeyAction(i, ""), false); }
        };
        presses[i] = press;
        return press;
    }

    void Gesture(int i, string action, string kind) {
        if (action == null) return;
        if (!action.Equals("showKeys", StringComparison.OrdinalIgnoreCase)) // the sheet is its own feedback
            osd.Flash(Pretty(action), kind + "  ·  " + KeyNames[i], 900, Osd.Green);
        Perform(i, action, false);
    }

    static string Pretty(string action) {
        return string.Join(", ", action.Split(',').Select(step => string.Join("+", step.Trim().Split('+')
            .Select(k => k.Length == 0 ? k : char.ToUpperInvariant(k[0]) + k.Substring(1)))));
    }

    /// Runs a key's action. allowHold: "hold …" actions keep their keys down until the key is released;
    /// gesture keys only know what they were once released, so there they are sent as a tap.
    void Perform(int i, string action, bool allowHold) {
        if (action == null) return;
        if (action.Equals("showKeys", StringComparison.OrdinalIgnoreCase)) { CancelSwitch(); ShowKeySheet(); return; }

        // Window switcher open: the dial button switches right away, any other key cancels it first.
        if (switchActive) {
            if (action.Equals("nextMode", StringComparison.OrdinalIgnoreCase) || action.Equals("prevMode", StringComparison.OrdinalIgnoreCase)) { CommitSwitch(); return; }
            CancelSwitch();
        }

        if (action.Equals("nextMode", StringComparison.OrdinalIgnoreCase) || action.Equals("prevMode", StringComparison.OrdinalIgnoreCase)) {
            // Claude asked "Switch model?" after a model dial gesture: the dial button confirms it.
            if (awaitingSwitchConfirm) {
                awaitingSwitchConfirm = false;
                string target = modelList != null && modelPending < modelList.Count ? modelList[modelPending] : "";
                uia.Post(() => {
                    bool ok = claude.ConfirmSwitch();
                    claude.RestoreComposer();
                    UI(() => osd.Flash(ok ? "Model: " + target : "Nothing to confirm", ok ? "Switched" : "The prompt was already closed", 1400, ok ? Osd.Green : Osd.Gray));
                });
                return;
            }
            // While a dial gesture is in progress, the dial button completes it instead.
            if (menuOpen || altHeld || modelActive || effortActive) { idle.Stop(); FinishDialGesture(); return; }
            RefreshDialProfile();
            int n = currentModes.Count;
            SetMode((mode + (action.Equals("nextMode", StringComparison.OrdinalIgnoreCase) ? 1 : n - 1)) % n, currentModes);
            return;
        }

        // Any other key interrupts a pending dial gesture without confirming it.
        CancelDialGesture();
        if (awaitingSwitchConfirm) {
            // e.g. Esc dismissed Claude's "Switch model?" prompt: focus lands on the model button, take it back.
            awaitingSwitchConfirm = false;
            uia.Post(() => { Thread.Sleep(150); claude.RestoreComposer(); });
        }

        if (action.Equals("claude:model", StringComparison.OrdinalIgnoreCase) || action.Equals("claude:effort", StringComparison.OrdinalIgnoreCase)) {
            string prefix = action.EndsWith("model", StringComparison.OrdinalIgnoreCase) ? "Model: " : "Effort: ";
            uia.Post(() => {
                var remembered = claude.RememberComposer();
                if (claude.OpenMenu(prefix)) claude.RefocusWhenClosed(prefix, remembered);
            });
            return;
        }

        if (action.StartsWith("hold ", StringComparison.OrdinalIgnoreCase)) {
            var vks = Output.Parse(action.Substring(5));
            if (!allowHold) { Output.Tap(vks); return; }
            Output.Down(vks);
            held[i] = vks;
            return;
        }
        Output.Run(action);
    }

    void KeyUp(int i) {
        ushort[] vks;
        if (held.TryGetValue(i, out vks)) { Output.Up(vks); held.Remove(i); }

        Press p;
        if (!presses.TryGetValue(i, out p) || !p.Down) return;
        p.Down = false;
        p.LongTimer.Stop();
        if (p.LongFired || p.SecondPress) return;
        if (KeyAction(i, ":DOUBLE") != null) {
            p.AwaitingSecond = true;
            p.DoubleTimer.Interval = cfg.DoublePressMs;
            p.DoubleTimer.Start();
        } else {
            Perform(i, KeyAction(i, ""), false);
        }
    }

    void Dial(bool cw) {
        CancelSwitch();
        RefreshDialProfile();
        var m = currentModes[mode];
        switch (m.Type) {
            case "menu":
                if (!menuOpen) {
                    Output.Run(m.Open);
                    menuOpen = true;
                } else {
                    Output.Run(cw ? m.Cw : m.Ccw);
                }
                RestartIdle(m.IdleMs);
                break;
            case "alttab":
                AltTab(cw, m.IdleMs);
                break;
            case "claude-model":
                DialModel(cw ? 1 : -1);
                RestartIdle(m.IdleMs);
                break;
            case "claude-effort":
                DialEffort(cw ? 1 : -1, m.Max);
                RestartIdle(m.IdleMs);
                break;
            default:
                Output.Run(cw ? m.Cw : m.Ccw);
                osd.Flash(m.Name, cw ? "▲" : "▼", 900, Osd.Teal);
                break;
        }
    }

    /// Re-checks which app has focus and switches currentModes/mode to that app's profile's dial modes
    /// if it has any (else the global list). Called only at the start of a LIVE dial interaction — a
    /// turn, or the dial button's nextMode/prevMode — never from something that merely finishes or
    /// displays an already-decided gesture, so switching focus mid-gesture never yanks it out from under you.
    void RefreshDialProfile() {
        string name = AppDetect.ForegroundProcessName();
        AppProfile profile = null;
        if (name != null) cfg.AppProfiles.TryGetValue(name, out profile);
        bool hasOwn = profile != null && profile.DialModes.Count > 0;
        var modes = hasOwn ? profile.DialModes : cfg.DialModes;
        string key = hasOwn ? name.ToLowerInvariant() : null;
        if (key != activeDialProfileKey) {
            activeDialProfileKey = key;
            currentModes = modes;
            CancelDialGesture();
            mode = 0;
            osd.Flash((hasOwn ? profile.Label : "Default") + " — " + modes[0].Name, "Dial", 1200, Osd.Teal);
        } else if (mode >= modes.Count) {
            mode = 0;
        }
    }

    void Roller(string action) {
        if (action == null) return;
        string a = action.Trim().ToLowerInvariant();
        if (a == "switch:next" || a == "switch:prev") SwitchStep(a == "switch:next");
        else if (a == "alttab:next" || a == "alttab:prev") AltTab(a == "alttab:next", cfg.RollerIdleMs);
        else Output.Run(action);
    }

    // ---- key sheet ("showKeys"): what every control does in the app you're in

    void ShowKeySheet() {
        var profile = ActiveAppProfile();
        var list = new List<SheetEntry>();
        for (int i = 0; i < KeyNames.Length; i++) list.Add(KeyEntry(i, profile));

        var modes = profile != null && profile.DialModes.Count > 0 ? profile.DialModes : cfg.DialModes;
        int active = modes == currentModes && mode < modes.Count ? mode : 0;
        list.Add(new SheetEntry {
            Id = "DialTurn", Tag = "TURN", Title = modes[active].Name,
            Detail = modes.Count > 1 ? "modes: " + string.Join(" · ", modes.Select(m => m.Name)) : "the dial's only mode"
        });
        string up = (cfg.RollerUp ?? "").Trim(), down = (cfg.RollerDown ?? "").Trim();
        bool switcherRoller = up.StartsWith("switch:", StringComparison.OrdinalIgnoreCase);
        list.Add(new SheetEntry {
            Id = "Roller", Tag = "ROLL",
            Title = switcherRoller ? "Switch windows" : up.StartsWith("alttab:", StringComparison.OrdinalIgnoreCase) ? "Alt-Tab" : Friendly(up) ?? "Nothing",
            Detail = switcherRoller ? "roll to pick, stop to switch" : "up: " + (Friendly(up) ?? "—") + " · down: " + (Friendly(down) ?? "—")
        });

        string title = profile != null ? profile.Label : "All apps";
        string subtitle = "Press any key or turn the dial to close";
        var screens = Screen.AllScreens;
        while (sheets.Count < screens.Length) sheets.Add(new KeySheet());
        for (int s = 0; s < screens.Length; s++) sheets[s].ShowSheet(title, subtitle, list, screens[s]);
        for (int s = screens.Length; s < sheets.Count; s++) sheets[s].Hide();
        sheetTimer.Stop();
        sheetTimer.Interval = 12000;
        sheetTimer.Start();
    }

    void HideKeySheet() {
        sheetTimer.Stop();
        foreach (var s in sheets) if (s.Visible) s.Hide();
    }

    SheetEntry KeyEntry(int i, AppProfile profile) {
        string press = KeyAction(i, ""), longAction = KeyAction(i, ":LONG"), doubleAction = KeyAction(i, ":DOUBLE");
        string name = LabelFor(i, "", profile);
        var detail = new List<string>();
        if (name != null && press != null && Friendly(press) != name) detail.Add(Friendly(press)); // the shortcut, under its name
        if (longAction != null) detail.Add("hold: " + (LabelFor(i, ":LONG", profile) ?? Friendly(longAction)));
        if (doubleAction != null) detail.Add("double: " + (LabelFor(i, ":DOUBLE", profile) ?? Friendly(doubleAction)));
        return new SheetEntry {
            Id = i == 11 ? "Dial" : KeyNames[i], Tag = i == 11 ? "PRESS" : KeyNames[i],
            Title = name ?? Friendly(press) ?? "Nothing", Detail = string.Join("  ·  ", detail)
        };
    }

    /// A key's name in words: the app profile's label if it has one; none if the profile changes the
    /// key without naming it (the global name would describe the wrong action); else the global label.
    string LabelFor(int i, string suffix, AppProfile profile) {
        string key = KeyNames[i] + suffix.ToUpperInvariant(), label;
        if (i != 0 && profile != null) {
            if (profile.Labels.TryGetValue(key, out label)) return label;
            if (profile.Keys.ContainsKey(key)) return null;
        }
        return cfg.Labels.TryGetValue(key, out label) ? label : null;
    }

    /// An action as a person would say it: "Ctrl+Shift+T", "Hold Ctrl+Space", "Scroll down"…
    static string Friendly(string action) {
        if (string.IsNullOrWhiteSpace(action)) return null;
        string a = action.Trim();
        switch (a.ToLowerInvariant()) {
            case "nextmode": return "Next dial mode";
            case "prevmode": return "Previous dial mode";
            case "showkeys": return "This key sheet";
            case "claude:model": return "Claude's model menu";
            case "claude:effort": return "Claude's effort panel";
            case "wheel+1": return "Scroll up";
            case "wheel-1": return "Scroll down";
        }
        if (a.StartsWith("hold ", StringComparison.OrdinalIgnoreCase)) return "Hold " + Pretty(a.Substring(5).Trim());
        return Pretty(a);
    }

    bool IncludeInSwitcher(string process) {
        bool listed = cfg.SwitcherApps.Contains(process);
        return cfg.SwitcherMode == "allow" ? listed : !listed;
    }

    /// One roller click: opens the list on the first click (starting from the window you're in, like
    /// Alt-Tab), moves the highlight on the next ones, and (re)arms the commit timer.
    void SwitchStep(bool next) {
        if (!switchActive) {
            IntPtr fg = WindowList.Foreground();
            var list = WindowList.Snapshot(IncludeInSwitcher);
            // The list is kept in on-screen order, and rolling moves the highlight the way you roll
            // (switch:next right, switch:prev left). historyOnLeft lays it out as a timeline (see
            // SwitchHistory): the past on the left, the present on the right, with ends you can't roll
            // past. Otherwise it's Alt-Tab's order, most recent first, wrapping around.
            if (cfg.SwitcherHistoryOnLeft) list = history.Order(list);
            int n = list.Count;
            int cur = list.FindIndex(t => t.Hwnd == fg);
            if (n == 0 || (n == 1 && cur == 0)) {
                osd.Flash("Nothing to switch to", cfg.SwitcherMode == "allow" ? "No allowed app has another window open" : "No other window", 1200, Osd.Gray);
                return;
            }
            switchList = list;
            switchCurrent = cur;
            // Not in any listed window (the desktop, a hidden app): start from the present end.
            switchIndex = cur < 0 ? (cfg.SwitcherHistoryOnLeft || !next ? n - 1 : 0) : Step(cur, next);
            switchActive = true;
            // Shown on every monitor, so it's in front of you wherever you're looking.
            var screens = Screen.AllScreens;
            while (switchers.Count < screens.Length) switchers.Add(new SwitcherOsd());
            for (int s = 0; s < screens.Length; s++) switchers[s].ShowList(switchList, switchIndex, cur, screens[s]);
            for (int s = screens.Length; s < switchers.Count; s++) switchers[s].Hide();
        } else {
            switchIndex = Step(switchIndex, next);
            foreach (var sw in switchers) if (sw.Visible) sw.Select(switchIndex);
        }
        switchTimer.Stop();
        switchTimer.Interval = Math.Max(150, cfg.SwitcherCommitMs);
        switchTimer.Start();
    }

    /// One roller click along the list: a timeline stops at its ends (there's nothing past the present,
    /// and wrapping round to the oldest window would break the "things stay where you left them" feel);
    /// Alt-Tab's order wraps around.
    int Step(int from, bool next) {
        int n = switchList.Count, to = from + (next ? 1 : -1);
        return cfg.SwitcherHistoryOnLeft ? Math.Max(0, Math.Min(n - 1, to)) : (to % n + n) % n;
    }

    void CommitSwitch() {
        if (!switchActive) return;
        switchActive = false;
        switchTimer.Stop();
        HideSwitchers();
        if (switchIndex == switchCurrent) return; // rolled back to where you started: stay
        var t = switchList[switchIndex];
        history.SwitchingTo(t.Hwnd);
        if (!WindowList.Activate(t.Hwnd)) {
            Log("switch: could not activate " + t.Process + " '" + t.Title + "'");
            osd.Flash("Couldn't switch to " + t.Label, "Windows blocked the focus change (an admin window in front?)", 2500, Osd.Red);
        }
    }

    void CancelSwitch() {
        if (!switchActive) return;
        switchActive = false;
        switchTimer.Stop();
        HideSwitchers();
    }

    void HideSwitchers() { foreach (var sw in switchers) sw.Hide(); }

    /// Holds Alt and steps through the Alt-Tab switcher; Alt is released once input rests for idleMs.
    void AltTab(bool next, int idleMs) {
        if (!altHeld) { Output.Down(new ushort[] { 0xA4 }); altHeld = true; }
        if (next) Output.Tap(new ushort[] { 0x09 }); else Output.Tap(new ushort[] { 0xA0, 0x09 });
        RestartIdle(idleMs);
    }

    void DialModel(int d) {
        awaitingSwitchConfirm = false;
        if (!modelActive) {
            modelActive = true; modelLoaded = false; modelDelta = 0;
            uia.Post(() => {
                claude.RememberComposer();
                string cur = claude.CurrentModel();
                List<string> list = null;
                if (cur != null) {
                    list = claude.Models;
                    if (list == null || !list.Contains(cur)) list = claude.Models = claude.ReadModels();
                }
                UI(() => {
                    if (!modelActive) return;
                    if (cur == null || list == null || !list.Contains(cur)) {
                        modelActive = false;
                        osd.Flash("Model: Claude not found", "Open a session in the Claude window", 2000, Osd.Red);
                        return;
                    }
                    modelList = list;
                    modelCurrent = list.IndexOf(cur);
                    modelPending = Clamp(modelCurrent + modelDelta, 0, list.Count - 1);
                    modelLoaded = true;
                    ShowModelOsd();
                });
            });
        }
        if (modelLoaded) { modelPending = Clamp(modelPending + d, 0, modelList.Count - 1); ShowModelOsd(); }
        else modelDelta += d;
    }

    void ShowModelOsd() {
        string pick = modelList[modelPending];
        string title = modelPending == modelCurrent ? "Model: " + pick : "Model → " + pick;
        osd.Flash(title, string.Join("  ·  ", modelList.Select((n, i) => i == modelPending ? "[" + n + "]" : n)), 4000, Osd.Blue);
    }

    void ApplyModel() {
        modelActive = false;
        if (!modelLoaded) return;
        if (modelPending == modelCurrent) {
            osd.Flash("Model: " + modelList[modelCurrent], "Unchanged", 1000, Osd.Blue);
            uia.Post(() => claude.RestoreComposer());
            return;
        }
        string target = modelList[modelPending];
        osd.Flash("Model → " + target, "Switching…", 3000, Osd.Blue);
        uia.Post(() => {
            string r = claude.SelectModel(target);
            if (r != "confirm") claude.RestoreComposer();
            else claude.RefocusWhenClosed("Model: ", claude.Composer); // however the prompt gets answered
            UI(() => {
                if (r == "confirm") {
                    awaitingSwitchConfirm = true;
                    osd.Flash("Switch to " + target + "?", "Press the dial to confirm  ·  Esc (K3/K4) cancels", 15000, Osd.Orange);
                } else if (r == "ok") {
                    osd.Flash("Model: " + target, "Switched", 1400, Osd.Green);
                } else {
                    osd.Flash("Model switch failed", r, 2500, Osd.Red);
                }
            });
        });
    }

    void DialEffort(int d, int max) {
        if (!effortActive) {
            effortActive = true;
            uia.Post(() => {
                claude.RememberComposer();
                int v = claude.EffortBegin();
                if (v < 0) {
                    UI(() => { effortActive = false; osd.Flash("Effort: Claude not found", "Open a session in the Claude window", 2000, Osd.Red); });
                    return;
                }
                v = claude.EffortStep(d, max);
                int shown = v;
                UI(() => ShowEffortOsd(shown, max));
            });
            return;
        }
        uia.Post(() => {
            int v = claude.EffortStep(d, Math.Max(max, 0));
            if (v >= 0) UI(() => ShowEffortOsd(v, max));
        });
    }

    void ShowEffortOsd(int v, int max) {
        var labels = ClaudeUi.EffortLabels;
        string name = v >= 0 && v < labels.Length ? labels[v] : v.ToString();
        // Claude's own effort slider is on screen while turning, so the pop-up doesn't repeat the level list.
        osd.Flash("Effort: " + name, "Turn to adjust", 4000, Osd.Purple);
    }

    void EndEffort() {
        if (!effortActive) return;
        effortActive = false;
        uia.Post(() => { claude.EffortEnd(); claude.RestoreComposer(); });
        osd.Flash(osdTitleOr("Effort"), "Applied", 1200, Osd.Green);
    }

    string osdTitleOr(string fallback) { return osd.Title ?? fallback; }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    void RestartIdle(int ms) { idle.Stop(); idle.Interval = Math.Max(150, ms); idle.Start(); }

    void FinishDialGesture() {
        if (menuOpen) { menuOpen = false; Output.Run(currentModes[mode].Confirm); }
        if (altHeld) { altHeld = false; Output.Up(new ushort[] { 0xA4 }); }
        if (modelActive) ApplyModel();
        if (effortActive) EndEffort();
    }

    void CancelDialGesture() {
        idle.Stop();
        menuOpen = false;
        if (altHeld) { altHeld = false; Output.Up(new ushort[] { 0xA4 }); }
        if (modelActive) { modelActive = false; uia.Post(() => claude.RestoreComposer()); }
        if (effortActive) { effortActive = false; uia.Post(() => { claude.EffortEnd(); claude.RestoreComposer(); }); }
    }

    void SetMode(int i, List<DialMode> modes) {
        CancelDialGesture();
        currentModes = modes;
        mode = i;
        BuildMenu();
        ShowMode();
    }

    void ShowMode() {
        var names = currentModes.Select((d, idx) => idx == mode ? "● " + d.Name : d.Name);
        osd.Flash("Dial: " + currentModes[mode].Name, string.Join("   ·   ", names), 1600, Osd.Teal);
    }

    void ReleaseAll() {
        foreach (var vks in held.Values) Output.Up(vks);
        held.Clear();
        lastMask = 0;
        CancelDialGesture();
        CancelSwitch();
    }

    void Quit() {
        ReleaseAll();
        try { if (ffe1 != null) ffe1.ValueChanged -= OnNotify; if (dev != null) dev.Dispose(); } catch { }
        tray.Visible = false;
        Log("exit");
        ExitThread();
        Environment.Exit(0);
    }

    static Icon MakeIcon() {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(Color.FromArgb(232, 93, 38))) g.FillEllipse(br, 1, 1, 30, 30);
            using (var ring = new Pen(Color.White, 3f)) g.DrawEllipse(ring, 8, 8, 16, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static void Log(string s) {
        try {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + s + Environment.NewLine);
        } catch { }
    }
}
