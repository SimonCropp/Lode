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

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        hovered = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button == MouseButtons.Left)
        {
            pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        pressed = false;
        Invalidate();
    }

    // Drawn here in full, not by Button, whose flat look has square corners.
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        var background = Parent?.BackColor ?? SystemColors.Control;
        graphics.Clear(background);
        if (Enabled &&
            (hovered || pressed))
        {
            var shade = ExplorerStyle.Hover(background);
            if (pressed)
            {
                shade = ExplorerStyle.Pressed(background);
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(shade);
            using var shape = ExplorerStyle.RoundedRectangle(ClientRectangle, LogicalToDeviceUnits(4));
            graphics.FillPath(brush, shape);
            graphics.SmoothingMode = SmoothingMode.Default;
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
