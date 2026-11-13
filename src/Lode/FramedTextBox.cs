using System.Windows.Forms.VisualStyles;

/// <summary>
/// A text box drawn as the address bar is: the same border and height, with a borderless box
/// centred on the text line inside it. Used for the filter box, so the two boxes on the
/// navigation bar match.
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
        Height = TextFrame.Height(Font);
    }

    protected override void OnFontChanged(EventArgs eventArgs)
    {
        base.OnFontChanged(eventArgs);
        Height = TextFrame.Height(Font);
        PlaceBox();
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        PlaceBox();
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        PlaceBox();
    }

    // On a display above 100% the form scales its controls after they are built, and again when
    // the window moves to a display of another scale. Scaled, the frame would be taller than its
    // font, which is already at the display's scale, needs; it keeps a text box's height instead,
    // and puts the box, which ignores scaling, back on its text line.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        Height = TextFrame.Height(Font);
        PlaceBox();
    }

    void PlaceBox()
    {
        var inset = TextFrame.Inset(this);
        Box.SetBounds(
            inset,
            (ClientSize.Height - Box.PreferredHeight) / 2,
            Math.Max(0, ClientSize.Width - inset * 2),
            Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(SystemColors.Window);
        TextFrame.DrawBorder(eventArgs.Graphics, ClientRectangle, Box.Focused);
    }

    // A click in the margin around the box still puts the caret in it.
    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
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
    /// A text box's border and background. Focused, Windows 11 adds an accent line along the bottom.
    /// </summary>
    public static void DrawBorder(Graphics graphics, Rectangle bounds, bool focused)
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
