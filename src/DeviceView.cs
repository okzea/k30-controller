// The Turing KeyDial K30 in the settings window: a picture of the device (k30.png, embedded in the exe)
// with an invisible shape over each control. A shape can be highlighted (the settings page lights up
// the key whose field you're on) and clicked (the page jumps to that key's field).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

class DeviceView : Viewbox {
    // Part ids: K1…K11, "Dial" (the dial's face: its button), "DialRing" (turning it: the dial modes), "Roller".
    public event Action<string> PartClicked;
    public event Action<string> PartHovered; // null when the mouse leaves a part

    readonly Dictionary<string, Shape> parts = new Dictionary<string, Shape>(StringComparer.OrdinalIgnoreCase);
    readonly Brush accent, accentFill;
    readonly Color accentColor;
    readonly Canvas overlay = new Canvas();
    string highlighted;

    // The picture is 528 × 1410; every coordinate below is in its pixels.
    public DeviceView(Color accentColor) {
        this.accentColor = accentColor;
        accent = Frozen(new SolidColorBrush(accentColor));
        accentFill = Frozen(new SolidColorBrush(Color.FromArgb(0x45, accentColor.R, accentColor.G, accentColor.B)));
        Stretch = Stretch.Uniform;

        var picture = new Image { Source = LoadPicture(), Width = 528, Height = 1410 };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var stack = new Grid { Width = 528, Height = 1410 };
        stack.Children.Add(picture);
        stack.Children.Add(overlay);
        Child = stack;

        // Dial: the ring first, so the face (on top) takes the clicks inside it.
        Add("DialRing", new Ellipse { Width = 444, Height = 464 }, 291 - 222, 250 - 232, "Dial (turn): dial modes");
        Add("Dial", new Ellipse { Width = 332, Height = 344 }, 296 - 166, 268 - 172, "Dial button (press)");

        // Side keys; the top and bottom ones have their inner corner cut off.
        Add("K1", Poly("75,441 102,441 171,492 171,617 75,617"), "K1");
        Add("K2", Box(75, 621, 96, 168), "K2");
        Add("K3", Box(75, 795, 96, 181), "K3");
        Add("K4", Poly("75,986 171,986 171,1120 90,1184 75,1184"), "K4");
        Add("K5", Poly("470,455 491,455 491,624 401,624 401,499"), "K5");
        Add("K6", Box(401, 632, 90, 160), "K6");
        Add("K7", Box(401, 801, 90, 183), "K7");
        Add("K8", Poly("401,987 491,987 491,1178 486,1184 401,1119"), "K8");

        // Roller (its housing), K9/K10 (one pill split in two), K11 (the pointed key at the bottom).
        Add("Roller", new Rectangle { Width = 92, Height = 237, RadiusX = 46, RadiusY = 46 }, 239, 530, "Roller");
        Add("K9", new Path { Data = Geometry.Parse("M 231,945 L 231,850 A 56,56 0 0 1 343,850 L 343,945 Z") }, "K9");
        Add("K10", new Path { Data = Geometry.Parse("M 231,949 L 343,949 L 343,1044 A 56,56 0 0 1 231,1044 Z") }, "K10");
        Add("K11", new Path { Data = Geometry.Parse("M 175,1212 L 218,1169 Q 221,1166 226,1166 L 348,1166 Q 353,1166 356,1169 L 397,1212 Q 286,1278 175,1212 Z") }, "K11");
    }

    static ImageSource LoadPicture() {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = Assembly.GetExecutingAssembly().GetManifestResourceStream("k30.png");
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// Lights up one part (null: none): the accent colour as an outline, a tint and a glow.
    public void Highlight(string id) {
        if (string.Equals(id, highlighted, StringComparison.OrdinalIgnoreCase)) return;
        Shape s;
        if (highlighted != null && parts.TryGetValue(highlighted, out s)) { s.Stroke = null; s.Fill = Brushes.Transparent; s.Effect = null; }
        highlighted = id;
        if (id != null && parts.TryGetValue(id, out s)) {
            s.Stroke = accent;
            s.StrokeThickness = 7;
            s.Fill = id == "DialRing" ? Brushes.Transparent : accentFill; // the ring's tint would cover the face
            s.Effect = new DropShadowEffect { Color = accentColor, ShadowDepth = 0, BlurRadius = 40, Opacity = 1 };
        }
    }

    static Shape Box(double x, double y, double w, double h) {
        var r = new Rectangle { Width = w, Height = h, RadiusX = 6, RadiusY = 6 };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        return r;
    }

    static Shape Poly(string points) {
        return new Polygon { Points = PointCollection.Parse(points), StrokeLineJoin = PenLineJoin.Round };
    }

    void Add(string id, Shape s, double x, double y, string tip) {
        Canvas.SetLeft(s, x); Canvas.SetTop(s, y);
        Add(id, s, tip);
    }

    void Add(string id, Shape s, string tip) {
        s.Fill = Brushes.Transparent; // invisible, but still takes the mouse
        s.Cursor = Cursors.Hand;
        s.ToolTip = tip;
        s.MouseEnter += (o, e) => { if (PartHovered != null) PartHovered(id); };
        s.MouseLeave += (o, e) => { if (PartHovered != null) PartHovered(null); };
        s.MouseLeftButtonUp += (o, e) => { if (PartClicked != null) PartClicked(id); e.Handled = true; };
        parts[id] = s;
        overlay.Children.Add(s);
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
