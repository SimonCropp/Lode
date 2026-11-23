using System.Windows.Forms.VisualStyles;

/// <summary>
/// A text box drawn as the address bar is: the same rounded background and height, with a
/// borderless box centred on the text line inside it. Used for the filter box, so the two
/// boxes on the navigation bar match.
/// </summary>
sealed class FramedTextBox : Control
{
    public FrameTextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None
    };

    public FramedTextBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        // The border shows the focus.
        Box.Enter += (_, _) => Invalidate();
        Box.Leave += (_, _) => Invalidate();
        Controls.Add(Box);
        Height = FrameHeight();
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        Height = FrameHeight();
        PlaceBox();
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        PlaceBox();
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        PlaceBox();
    }

    // On a display above 100% the form scales its controls after they are built, and again when
    // the window moves to a display of another scale. Scaled, the frame would be taller than its
    // font, which is already at the display's scale, needs; it keeps a text box's height instead,
    // and puts the box, which ignores scaling, back on its text line.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        Height = FrameHeight();
        PlaceBox();
    }

    /// <summary>
    /// An icon at the right-hand end, as Explorer's search box has its magnifier there. None
    /// when null.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public char? Glyph
    {
        get => glyph;
        set
        {
            glyph = value;
            PlaceBox();
            Invalidate();
        }
    }

    char? glyph;

    int GlyphSize => LogicalToDeviceUnits(14);

    // The icon's square, centred on the text line, as far in from the right as the text is from
    // the left.
    Rectangle GlyphBounds
    {
        get
        {
            var size = GlyphSize;
            return new(
                ClientSize.Width - TextFrame.Inset(this) - size,
                (ClientSize.Height - size) / 2,
                size,
                size);
        }
    }

    void PlaceBox()
    {
        var inset = TextFrame.Inset(this);
        var right = ClientSize.Width - inset;
        if (glyph != null)
        {
            right = GlyphBounds.Left - inset;
        }

        Box.SetBounds(
            inset,
            (ClientSize.Height - Box.PreferredHeight) / 2,
            Math.Max(0, right - inset),
            Box.PreferredHeight);
    }

    /// <summary>
    /// Whether a faint border shows without the focus too: for a box on a white background, where
    /// the box's own white would not show where it is.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Outlined { get; set; }

    /// <summary>
    /// Whether it is drawn as GitHub Desktop draws its text boxes: see
    /// <see cref="TextFrame.DrawDesktop"/>.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Desktop
    {
        get => desktop;
        set
        {
            desktop = value;
            Height = FrameHeight();
            PlaceBox();
            Invalidate();
        }
    }

    bool desktop;

    /// <summary>
    /// Whether it is drawn as Windows draws a push button, for a box beside one, such as a
    /// Choose button: a pixel in from each edge, its corners as round as the button's, and two
    /// pixels taller for the margin, so beside a button as tall the two line up.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool BesideButtons
    {
        get => besideButtons;
        set
        {
            besideButtons = value;
            Height = FrameHeight();
            PlaceBox();
            Invalidate();
        }
    }

    bool besideButtons;

    int FrameHeight()
    {
        if (desktop)
        {
            return TextFrame.DesktopHeight(this, Font);
        }

        if (besideButtons)
        {
            return TextFrame.Height(Font) + ExplorerStyle.PushButtonInset * 2;
        }

        return TextFrame.Height(Font);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        if (desktop)
        {
            TextFrame.DrawDesktop(args.Graphics, this, Box.Focused);
        }
        else
        {
            TextFrame.Draw(args.Graphics, this, Box.Focused, besideButtons);
        }

        if (!desktop &&
            Outlined &&
            !Box.Focused)
        {
            TextFrame.Outline(args.Graphics, this, besideButtons);
        }

        if (glyph is { } icon)
        {
            args.Graphics.DrawImage(Icons.Bitmap(icon, GlyphSize, ExplorerStyle.SecondaryText), GlyphBounds);
        }
    }

    // A click in the margin around the box still puts the caret in it.
    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        Box.Focus();
    }
}

/// <summary>
/// The borderless text box inside a frame. The frame decides where it goes, so it ignores the
/// form's scaling: scaled after the frame has placed it, it would sit low and run over the
/// frame's right border.
/// </summary>
sealed class FrameTextBox : TextBox
{
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
    }
}

/// <summary>
/// The look the address bar and filter box share.
/// </summary>
static class TextFrame
{
    /// <summary>
    /// As tall as a standard text box with a border.
    /// </summary>
    public static int Height(Font font)
    {
        using var probe = new TextBox
        {
            Font = font
        };
        return probe.PreferredHeight;
    }

    /// <summary>
    /// How far text starts in from the left edge.
    /// </summary>
    public static int Inset(Control control) =>
        control.LogicalToDeviceUnits(6);

    /// <summary>
    /// The box's background, as Windows 11's Explorer draws its address and search boxes: white,
    /// with its corners rounded on the bar behind it, and no border until it has the focus, when
    /// it gets a faint one and an accent line along the bottom. With high contrast on, a text
    /// box's own border, which shows where the box is. Beside push buttons, in from the edges as
    /// one is: see <see cref="FramedTextBox.BesideButtons"/>.
    /// </summary>
    public static void Draw(Graphics graphics, Control control, bool focused, bool besideButtons = false)
    {
        var (bounds, radius) = Frame(control, besideButtons);
        if (SystemInformation.HighContrast ||
            !Application.RenderWithVisualStyles)
        {
            graphics.Clear(SystemColors.Window);
            DrawSystemBorder(graphics, bounds, focused);
            return;
        }

        graphics.Clear(control.Parent?.BackColor ?? SystemColors.Control);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = ExplorerStyle.RoundedRectangle(bounds, radius);
        using var window = new SolidBrush(ExplorerStyle.Window);
        graphics.FillPath(window, shape);
        if (focused)
        {
            using var outline = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right - 1, bounds.Bottom - 1), radius);
            using var pen = new Pen(ExplorerStyle.TabBorder);
            graphics.DrawPath(pen, outline);

            var accent = control.LogicalToDeviceUnits(2);
            using var accentBrush = new SolidBrush(ExplorerStyle.Accent);
            graphics.SetClip(shape);
            graphics.FillRectangle(accentBrush, bounds.Left, bounds.Bottom - accent, bounds.Width, accent);
            graphics.ResetClip();
        }

        graphics.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>
    /// The box as GitHub Desktop draws its text boxes: white, its corners more rounded, with a
    /// grey border always, dark enough to see on the grey behind it, which the focus turns blue,
    /// with a faint blue ring outside it. A pixel is left all round for the ring. With high
    /// contrast on, a text box's own border.
    /// </summary>
    public static void DrawDesktop(Graphics graphics, Control control, bool focused)
    {
        var bounds = control.ClientRectangle;
        if (SystemInformation.HighContrast ||
            !Application.RenderWithVisualStyles)
        {
            graphics.Clear(SystemColors.Window);
            DrawSystemBorder(graphics, bounds, focused);
            return;
        }

        graphics.Clear(control.Parent?.BackColor ?? SystemColors.Control);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = control.LogicalToDeviceUnits(DesktopStyle.Radius);
        using var shape = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(bounds.Left + 1, bounds.Top + 1, bounds.Right - 2, bounds.Bottom - 2), radius);
        using (var window = new SolidBrush(ExplorerStyle.Window))
        {
            graphics.FillPath(window, shape);
        }

        var border = DesktopStyle.BoxBorder;
        if (focused)
        {
            border = DesktopStyle.Blue;
            using var ring = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right - 1, bounds.Bottom - 1), radius + 1);
            using var ringPen = new Pen(Color.FromArgb(64, border));
            graphics.DrawPath(ringPen, ring);
        }

        using (var pen = new Pen(border))
        {
            graphics.DrawPath(pen, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>
    /// How tall a box drawn as Desktop's is: Desktop's 25 pixels, or a text box's height for a
    /// larger font, and the pixel either side for the focus ring.
    /// </summary>
    public static int DesktopHeight(Control control, Font font) =>
        Math.Max(Height(font), control.LogicalToDeviceUnits(DesktopStyle.BoxHeight)) + 2;

    /// <summary>
    /// The faint border the box has with the focus, without the accent line along the bottom.
    /// </summary>
    public static void Outline(Graphics graphics, Control control, bool besideButtons = false)
    {
        if (SystemInformation.HighContrast ||
            !Application.RenderWithVisualStyles)
        {
            return;
        }

        var (bounds, radius) = Frame(control, besideButtons);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right - 1, bounds.Bottom - 1), radius);
        using var pen = new Pen(ExplorerStyle.TabBorder);
        graphics.DrawPath(pen, outline);
        graphics.SmoothingMode = SmoothingMode.Default;
    }

    // Where the box goes, and how round its corners are: all of the control, as Explorer's, or
    // beside push buttons, in from its edges and as round as one.
    static (Rectangle Bounds, int Radius) Frame(Control control, bool besideButtons)
    {
        var bounds = control.ClientRectangle;
        if (besideButtons)
        {
            var inset = ExplorerStyle.PushButtonInset;
            return (Rectangle.Inflate(bounds, -inset, -inset), ExplorerStyle.PushButtonRadius);
        }

        return (bounds, control.LogicalToDeviceUnits(4));
    }

    static void DrawSystemBorder(Graphics graphics, Rectangle bounds, bool focused)
    {
        var element = focused ? VisualStyleElement.TextBox.TextEdit.Focused : VisualStyleElement.TextBox.TextEdit.Normal;
        if (Application.RenderWithVisualStyles &&
            VisualStyleRenderer.IsElementDefined(element))
        {
            new VisualStyleRenderer(element).DrawBackground(graphics, bounds);
            return;
        }

        ControlPaint.DrawBorder3D(graphics, bounds, Border3DStyle.Sunken);
    }
}
