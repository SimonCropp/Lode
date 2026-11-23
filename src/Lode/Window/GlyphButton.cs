/// <summary>
/// A button that is only a glyph, as Explorer's Back, Forward, Up and Refresh are: no border,
/// and under the mouse a shade darker than the bar behind it, its corners rounded; pressed, a
/// shade lighter, as Windows 11 has it.
/// </summary>
sealed class GlyphButton : Button
{
    // Pixels at 100% scale.
    const int glyphSize = 12;

    bool hovered;
    bool pressed;

    // Painted through a buffer, so the shade coming and going doesn't flicker.
    public GlyphButton() =>
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

    protected override void OnMouseEnter(EventArgs args)
    {
        base.OnMouseEnter(args);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        hovered = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button == MouseButtons.Left)
        {
            pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        pressed = false;
        Invalidate();
    }

    /// <summary>
    /// Whether what the button turns on is on: then it keeps the pressed shade, as a toggle button
    /// on a toolbar does, Changes' filter shown say.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
            {
                return;
            }

            isChecked = value;
            Invalidate();
        }
    }

    bool isChecked;

    // Drawn here in full, not by Button, whose flat look has square corners.
    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        var background = Parent?.BackColor ?? SystemColors.Control;
        graphics.Clear(background);
        var highlighted = Enabled &&
                          (hovered || pressed || isChecked);
        if (highlighted)
        {
            var shade = ExplorerStyle.Highlighted(background, pressed || (isChecked && !hovered));
            ExplorerStyle.FillButton(graphics, ClientRectangle, shade, LogicalToDeviceUnits(ExplorerStyle.ButtonRadius));
        }

        if (Text.Length == 0)
        {
            return;
        }

        var color = ForeColor;
        if (!Enabled)
        {
            color = ExplorerStyle.DisabledText;
        }
        else if (highlighted)
        {
            color = ExplorerStyle.HighlightedText(color);
        }

        // An icon of the glyph, smoothed in gray, as the command bar's are. Drawn as text, it was
        // smoothed with ClearType, which turned a thin stroke, Up's, green. At Explorer's size.
        var size = LogicalToDeviceUnits(glyphSize);
        graphics.DrawImage(
            Icons.Bitmap(Text[0], size, color),
            (Width - size) / 2,
            (Height - size) / 2,
            size,
            size);
    }
}
