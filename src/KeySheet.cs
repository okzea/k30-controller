// The key sheet: hold the dial button ("showKeys") to see, over the picture of the K30, what every
// control does in the app you're in. Same dark card as the pop-up and the window switcher, centred on
// each monitor, with a label per control and a leader line to it (like docs/key-map.html).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// One label on the sheet: which control, what it does, and the shortcut or extra gestures underneath.
class SheetEntry {
    public string Id;      // K1…K11, Dial, DialTurn, Roller
    public string Tag;     // shown small, e.g. "K5"
    public string Title;   // what it does, e.g. "Close tab"
    public string Detail;  // e.g. "Ctrl+W · hold: …"
}

class KeySheet : Form {
    static readonly Color BackgroundColor = Color.FromArgb(15, 15, 15);
    static readonly Color BorderColor = Color.FromArgb(31, 255, 255, 255);
    static readonly Color BoxFill = Color.FromArgb(27, 27, 29);
    static readonly Color BoxBorder = Color.FromArgb(40, 255, 255, 255);
    static readonly Color TitleColor = Color.White;
    static readonly Color DimColor = Color.FromArgb(150, 255, 255, 255);
    static readonly Color TagColor = Color.FromArgb(90, 200, 250);
    static readonly Color LeadColor = Color.FromArgb(80, 255, 255, 255);

    // Layout, in 96-dpi pixels before scaling. Labels sit where their controls are on the device: the
    // dial's in a row on top, the side keys in a column on each side (level with their keys, so the
    // leader lines run straight across), and the centre column's (roller, K9, K10, K11) in a row below.
    const int Width0 = 1120, Pad = 28, Header = 50, RowGap = 32, PictureHeight = 560, Radius = 22;
    const int SideWidth = 300, TopWidth = 300, BottomWidth = 222, BoxHeight = 64, Gap = 10, BottomGap = 14;
    const int PictureW = 528, PictureH = 1410; // k30.png's size: the anchors below are in its pixels

    enum Side { Top, Left, Right, Bottom }

    // Where each control's leader line ends on the picture, per row or column, in reading order.
    // The bottom row's anchors sit on the left edge of the roller and K9 and the right edge of K10
    // and K11, so the lines fan in from both sides without crossing each other or the side labels.
    static readonly (string Id, int X, int Y, Side Side)[] Anchors = {
        ("DialTurn", 150, 102, Side.Top), ("Dial", 330, 200, Side.Top),
        ("K1", 92, 530, Side.Left), ("K2", 92, 705, Side.Left), ("K3", 92, 885, Side.Left), ("K4", 92, 1080, Side.Left),
        ("K5", 474, 540, Side.Right), ("K6", 474, 712, Side.Right), ("K7", 474, 892, Side.Right), ("K8", 474, 1080, Side.Right),
        ("Roller", 252, 700, Side.Bottom), ("K9", 240, 925, Side.Bottom), ("K10", 335, 1030, Side.Bottom), ("K11", 388, 1212, Side.Bottom),
    };

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HwndTopmost = new IntPtr(-1);
    static Bitmap picture;

    string title = "", subtitle = "";
    Dictionary<string, SheetEntry> entries = new Dictionary<string, SheetEntry>();
    float scale = 1f;

    public KeySheet() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = BackgroundColor;
        Opacity = 0.96;
        DoubleBuffered = true;
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
        if (m.Msg == 0x02E0) return; // WM_DPICHANGED: ShowSheet sizes the card for its monitor itself
        base.WndProc(ref m);
    }

    static Bitmap Picture() {
        if (picture == null)
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("k30.png"))
            using (var b = new Bitmap(s))
                picture = new Bitmap(b); // a copy, so the stream can close
        return picture;
    }

    int S(float v) { return (int)Math.Round(v * scale); }
    int Height0 { get { return Pad + Header + BoxHeight + RowGap + PictureHeight + RowGap + BoxHeight + Pad; } }

    public void ShowSheet(string title, string subtitle, IEnumerable<SheetEntry> list, Screen screen) {
        this.title = title ?? "";
        this.subtitle = subtitle ?? "";
        entries = list.ToDictionary(e => e.Id);
        var area = screen.WorkingArea;
        // The monitor's scale, shrunk if needed so the whole card fits on screen.
        scale = Math.Min(Osd.ScaleFor(screen), Math.Min(area.Width * 0.96f / Width0, area.Height * 0.94f / Height0));
        int w = S(Width0), h = S(Height0);
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

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var path = Osd.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(Radius)))
        using (var border = new Pen(BorderColor, 1f))
            g.DrawPath(border, path);

        using (var titleFont = new Font("Segoe UI Semibold", 22f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var tagFont = new Font("Segoe UI Semibold", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var nameFont = new Font("Segoe UI Semibold", 16f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var detailFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var white = new SolidBrush(TitleColor))
        using (var dim = new SolidBrush(DimColor))
        using (var tagBrush = new SolidBrush(TagColor))
        using (var boxFill = new SolidBrush(BoxFill))
        using (var boxPen = new Pen(BoxBorder, 1f))
        using (var lead = new Pen(LeadColor, Math.Max(1f, 1.2f * scale)))
        using (var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter }) {
            // header: the app, and how to close
            g.DrawString(title, titleFont, white, S(Pad), S(Pad) - S(6));
            var ts = g.MeasureString(title, titleFont);
            g.DrawString(subtitle, subFont, dim, new RectangleF(S(Pad) + ts.Width + S(12), S(Pad) + S(6), Width - S(Pad * 2) - ts.Width - S(12), S(24)), fmt);

            // rows: header, dial labels, the picture (with the side columns beside it), centre labels
            float topRow = S(Pad + Header);
            float top = topRow + S(BoxHeight + RowGap);
            float bottomRow = top + S(PictureHeight + RowGap);
            float k = S(PictureHeight) / (float)PictureH;
            float pw = PictureW * k, px = (Width - pw) / 2f;
            g.DrawImage(Picture(), new RectangleF(px, top, pw, S(PictureHeight)));

            // Each label: its box, and a leader line from the box's side facing the picture to the control.
            Action<(string Id, int X, int Y, Side Side), RectangleF> label = (a, box) => {
                var en = entries[a.Id];
                float ax = px + a.X * k, ay = top + a.Y * k, cx = box.Left + box.Width / 2f, cy = box.Top + box.Height / 2f;
                PointF start, elbow;
                switch (a.Side) {
                    case Side.Left:   start = new PointF(box.Right, cy); elbow = new PointF(box.Right + S(16), cy); break;
                    case Side.Right:  start = new PointF(box.Left, cy); elbow = new PointF(box.Left - S(16), cy); break;
                    case Side.Top:    start = new PointF(cx, box.Bottom); elbow = new PointF(cx, box.Bottom + S(12)); break;
                    default:          start = new PointF(cx, box.Top); elbow = new PointF(cx, box.Top - S(12)); break;
                }
                g.DrawLines(lead, new[] { start, elbow, new PointF(ax, ay) });
                using (var dot = new SolidBrush(TagColor)) g.FillEllipse(dot, ax - S(3.5f), ay - S(3.5f), S(7), S(7));

                using (var bp = Osd.RoundedRect(Rectangle.Round(box), S(12))) { g.FillPath(boxFill, bp); g.DrawPath(boxPen, bp); }
                float tx = box.Left + S(16), tagW = S(52);
                g.DrawString(en.Tag, tagFont, tagBrush, new RectangleF(tx, box.Top + S(11), tagW, S(20)), fmt);
                g.DrawString(en.Title ?? "", nameFont, en.Title == "Nothing" ? dim : white, new RectangleF(tx + tagW, box.Top + S(8), box.Width - tagW - S(28), S(26)), fmt);
                g.DrawString(en.Detail ?? "", detailFont, dim, new RectangleF(tx, box.Top + S(36), box.Width - S(28), S(22)), fmt);
            };

            // dial: a row centred above the picture
            var dial = Anchors.Where(a => a.Side == Side.Top && entries.ContainsKey(a.Id)).ToList();
            float dx = (Width - (dial.Count * S(TopWidth) + (dial.Count - 1) * S(BottomGap))) / 2f;
            for (int i = 0; i < dial.Count; i++)
                label(dial[i], new RectangleF(dx + i * S(TopWidth + BottomGap), topRow, S(TopWidth), S(BoxHeight)));

            // side keys: a column on each side, each label level with its key where they don't overlap
            foreach (var side in new[] { Side.Left, Side.Right }) {
                var col = Anchors.Where(a => a.Side == side && entries.ContainsKey(a.Id)).ToList();
                var ys = Stack(col.Select(a => top + a.Y * k - S(BoxHeight) / 2f).ToList(), top, top + S(PictureHeight));
                float bx = side == Side.Left ? S(Pad) : Width - S(Pad) - S(SideWidth);
                for (int i = 0; i < col.Count; i++) label(col[i], new RectangleF(bx, ys[i], S(SideWidth), S(BoxHeight)));
            }

            // centre column (roller, K9, K10, K11): a row centred below the picture
            var centre = Anchors.Where(a => a.Side == Side.Bottom && entries.ContainsKey(a.Id)).ToList();
            float bx0 = (Width - (centre.Count * S(BottomWidth) + (centre.Count - 1) * S(BottomGap))) / 2f;
            for (int i = 0; i < centre.Count; i++)
                label(centre[i], new RectangleF(bx0 + i * S(BottomWidth + BottomGap), bottomRow, S(BottomWidth), S(BoxHeight)));
        }
    }

    /// Spreads label boxes (their desired top edges, in order) so none overlap and all fit between
    /// top and bottom: pushed down past the one above, then up from the bottom if they overflow.
    List<float> Stack(List<float> want, float top, float bottom) {
        float step = S(BoxHeight + Gap);
        var ys = new List<float>(want);
        for (int i = 0; i < ys.Count; i++) ys[i] = Math.Max(ys[i], i == 0 ? top : ys[i - 1] + step);
        for (int i = ys.Count - 1; i >= 0; i--) ys[i] = Math.Min(ys[i], i == ys.Count - 1 ? bottom - S(BoxHeight) : ys[i + 1] - step);
        return ys;
    }
}
