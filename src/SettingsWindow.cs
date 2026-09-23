// The settings window: a WPF window using WPF's built-in Windows 11 (Fluent) theme, so it looks like
// the Windows Settings app, follows light/dark mode, and needs no extra dependency. It edits
// k30-config.json in place: it loads the file as a JSON tree, changes only what it shows, and writes
// the tree back, so anything it doesn't show (wake commands, _help notes, a menu mode's open/confirm
// keys) survives a save.
#pragma warning disable WPF0001 // Window.ThemeMode (the Fluent theme) is still flagged experimental in .NET 10

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

/// An app with an open window when the settings were opened (collected on the tray app's thread).
class RunningApp {
    public string Process, Label;
    public System.Drawing.Icon Icon;
}

/// Runs the settings window on its own STA thread with a WPF dispatcher, so the tray app's WinForms
/// loop (and with it the K30's input handling) never waits on it.
static class SettingsHost {
    static Dispatcher dispatcher;
    static SettingsWindow window;

    public static void Open(string configPath, List<RunningApp> running, System.Drawing.Rectangle workArea, Action saved) {
        if (dispatcher == null) {
            using (var ready = new ManualResetEventSlim()) {
                var t = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Name = "settings";
                t.Start();
                ready.Wait();
            }
        }
        dispatcher.BeginInvoke(new Action(() => {
            try {
                if (window == null) {
                    window = new SettingsWindow(configPath, running, workArea, saved);
                    window.Closed += (s, e) => window = null;
                    window.Show();
                }
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            } catch (Exception e) {
                window = null;
                MessageBox.Show("Couldn't open the settings: " + e.Message, "K30 Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
    }
}

/// One column of the "Buttons & dial" page: the defaults, or one app's overrides.
class ProfileModel {
    public string Process;     // null for the defaults ("All apps")
    public string Label;
    public JsonObject Node;    // the profile's JSON as loaded, so properties this window doesn't show survive
    public readonly Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // names for the key sheet
    public readonly List<JsonObject> Modes = new List<JsonObject>();
}

/// A text box for an action ("ctrl+shift+t", "hold ctrl+space"…) with a grey placeholder and a record
/// button: click it, press a shortcut, and the field is filled in with the config's own key names.
class ShortcutField : Grid {
    public static bool AnyRecording;
    readonly TextBox box = new TextBox();
    readonly TextBlock hint = new TextBlock();
    readonly Button rec = new Button();
    readonly Func<string, string> check;
    readonly string placeholder;
    bool recording, quiet;
    string before;
    public event Action<string> Changed;

    public string Value { get { return box.Text.Trim(); } }
    public string AccessibleName { set { AutomationProperties.SetName(box, value); AutomationProperties.SetName(rec, "Record " + value); } }
    public string Problem { get { return check(Value); } }

    public void FocusAndSelect() { box.Focus(); box.SelectAll(); }

    public ShortcutField(string value, string placeholder, Func<string, string> check) {
        this.placeholder = placeholder ?? "";
        this.check = check;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Text = value ?? "";
        box.MinWidth = 60;
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(11, 0, 8, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        rec.Content = SettingsWindow.Glyph(""); // keyboard
        rec.Margin = new Thickness(4, 0, 0, 0);
        rec.Padding = new Thickness(8, 4, 8, 4);
        rec.ToolTip = "Record a shortcut: click, then press the keys";
        rec.Focusable = false;
        Grid.SetColumn(rec, 1);
        Children.Add(box);
        Children.Add(hint);
        Children.Add(rec);
        UpdateHint();
        ShowProblem();

        box.TextChanged += (s, e) => { UpdateHint(); ShowProblem(); if (!recording && !quiet && Changed != null) Changed(Value); };
        rec.Click += (s, e) => StartRecording();
        box.PreviewKeyDown += OnKey;
        box.LostKeyboardFocus += (s, e) => StopRecording(true);
    }

    void UpdateHint() {
        hint.Text = recording ? "Press a shortcut…" : placeholder;
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowProblem() {
        string p = recording ? null : Problem;
        if (p == null) { box.ClearValue(Control.ForegroundProperty); box.ToolTip = null; }
        else { box.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x3B)); box.ToolTip = p; }
    }

    void StartRecording() {
        if (recording) { StopRecording(true); return; }
        before = box.Text;
        recording = AnyRecording = true;
        box.Text = "";
        UpdateHint();
        box.Focus();
    }

    void StopRecording(bool restore) {
        if (!recording) return;
        recording = AnyRecording = false;
        if (restore && box.Text.Length == 0) { quiet = true; box.Text = before; quiet = false; }
        UpdateHint();
        ShowProblem();
    }

    void OnKey(object sender, KeyEventArgs e) {
        if (!recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed || key == Key.DeadCharProcessed) return;
        var mods = new List<string>();
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods.Add("ctrl");
        if ((m & ModifierKeys.Shift) != 0) mods.Add("shift");
        if ((m & ModifierKeys.Alt) != 0) mods.Add("alt");
        if ((m & ModifierKeys.Windows) != 0 || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods.Add("win");
        bool modifierOnly = key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift ||
                            key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin;
        if (modifierOnly) { hint.Text = string.Join("+", mods) + "+…"; return; }
        string name = Output.NameFor((ushort)KeyInterop.VirtualKeyFromKey(key));
        if (name == null) { hint.Text = "That key can't be sent — try another"; return; }
        mods.Add(name);
        recording = AnyRecording = false;
        box.Text = string.Join("+", mods);
        box.CaretIndex = box.Text.Length;
        UpdateHint();
        ShowProblem();
    }
}

class SettingsWindow : Window {
    static readonly string[] KeyOrder = { "K1", "K2", "K3", "K4", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "Dial" };
    static readonly string[] Suffixes = { "", ":long", ":double" };
    static readonly string[] SuffixNames = { "Press", "Long press", "Double press" };
    static readonly string[] ModeTypeIds = { "keys", "claude-model", "claude-effort", "alttab", "menu" };
    static readonly string[] ModeTypeNames = { "Send keys", "Claude model", "Claude effort", "Alt-Tab", "Menu" };
    static readonly string[] EffortSteps = { "Low", "Medium", "High", "Extra", "Max", "Ultracode" };
    static readonly JsonDocumentOptions ReadOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    readonly string path;
    readonly Action saved;
    readonly System.Drawing.Rectangle workArea;
    readonly JsonObject root;
    readonly List<ProfileModel> profiles = new List<ProfileModel>();
    readonly Dictionary<string, RunningApp> running = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
    string rollerUp, rollerDown;
    bool dirty, building, centred;

    ListBox profileList;
    ScrollViewer editorHost;
    DeviceView device;
    readonly Dictionary<string, KeyRow> keyRows = new Dictionary<string, KeyRow>(StringComparer.OrdinalIgnoreCase);
    string hoverKey, deviceHover, focusKey;
    SolidColorBrush rowHighlight;
    ComboBox addAppBox;
    Button removeAppButton;
    ListBox shownList, hiddenList;
    RadioButton unsortedShow, unsortedHide, historyOnLeft;
    Slider commitSlider;
    TextBox addressBox, longPressBox, doublePressBox, addNameBox;
    TextBlock status;

    public SettingsWindow(string path, List<RunningApp> apps, System.Drawing.Rectangle workArea, Action saved) {
        this.path = path;
        this.saved = saved;
        this.workArea = workArea;
        root = JsonNode.Parse(File.ReadAllText(path), null, ReadOptions) as JsonObject;
        if (root == null) throw new Exception("k30-config.json is not a JSON object");
        foreach (var a in apps) { running[a.Process] = a; icons[a.Process] = ToImage(a.Icon); }
        LoadModel();

        Title = "K30 Controller settings";
        ThemeMode = ThemeMode.System;
        Width = 1320; Height = 760; MinWidth = 1040; MinHeight = 560;
        try { Icon = ToImage(System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)); } catch { }

        building = true;
        Content = BuildLayout();
        building = false;

        SourceInitialized += (s, e) => Centre();
        DpiChanged += (s, e) => { if (!centred) Dispatcher.BeginInvoke(new Action(Centre)); };
        ContentRendered += (s, e) => centred = true;
        PreviewKeyDown += (s, e) => {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && !ShortcutField.AnyRecording) { Save(); e.Handled = true; }
        };
        Closing += (s, e) => {
            if (!dirty) return;
            var r = MessageBox.Show(this, "Save your changes?", "K30 Controller", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) e.Cancel = true;
        };
    }

    /// Centres the window on the monitor the tray icon was clicked on (in pixels, so mixed DPIs work),
    /// shrinking it to fit a small screen.
    void Centre() {
        var h = new WindowInteropHelper(this).Handle;
        RECT r;
        if (h == IntPtr.Zero || !GetWindowRect(h, out r)) return;
        int w = Math.Min(r.Right - r.Left, workArea.Width - 24), ht = Math.Min(r.Bottom - r.Top, workArea.Height - 24);
        SetWindowPos(h, IntPtr.Zero, workArea.X + (workArea.Width - w) / 2, workArea.Y + (workArea.Height - ht) / 2, w, ht, 0x4 | 0x10 /* NOZORDER | NOACTIVATE */);
    }

    // ---------------------------------------------------------------- model

    void LoadModel() {
        var def = new ProfileModel { Process = null, Label = "All apps", Node = root };
        ReadKeys(root["keys"] as JsonObject, def.Keys);
        ReadKeys(root["labels"] as JsonObject, def.Labels);
        ReadModes(root["dialModes"] as JsonArray, def.Modes);
        profiles.Add(def);
        var aps = root["appProfiles"] as JsonObject;
        if (aps != null)
            foreach (var kv in aps) {
                var node = kv.Value as JsonObject ?? new JsonObject();
                var p = new ProfileModel { Process = kv.Key, Label = Str(node, "label") ?? kv.Key, Node = node };
                ReadKeys(node["keys"] as JsonObject, p.Keys);
                ReadKeys(node["labels"] as JsonObject, p.Labels);
                ReadModes(node["dialModes"] as JsonArray, p.Modes);
                profiles.Add(p);
            }
        var roller = root["roller"] as JsonObject;
        rollerUp = Str(roller, "up");
        rollerDown = Str(roller, "down");
    }

    static void ReadKeys(JsonObject o, Dictionary<string, string> into) {
        if (o == null) return;
        foreach (var kv in o) {
            string v = AsString(kv.Value);
            if (v != null) into[NormaliseKey(kv.Key)] = v;
        }
    }

    static void ReadModes(JsonArray a, List<JsonObject> into) {
        if (a == null) return;
        foreach (var n in a) { var o = n as JsonObject; if (o != null) into.Add((JsonObject)o.DeepClone()); }
    }

    /// "k2:LONG" → "K2:long", "DIAL" → "Dial": one spelling per key, whatever the file used.
    static string NormaliseKey(string k) {
        int c = k.IndexOf(':');
        string b = c < 0 ? k : k.Substring(0, c), s = c < 0 ? "" : k.Substring(c).ToLowerInvariant();
        return (b.Equals("dial", StringComparison.OrdinalIgnoreCase) ? "Dial" : b.ToUpperInvariant()) + s;
    }

    static string AsString(JsonNode n) {
        var v = n as JsonValue;
        string s;
        return v != null && v.TryGetValue(out s) ? s : null;
    }

    static string Str(JsonObject o, string key) { return o == null ? null : AsString(o[key]); }

    static int? Int(JsonObject o, string key) {
        var v = o == null ? null : o[key] as JsonValue;
        int i;
        return v != null && v.TryGetValue(out i) ? i : (int?)null;
    }

    ProfileModel Defaults { get { return profiles[0]; } }

    void Dirty() {
        if (building) return;
        dirty = true;
        status.Text = "Unsaved changes";
    }

    /// Why an action string won't work, or null if it's fine (empty means "nothing" and is fine too).
    static string Problem(string action, bool roller) {
        if (string.IsNullOrWhiteSpace(action)) return null;
        string a = action.Trim();
        if (roller && (a.StartsWith("switch:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("alttab:", StringComparison.OrdinalIgnoreCase))) {
            string dir = a.Substring(a.IndexOf(':') + 1).Trim().ToLowerInvariant();
            return dir == "next" || dir == "prev" ? null : "Use " + a.Substring(0, a.IndexOf(':')) + ":next or :prev";
        }
        try { K30App.Validate(a); return null; } catch (Exception e) { return e.Message; }
    }

    // ---------------------------------------------------------------- layout helpers

    internal static TextBlock Glyph(string g) {
        return new TextBlock { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };
    }

    static TextBlock PageTitle(string s) {
        return new TextBlock { Text = s, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
    }

    static TextBlock Section(string s) {
        return new TextBlock { Text = s, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 8) };
    }

    static TextBlock Caption(string s, double top = 0) {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    static TextBlock Header(string s) {
        var t = new TextBlock { Text = s, Margin = new Thickness(4, 0, 4, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    static void Place(Grid g, UIElement e, int row, int col, int colSpan = 1) {
        Grid.SetRow(e, row); Grid.SetColumn(e, col);
        if (colSpan > 1) Grid.SetColumnSpan(e, colSpan);
        g.Children.Add(e);
    }

    static Grid Columns(params GridLength[] widths) {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    static GridLength Px(double v) { return new GridLength(v); }
    static GridLength Star(double v = 1) { return new GridLength(v, GridUnitType.Star); }
    static readonly GridLength Auto = GridLength.Auto;

    static int AddRow(Grid g) { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); return g.RowDefinitions.Count - 1; }

    static Grid FormRow(string label, UIElement control, string after = null) {
        var g = Columns(Px(200), Auto, Star());
        g.Margin = new Thickness(0, 4, 0, 4);
        Place(g, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Place(g, control, 0, 1);
        if (after != null) {
            var c = Caption(after);
            c.VerticalAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(12, 0, 0, 0);
            Place(g, c, 0, 2);
        }
        return g;
    }

    static ImageSource ToImage(System.Drawing.Icon icon) {
        if (icon == null) return null;
        try {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        } catch { return null; }
    }

    UIElement BuildLayout() {
        var tabs = new TabControl { Margin = new Thickness(20, 12, 20, 0) };
        tabs.Items.Add(new TabItem { Header = "Buttons & dial", Content = BuildKeysTab() });
        tabs.Items.Add(new TabItem { Header = "Window switcher", Content = BuildSwitcherTab() });
        tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneralTab() });

        var footer = new DockPanel { Margin = new Thickness(20, 12, 20, 16) };
        var save = new Button { Content = "Save", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Save and use these settings now (Ctrl+S)" };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        save.Click += (s, e) => Save();
        var close = new Button { Content = "Close", MinWidth = 110 };
        close.Click += (s, e) => Close();
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(close);
        status = Caption("");
        status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(status);

        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(tabs);
        return dock;
    }

    // ---------------------------------------------------------------- Buttons & dial

    UIElement BuildKeysTab() {
        var grid = Columns(Px(230), Px(240), Star());
        grid.Margin = new Thickness(0, 16, 0, 0);
        rowHighlight = new SolidColorBrush(Color.FromArgb(0x38, AccentColor().R, AccentColor().G, AccentColor().B));
        rowHighlight.Freeze();

        var left = new DockPanel { Margin = new Thickness(0, 0, 20, 0) };
        var add = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        add.Children.Add(Caption("Add an app: pick an open one, or type its process name (Task Manager › Details, without .exe)."));
        addAppBox = new ComboBox { IsEditable = true, Margin = new Thickness(0, 8, 0, 0) };
        FillAddAppBox();
        add.Children.Add(addAppBox);
        var addButton = new Button { Content = "Add app", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        addButton.Click += (s, e) => AddProfile();
        add.Children.Add(addButton);
        removeAppButton = new Button { Content = "Remove app", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        removeAppButton.Click += (s, e) => RemoveProfile();
        add.Children.Add(removeAppButton);
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);

        profileList = new ListBox();
        foreach (var p in profiles) profileList.Items.Add(ProfileItem(p));
        profileList.SelectionChanged += (s, e) => ShowProfile();
        left.Children.Add(profileList);
        Place(grid, left, 0, 0);

        editorHost = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Place(grid, editorHost, 0, 2);

        // The K30 itself, between the app list and the fields: hovering a field lights up its key,
        // clicking a key jumps to its field.
        var middle = new DockPanel { Margin = new Thickness(0, 0, 24, 0) };
        var hint = Caption("Point at a field to find its key. Click a key to jump to its shortcut.");
        hint.TextAlignment = TextAlignment.Center;
        hint.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(hint, Dock.Bottom);
        middle.Children.Add(hint);
        device = new DeviceView(AccentColor()) { VerticalAlignment = VerticalAlignment.Top };
        device.PartHovered += id => { deviceHover = id; UpdateHighlight(); };
        device.PartClicked += JumpTo;
        middle.Children.Add(device);
        Place(grid, middle, 0, 1);

        profileList.SelectedIndex = 0;
        return grid;
    }

    /// Windows' accent colour, in its light variant (the K30 drawing is dark in both themes).
    static Color AccentColor() {
        try {
            var c = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return Color.FromRgb(c.R, c.G, c.B);
        } catch { return Color.FromRgb(0x99, 0xEB, 0xFF); }
    }

    /// One control's place on the Buttons & dial page: its row (highlighted with the key), the field
    /// a click on the key focuses, and what to scroll into view.
    class KeyRow { public Border Back; public ShortcutField Field; public FrameworkElement Anchor; }

    // Which K30 control is lit: the one under the mouse (a field or the drawing) wins over the focused field.
    void UpdateHighlight() {
        string id = hoverKey ?? deviceHover ?? focusKey;
        device.Highlight(id);
        foreach (var kv in keyRows)
            if (kv.Value.Back != null)
                kv.Value.Back.Background = string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase) ? rowHighlight : Brushes.Transparent;
    }

    void JumpTo(string id) {
        KeyRow row;
        if (!keyRows.TryGetValue(id, out row)) return;
        row.Anchor.BringIntoView();
        if (row.Field != null && row.Field.IsEnabled) row.Field.FocusAndSelect();
        focusKey = id;
        UpdateHighlight();
    }

    /// Links a row's elements to a K30 control: pointing at any of them lights the control up, and
    /// focus inside a field keeps it lit.
    void Link(string id, KeyRow row, params FrameworkElement[] hoverables) {
        keyRows[id] = row;
        foreach (var e in hoverables) {
            e.MouseEnter += (s, a) => { hoverKey = id; UpdateHighlight(); };
            e.MouseLeave += (s, a) => { if (hoverKey == id) { hoverKey = null; UpdateHighlight(); } };
            e.IsKeyboardFocusWithinChanged += (s, a) => {
                if ((bool)a.NewValue) focusKey = id;
                else if (focusKey == id) focusKey = null;
                UpdateHighlight();
            };
        }
    }

    /// A plain text box with a grey placeholder shown while it's empty.
    static Grid PlaceholderBox(string value, string placeholder, Action<string> changed) {
        var box = new TextBox { Text = value ?? "", MinWidth = 60 };
        var hint = new TextBlock { Text = placeholder ?? "", IsHitTestVisible = false, Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (s, e) => {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            changed(box.Text.Trim());
        };
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(hint);
        return g;
    }

    static Border RowBack(Grid g, int row, int span) {
        var b = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Margin = new Thickness(-6, 0, -2, 0) };
        Grid.SetRow(b, row);
        Grid.SetColumnSpan(b, span);
        g.Children.Insert(0, b);
        return b;
    }

    ListBoxItem ProfileItem(ProfileModel p) {
        var item = new ListBoxItem { Tag = p, Content = ProfileItemContent(p) };
        AutomationProperties.SetName(item, p.Label);
        return item;
    }

    UIElement ProfileItemContent(ProfileModel p) {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        ImageSource icon = null;
        if (p.Process != null) icons.TryGetValue(p.Process, out icon);
        var img = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), Source = icon, VerticalAlignment = VerticalAlignment.Center };
        if (p.Process == null) sp.Children.Add(new Border { Width = 20, Margin = img.Margin, Child = Glyph("") }); // "all apps" glyph
        else sp.Children.Add(img);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = p.Label, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Caption(p.Process == null ? "Default for every app" : p.Process));
        sp.Children.Add(text);
        return sp;
    }

    void FillAddAppBox() {
        addAppBox.Items.Clear();
        foreach (var a in running.Values.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase))
            if (!profiles.Any(p => a.Process.Equals(p.Process, StringComparison.OrdinalIgnoreCase)) &&
                !a.Process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                addAppBox.Items.Add(new ComboBoxItem { Content = a.Process, ToolTip = a.Label });
    }

    ProfileModel SelectedProfile {
        get { var i = profileList.SelectedItem as ListBoxItem; return i == null ? null : (ProfileModel)i.Tag; }
    }

    void AddProfile() {
        string name = (addAppBox.Text ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.Length == 0) { status.Text = "Pick or type an app first."; return; }
        var existing = profiles.FirstOrDefault(p => name.Equals(p.Process, StringComparison.OrdinalIgnoreCase));
        if (existing == null) {
            RunningApp r;
            existing = new ProfileModel { Process = name, Label = running.TryGetValue(name, out r) ? r.Label : name, Node = new JsonObject() };
            profiles.Add(existing);
            profileList.Items.Add(ProfileItem(existing));
            Dirty();
        }
        addAppBox.Text = "";
        FillAddAppBox();
        profileList.SelectedIndex = profiles.IndexOf(existing);
    }

    void RemoveProfile() {
        var p = SelectedProfile;
        if (p == null || p.Process == null) return;
        int i = profiles.IndexOf(p);
        profiles.RemoveAt(i);
        profileList.Items.RemoveAt(i);
        profileList.SelectedIndex = Math.Min(i, profiles.Count - 1);
        FillAddAppBox();
        Dirty();
    }

    void ShowProfile() {
        var p = SelectedProfile;
        removeAppButton.IsEnabled = p != null && p.Process != null;
        if (p == null) { editorHost.Content = null; return; }
        bool wasBuilding = building;
        building = true;
        bool app = p.Process != null;
        var def = Defaults;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 24) };
        keyRows.Clear();
        hoverKey = focusKey = null;

        panel.Children.Add(PageTitle(app ? p.Label : "All apps"));
        panel.Children.Add(Caption(app
            ? "While " + p.Label + " is in front, anything you set here replaces the default. Leave a field empty to keep the default, shown in grey."
            : "Used everywhere, except where an app on the left sets its own."));

        if (app) {
            var label = new TextBox { Text = p.Label, Width = 260 };
            label.TextChanged += (s, e) => {
                p.Label = label.Text.Trim().Length > 0 ? label.Text.Trim() : p.Process;
                var item = (ListBoxItem)profileList.SelectedItem;
                item.Content = ProfileItemContent(p);
                AutomationProperties.SetName(item, p.Label);
                Dirty();
            };
            var row = FormRow("Name in the pop-up", label, "Runs for " + p.Process + ".exe");
            row.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(row);
        }

        // buttons
        panel.Children.Add(Section("Buttons"));
        var g = Columns(Px(100), Star(0.85), Star(), Star(), Star());
        int hr = AddRow(g);
        var nameHeader = Header("Name");
        nameHeader.ToolTip = "What the key does, in words: the key sheet (hold the dial button) shows it";
        Place(g, nameHeader, hr, 1);
        for (int c = 0; c < SuffixNames.Length; c++) Place(g, Header(SuffixNames[c]), hr, c + 2);
        foreach (var key in KeyOrder) {
            int r = AddRow(g);
            var name = new TextBlock { Text = key == "Dial" ? "Dial button" : key, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            if (key == "K1") name.ToolTip = "Push-to-talk: the same in every app";
            Place(g, name, r, 0);
            var back = RowBack(g, r, 5);
            string label, defLabel;
            p.Labels.TryGetValue(key, out label);
            def.Labels.TryGetValue(key, out defLabel);
            // An app's placeholder is the default name, but only while it keeps the default action.
            var labelField = PlaceholderBox(label, app && !p.Keys.ContainsKey(key) ? defLabel : "", v => {
                if (v.Length == 0) p.Labels.Remove(key); else p.Labels[key] = v;
                Dirty();
            });
            labelField.Margin = new Thickness(4, 3, 4, 3);
            AutomationProperties.SetName(labelField, name.Text + " name");
            if (app && key == "K1") labelField.IsEnabled = false;
            Place(g, labelField, r, 1);
            var fields = new List<FrameworkElement> { back, name, labelField };
            for (int c = 0; c < Suffixes.Length; c++) {
                string k = key + Suffixes[c];
                string val, dv;
                p.Keys.TryGetValue(k, out val);
                def.Keys.TryGetValue(k, out dv);
                bool locked = app && key == "K1";
                var f = new ShortcutField(locked ? "" : val, app ? (locked && string.IsNullOrEmpty(dv) ? "same in every app" : dv) : "", a => Problem(a, false));
                f.Margin = new Thickness(4, 3, 4, 3);
                f.AccessibleName = name.Text + " " + SuffixNames[c];
                if (locked) { f.IsEnabled = false; f.ToolTip = "Push-to-talk is the same in every app"; ToolTipService.SetShowOnDisabled(f, true); }
                f.Changed += v => { if (v.Length == 0) p.Keys.Remove(k); else p.Keys[k] = v; Dirty(); };
                Place(g, f, r, c + 2);
                fields.Add(f);
            }
            Link(key, new KeyRow { Back = back, Field = (ShortcutField)fields[3], Anchor = back }, fields.ToArray());
        }
        panel.Children.Add(g);
        panel.Children.Add(Caption(
            "Shortcuts: ctrl+shift+t, alt+left, f13… Several in a row: ctrl+a, backspace. 'hold ctrl+space' keeps the keys down while the button is held. " +
            "Also: nextMode / prevMode (dial modes), claude:model, claude:effort, wheel+1 / wheel-1, showKeys (the key sheet: a picture of the K30 with every key's Name). The keyboard button records a shortcut. " +
            "A long or double press action makes the plain press fire on release.", 8));

        // roller
        var rollerHeader = Section("Roller");
        panel.Children.Add(rollerHeader);
        if (app) {
            var note = Caption("The roller and K1 (push-to-talk) are the same in every app, so you can always switch away and talk.");
            panel.Children.Add(note);
            Link("Roller", new KeyRow { Anchor = note }, rollerHeader, note);
        } else {
            var rg = Columns(Px(110), Star(), Star());
            int h2 = AddRow(rg);
            Place(rg, Header("Roll up"), h2, 1);
            Place(rg, Header("Roll down"), h2, 2);
            int rr = AddRow(rg);
            var up = new ShortcutField(rollerUp, "", a => Problem(a, true)) { Margin = new Thickness(4, 3, 4, 3) };
            up.Changed += v => { rollerUp = v; Dirty(); };
            var down = new ShortcutField(rollerDown, "", a => Problem(a, true)) { Margin = new Thickness(4, 3, 4, 3) };
            down.Changed += v => { rollerDown = v; Dirty(); };
            Place(rg, up, rr, 1);
            Place(rg, down, rr, 2);
            var rollerBack = RowBack(rg, rr, 3);
            Link("Roller", new KeyRow { Back = rollerBack, Field = up, Anchor = rollerBack }, rollerBack, rollerHeader, up, down);
            panel.Children.Add(rg);
            panel.Children.Add(Caption("switch:next / switch:prev: K30 Controller's window switcher (see the Window switcher tab). alttab:next / alttab:prev: Windows' own Alt-Tab. Or any shortcut, or wheel+1 / wheel-1.", 8));
        }

        // dial modes: what turning the dial does, so they belong to the dial's ring on the drawing
        var modesHeader = Section("Dial modes");
        panel.Children.Add(modesHeader);
        var modes = new StackPanel();
        var modesBack = new Border { Child = modes, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Margin = new Thickness(-6, 0, -6, 0) };
        panel.Children.Add(modesBack);
        if (app && p.Modes.Count == 0) {
            modes.Children.Add(Caption(p.Label + " uses the default dial modes: " + string.Join(", ", def.Modes.Select(m => Str(m, "name") ?? "?")) + "."));
            var own = new Button { Content = "Give " + p.Label + " its own dial modes", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            own.Click += (s, e) => { p.Modes.Add(NewMode()); Dirty(); ShowProfile(); };
            modes.Children.Add(own);
        } else {
            modes.Children.Add(Caption("Turn the dial to use the active mode; press the dial button to go to the next one. The pop-up says which one is active." + (app ? " Remove them all to go back to the default modes." : "")));
            modes.Children.Add(BuildModes(p));
            var addMode = new Button { Content = "Add a dial mode", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            addMode.Click += (s, e) => { p.Modes.Add(NewMode()); Dirty(); ShowProfile(); };
            modes.Children.Add(addMode);
        }
        Link("DialRing", new KeyRow { Back = modesBack, Anchor = modesHeader }, modesHeader, modesBack);

        editorHost.Content = panel;
        UpdateHighlight();
        building = wasBuilding;
    }

    static JsonObject NewMode() {
        return new JsonObject { ["name"] = "Scroll", ["type"] = "keys", ["cw"] = "wheel-1", ["ccw"] = "wheel+1" };
    }

    UIElement BuildModes(ProfileModel p) {
        var g = Columns(Px(120), Px(140), Star(), Star(), Px(84), Px(116), Auto);
        g.Margin = new Thickness(0, 10, 0, 0);
        int hr = AddRow(g);
        Place(g, Header("Name"), hr, 0);
        Place(g, Header("Does"), hr, 1);
        Place(g, Header("Clockwise"), hr, 2);
        Place(g, Header("Counter-clockwise"), hr, 3);
        Place(g, Header("Settle (ms)"), hr, 4);
        Place(g, Header("Up to"), hr, 5);

        for (int i = 0; i < p.Modes.Count; i++) {
            var m = p.Modes[i];
            int index = i, r = AddRow(g);
            var cellMargin = new Thickness(4, 3, 4, 3);

            var name = new TextBox { Text = Str(m, "name") ?? "", Margin = cellMargin };
            name.TextChanged += (s, e) => { m["name"] = name.Text; Dirty(); };

            var type = new ComboBox { Margin = cellMargin, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var n in ModeTypeNames) type.Items.Add(n);
            string t = (Str(m, "type") ?? "keys").ToLowerInvariant();
            int ti = Array.IndexOf(ModeTypeIds, t);
            if (ti < 0) { type.Items.Add(t); ti = type.Items.Count - 1; }
            type.SelectedIndex = ti;

            var cw = new ShortcutField(Str(m, "cw"), "", a => Problem(a, false)) { Margin = cellMargin };
            cw.Changed += v => { SetOrRemove(m, "cw", v); Dirty(); };
            var ccw = new ShortcutField(Str(m, "ccw"), "", a => Problem(a, false)) { Margin = cellMargin };
            ccw.Changed += v => { SetOrRemove(m, "ccw", v); Dirty(); };

            var idle = new TextBox { Text = Int(m, "idleMs")?.ToString() ?? "", Margin = cellMargin, ToolTip = "How long the dial must rest before the choice is applied (default 800)" };
            idle.TextChanged += (s, e) => {
                int n;
                if (idle.Text.Trim().Length == 0) m.Remove("idleMs");
                else if (int.TryParse(idle.Text.Trim(), out n) && n > 0) m["idleMs"] = n;
                Dirty();
            };

            var max = new ComboBox { Margin = cellMargin, ToolTip = "The highest effort step the dial can reach" };
            foreach (var s in EffortSteps) max.Items.Add(s);
            max.SelectedIndex = Math.Max(0, Math.Min(EffortSteps.Length - 1, Int(m, "max") ?? 4));
            max.SelectionChanged += (s, e) => { m["max"] = max.SelectedIndex; Dirty(); };

            Action vis = () => {
                string id = type.SelectedIndex < ModeTypeIds.Length ? ModeTypeIds[type.SelectedIndex] : t;
                bool keys = id == "keys" || id == "menu";
                cw.Visibility = ccw.Visibility = keys ? Visibility.Visible : Visibility.Hidden;
                idle.Visibility = id == "menu" || id == "claude-model" || id == "claude-effort" ? Visibility.Visible : Visibility.Hidden;
                max.Visibility = id == "claude-effort" ? Visibility.Visible : Visibility.Hidden;
            };
            vis();
            type.SelectionChanged += (s, e) => {
                if (type.SelectedIndex < ModeTypeIds.Length) m["type"] = ModeTypeIds[type.SelectedIndex];
                vis();
                Dirty();
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = cellMargin };
            var upB = new Button { Content = Glyph(""), ToolTip = "Move up", IsEnabled = index > 0, Padding = new Thickness(8, 4, 8, 4) };
            upB.Click += (s, e) => { p.Modes.RemoveAt(index); p.Modes.Insert(index - 1, m); Dirty(); ShowProfile(); };
            var downB = new Button { Content = Glyph(""), ToolTip = "Move down", IsEnabled = index < p.Modes.Count - 1, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0) };
            downB.Click += (s, e) => { p.Modes.RemoveAt(index); p.Modes.Insert(index + 1, m); Dirty(); ShowProfile(); };
            var del = new Button { Content = Glyph(""), ToolTip = "Remove", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0), IsEnabled = p.Process != null || p.Modes.Count > 1 };
            del.Click += (s, e) => { p.Modes.RemoveAt(index); Dirty(); ShowProfile(); };
            buttons.Children.Add(upB);
            buttons.Children.Add(downB);
            buttons.Children.Add(del);

            Place(g, name, r, 0);
            Place(g, type, r, 1);
            Place(g, cw, r, 2);
            Place(g, ccw, r, 3);
            Place(g, idle, r, 4);
            Place(g, max, r, 5);
            Place(g, buttons, r, 6);
        }
        return g;
    }

    static void SetOrRemove(JsonObject o, string key, string v) {
        if (string.IsNullOrWhiteSpace(v)) o.Remove(key); else o[key] = v.Trim();
    }

    // ---------------------------------------------------------------- Window switcher

    UIElement BuildSwitcherTab() {
        var sw = root["switcher"] as JsonObject;
        bool allow = (Str(sw, "mode") ?? "block").Trim().Equals("allow", StringComparison.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = sw == null ? null : sw["apps"] as JsonArray;
        if (apps != null) foreach (var n in apps) { string s = AsString(n); if (!string.IsNullOrWhiteSpace(s)) listed.Add(StripExe(s.Trim())); }

        var g = Columns(Star(), Auto, Star());
        g.Margin = new Thickness(0, 16, 0, 0);
        int r0 = AddRow(g);
        var intro = new StackPanel();
        intro.Children.Add(PageTitle("Window switcher"));
        intro.Children.Add(Caption("Roll to bring up the list of open windows (on every monitor); stop rolling and it switches (the dial button switches at once). " +
                                   "Drag apps between the two lists to choose which ones it offers. Double-click an app, or use the arrows, to move it too."));
        Place(g, intro, r0, 0, 3);

        int r1 = AddRow(g);
        var h1 = Section("Shown in the switcher");
        var h2 = Section("Hidden from the switcher");
        Place(g, h1, r1, 0);
        Place(g, h2, r1, 2);

        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 180 });
        int r2 = g.RowDefinitions.Count - 1;
        shownList = MakeAppList();
        hiddenList = MakeAppList();
        Place(g, shownList, r2, 0);
        Place(g, hiddenList, r2, 2);

        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var hide = new Button { Content = Glyph(""), ToolTip = "Hide the selected apps", Padding = new Thickness(12, 8, 12, 8) };
        hide.Click += (s, e) => Move(shownList.SelectedItems.Cast<ListBoxItem>().ToList(), hiddenList);
        var show = new Button { Content = Glyph(""), ToolTip = "Show the selected apps", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0) };
        show.Click += (s, e) => Move(hiddenList.SelectedItems.Cast<ListBoxItem>().ToList(), shownList);
        arrows.Children.Add(hide);
        arrows.Children.Add(show);
        Place(g, arrows, r2, 1);

        // Every app the page knows about lands in one list: open ones, ones already listed, ones with a profile.
        var names = new List<string>();
        foreach (var n in running.Keys.Concat(listed).Concat(profiles.Where(p => p.Process != null).Select(p => p.Process)))
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        foreach (var n in names) {
            bool shown = listed.Contains(n) ? allow : !allow;
            Insert(shown ? shownList : hiddenList, AppItem(n));
        }

        int r3 = AddRow(g);
        var addRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var toShown = new Button { Content = "Add to shown", Margin = new Thickness(8, 0, 0, 0) };
        var toHidden = new Button { Content = "Add to hidden", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(toHidden, Dock.Right);
        DockPanel.SetDock(toShown, Dock.Right);
        addRow.Children.Add(toHidden);
        addRow.Children.Add(toShown);
        addNameBox = new TextBox { ToolTip = "The process name of an app that isn't open right now (Task Manager › Details, without .exe), e.g. olk for Outlook" };
        var addField = new Grid();
        addField.Children.Add(addNameBox);
        var addHint = new TextBlock { Text = "An app that isn't open: its process name, e.g. olk", IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        addHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        addNameBox.TextChanged += (s, e) => addHint.Visibility = addNameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        addField.Children.Add(addHint);
        addRow.Children.Add(addField);
        toShown.Click += (s, e) => AddByName(shownList);
        toHidden.Click += (s, e) => AddByName(hiddenList);
        Place(g, addRow, r3, 0, 3);

        int r4 = AddRow(g);
        var more = new StackPanel();
        more.Children.Add(Section("Apps in neither list"));
        more.Children.Add(Caption("Apps you haven't sorted yet, like ones you open for the first time later on:"));
        var radios = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        unsortedShow = new RadioButton { Content = "Show them", IsChecked = !allow, Margin = new Thickness(0, 0, 24, 0) };
        unsortedHide = new RadioButton { Content = "Hide them (only the apps I list are shown)", IsChecked = allow };
        unsortedShow.Checked += (s, e) => Dirty();
        unsortedHide.Checked += (s, e) => Dirty();
        radios.Children.Add(unsortedShow);
        radios.Children.Add(unsortedHide);
        more.Children.Add(radios);

        more.Children.Add(Section("Order"));
        more.Children.Add(Caption("The highlight always moves the way you roll. What changes is where the windows you used before the current one sit:"));
        var order = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var hv = sw == null ? null : sw["historyOnLeft"] as JsonValue;
        bool historyLeft;
        if (hv == null || !hv.TryGetValue(out historyLeft)) historyLeft = true;
        historyOnLeft = new RadioButton { Content = "On the left, like a browser's Back and Forward: rolling left goes back, rolling right comes forward again, and windows stay where you left them", IsChecked = historyLeft, Margin = new Thickness(0, 0, 0, 6) };
        var historyRight = new RadioButton { Content = "On the right, like Alt-Tab: most recent first, reordered after every switch", IsChecked = !historyLeft };
        historyOnLeft.Checked += (s, e) => Dirty();
        historyRight.Checked += (s, e) => Dirty();
        order.Children.Add(historyOnLeft);
        order.Children.Add(historyRight);
        more.Children.Add(order);

        more.Children.Add(Section("Timing"));
        commitSlider = new Slider { Minimum = 150, Maximum = 1500, TickFrequency = 50, IsSnapToTickEnabled = true, Width = 320, Value = Int(sw, "commitMs") ?? 400, VerticalAlignment = VerticalAlignment.Center };
        var commitText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), MinWidth = 60 };
        commitText.Text = (int)commitSlider.Value + " ms";
        commitSlider.ValueChanged += (s, e) => { commitText.Text = (int)commitSlider.Value + " ms"; Dirty(); };
        var sliderRow = new StackPanel { Orientation = Orientation.Horizontal };
        sliderRow.Children.Add(commitSlider);
        sliderRow.Children.Add(commitText);
        more.Children.Add(FormRow("Switch after the roller rests", sliderRow));
        Place(g, more, r4, 0, 3);

        return new ScrollViewer { Content = g, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 12) };
    }

    static string StripExe(string n) {
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
    }

    ListBoxItem AppItem(string process) {
        RunningApp ra;
        bool open = running.TryGetValue(process, out ra);
        var prof = profiles.FirstOrDefault(p => process.Equals(p.Process, StringComparison.OrdinalIgnoreCase));
        string label = open ? ra.Label : prof != null ? prof.Label : process;
        ImageSource icon;
        icons.TryGetValue(process, out icon);

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        sp.Children.Add(new Image { Width = 20, Height = 20, Source = icon, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var sub = Caption("   " + process + (open ? "" : "  ·  not open"));
        sub.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(sub);
        var item = new ListBoxItem { Content = sp, Tag = process, ToolTip = "Drag to the other list, or double-click" };
        AutomationProperties.SetName(item, label + " (" + process + ")");
        item.MouseDoubleClick += (s, e) => {
            var from = ItemsControl.ItemsControlFromItemContainer(item) as ListBox;
            Move(new List<ListBoxItem> { item }, from == shownList ? hiddenList : shownList);
        };
        return item;
    }

    static string SortKey(ListBoxItem i) {
        var sp = i.Content as StackPanel;
        var t = sp == null ? null : sp.Children.OfType<TextBlock>().FirstOrDefault();
        return t == null ? (string)i.Tag : t.Text;
    }

    static void Insert(ListBox list, ListBoxItem item) {
        int at = 0;
        while (at < list.Items.Count && string.Compare(SortKey((ListBoxItem)list.Items[at]), SortKey(item), StringComparison.CurrentCultureIgnoreCase) < 0) at++;
        list.Items.Insert(at, item);
    }

    void Move(List<ListBoxItem> items, ListBox to) {
        if (items.Count == 0) return;
        foreach (var it in items) {
            var from = ItemsControl.ItemsControlFromItemContainer(it) as ListBox;
            if (from == null || from == to) continue;
            from.Items.Remove(it);
            Insert(to, it);
        }
        to.SelectedItems.Clear();
        foreach (var it in items) to.SelectedItems.Add(it);
        Dirty();
    }

    void AddByName(ListBox to) {
        string n = StripExe((addNameBox.Text ?? "").Trim());
        if (n.Length == 0) return;
        var existing = shownList.Items.Cast<ListBoxItem>().Concat(hiddenList.Items.Cast<ListBoxItem>())
                                .FirstOrDefault(i => n.Equals((string)i.Tag, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Move(new List<ListBoxItem> { existing }, to);
        else { var it = AppItem(n); Insert(to, it); to.SelectedItem = it; Dirty(); }
        addNameBox.Text = "";
    }

    class DragPayload { public List<ListBoxItem> Items; }

    ListBox MakeAppList() {
        var list = new ListBox { SelectionMode = SelectionMode.Extended, AllowDrop = true, BorderThickness = new Thickness(1), Padding = new Thickness(4), Height = 250 };
        list.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        list.SetResourceReference(Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        Point start = new Point();
        ListBoxItem pressed = null;
        list.PreviewMouseLeftButtonDown += (s, e) => {
            start = e.GetPosition(list);
            pressed = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
        };
        list.PreviewMouseMove += (s, e) => {
            if (pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(list) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var items = pressed.IsSelected ? list.SelectedItems.Cast<ListBoxItem>().ToList() : new List<ListBoxItem> { pressed };
            pressed = null;
            DragDrop.DoDragDrop(list, new DataObject(typeof(DragPayload), new DragPayload { Items = items }), DragDropEffects.Move);
        };
        list.PreviewMouseLeftButtonUp += (s, e) => pressed = null;
        list.DragOver += (s, e) => {
            var p = e.Data.GetData(typeof(DragPayload)) as DragPayload;
            e.Effects = p != null && p.Items.Count > 0 && ItemsControl.ItemsControlFromItemContainer(p.Items[0]) != list ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.DragEnter += (s, e) => { if (e.Data.GetDataPresent(typeof(DragPayload))) list.SetResourceReference(Control.BorderBrushProperty, "AccentFillColorDefaultBrush"); };
        list.DragLeave += (s, e) => list.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        list.Drop += (s, e) => {
            list.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            var p = e.Data.GetData(typeof(DragPayload)) as DragPayload;
            if (p != null) Move(p.Items, list);
        };
        return list;
    }

    // ---------------------------------------------------------------- General

    UIElement BuildGeneralTab() {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 16, 16) };
        panel.Children.Add(PageTitle("General"));

        panel.Children.Add(Section("Device"));
        addressBox = new TextBox { Text = Str(root, "address") ?? "auto", Width = 240 };
        addressBox.TextChanged += (s, e) => Dirty();
        panel.Children.Add(FormRow("Bluetooth address", addressBox, "auto: the first paired device named Turing KDial…"));

        panel.Children.Add(Section("Button timing"));
        longPressBox = new TextBox { Text = (Int(root, "longPressMs") ?? 450).ToString(), Width = 90 };
        longPressBox.TextChanged += (s, e) => Dirty();
        panel.Children.Add(FormRow("Long press after", longPressBox, "ms"));
        doublePressBox = new TextBox { Text = (Int(root, "doublePressMs") ?? 300).ToString(), Width = 90 };
        doublePressBox.TextChanged += (s, e) => Dirty();
        panel.Children.Add(FormRow("Double press within", doublePressBox, "ms"));

        panel.Children.Add(Section("Config file"));
        panel.Children.Add(Caption("Everything here is stored in k30-config.json, next to K30.exe. Saving rewrites it and keeps the previous version as k30-config.json.bak. " +
                                   "A few rarely needed settings (the Bluetooth wake-up commands, a menu dial mode's open/confirm keys) are only in the file. " +
                                   "If you edit the file by hand, close this window first, then use Reload config file in the tray menu."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var open = new Button { Content = "Open config file" };
        open.Click += (s, e) => Process.Start("notepad.exe", "\"" + path + "\"");
        var folder = new Button { Content = "Open folder", Margin = new Thickness(8, 0, 0, 0) };
        folder.Click += (s, e) => Process.Start("explorer.exe", "/select,\"" + path + "\"");
        buttons.Children.Add(open);
        buttons.Children.Add(folder);
        panel.Children.Add(buttons);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ---------------------------------------------------------------- save

    bool Save() {
        var problems = new List<string>();
        foreach (var p in profiles) {
            string where = p.Process == null ? "All apps" : p.Label;
            foreach (var kv in p.Keys) { string e = Problem(kv.Value, false); if (e != null) problems.Add(where + " › " + kv.Key + ": " + e); }
            foreach (var m in p.Modes) {
                string mn = Str(m, "name") ?? "dial mode";
                if (string.IsNullOrWhiteSpace(Str(m, "name"))) problems.Add(where + " › a dial mode has no name");
                foreach (var k in new[] { "cw", "ccw" }) { string e = Problem(Str(m, k), false); if (e != null) problems.Add(where + " › " + mn + ": " + e); }
            }
        }
        foreach (var v in new[] { rollerUp, rollerDown }) { string e = Problem(v, true); if (e != null) problems.Add("Roller: " + e); }
        if (Defaults.Modes.Count == 0) problems.Add("All apps needs at least one dial mode");
        int longMs, doubleMs;
        if (!int.TryParse(longPressBox.Text.Trim(), out longMs) || longMs < 100) problems.Add("Long press: a number of milliseconds, 100 or more");
        if (!int.TryParse(doublePressBox.Text.Trim(), out doubleMs) || doubleMs < 100) problems.Add("Double press: a number of milliseconds, 100 or more");
        if (problems.Count > 0) {
            MessageBox.Show(this, "Not saved, please fix:\n\n• " + string.Join("\n• ", problems.Take(12)), "K30 Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var def = Defaults;
        root["address"] = addressBox.Text.Trim().Length == 0 ? "auto" : addressBox.Text.Trim();
        root["longPressMs"] = longMs;
        root["doublePressMs"] = doubleMs;
        root["keys"] = KeysNode(def.Keys);
        SetLabels(root, def.Labels);
        var roller = (root["roller"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        SetOrRemove(roller, "up", rollerUp);
        SetOrRemove(roller, "down", rollerDown);
        root["roller"] = roller;

        // The two lists are what the user sees; which one gets written depends on what happens to apps in neither.
        bool allow = unsortedHide.IsChecked == true;
        var sw = (root["switcher"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        sw["mode"] = allow ? "allow" : "block";
        sw["apps"] = new JsonArray((allow ? shownList : hiddenList).Items.Cast<ListBoxItem>().Select(i => (JsonNode)JsonValue.Create((string)i.Tag)).ToArray());
        sw["commitMs"] = (int)commitSlider.Value;
        sw["historyOnLeft"] = historyOnLeft.IsChecked == true;
        root["switcher"] = sw;

        root["dialModes"] = ModesNode(def.Modes);
        var aps = new JsonObject();
        foreach (var p in profiles.Skip(1)) {
            var n = (JsonObject)p.Node.DeepClone();
            n["label"] = p.Label;
            n["keys"] = KeysNode(p.Keys);
            SetLabels(n, p.Labels);
            if (p.Modes.Count > 0) n["dialModes"] = ModesNode(p.Modes); else n.Remove("dialModes");
            aps[p.Process] = n;
        }
        root["appProfiles"] = aps;
        foreach (var p in profiles.Skip(1)) p.Node = (JsonObject)aps[p.Process];

        string json = root.ToJsonString(WriteOptions);
        try {
            Config.FromJson(json); // the tray app must be able to read it back
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        } catch (Exception e) {
            MessageBox.Show(this, "Couldn't save: " + e.Message, "K30 Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        dirty = false;
        status.Text = "Saved at " + DateTime.Now.ToString("HH:mm") + " — the K30 is using it now.";
        if (saved != null) saved();
        return true;
    }

    static JsonObject KeysNode(Dictionary<string, string> keys) {
        var o = new JsonObject();
        var canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in KeyOrder)
            foreach (var s in Suffixes) {
                string name = k + s, v;
                canonical.Add(name);
                if (keys.TryGetValue(name, out v) && !string.IsNullOrWhiteSpace(v)) o[name] = v.Trim();
            }
        foreach (var kv in keys)
            if (!canonical.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)) o[kv.Key] = kv.Value.Trim();
        return o;
    }

    static void SetLabels(JsonObject owner, Dictionary<string, string> labels) {
        var o = KeysNode(labels);
        if (o.Count > 0) owner["labels"] = o; else owner.Remove("labels");
    }

    static JsonArray ModesNode(List<JsonObject> modes) {
        return new JsonArray(modes.Select(m => (JsonNode)m.DeepClone()).ToArray());
    }
}
