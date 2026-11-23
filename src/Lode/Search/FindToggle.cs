/// <summary>
/// A toggle in Find in Files' strip, as Sublime's and VS Code's are: a short label, with a shade
/// of the accent behind it and an accent border while it is on.
/// </summary>
sealed class FindToggle : CheckBox
{
    bool hovered;

    public FindToggle()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        Appearance = Appearance.Button;
        AutoSize = false;
        Margin = new(1);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return new(text.Width + LogicalToDeviceUnits(14), TextFrame.Height(Font));
    }

    protected override void OnTextChanged(EventArgs args)
    {
        base.OnTextChanged(args);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        Size = GetPreferredSize(Size.Empty);
    }

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
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        var background = Parent?.BackColor ?? SystemColors.Control;
        graphics.Clear(background);
        var text = ForeColor;
        var bounds = ClientRectangle;
        var radius = LogicalToDeviceUnits(ExplorerStyle.ButtonRadius);
        if (SystemInformation.HighContrast)
        {
            if (Checked)
            {
                graphics.Clear(SystemColors.Highlight);
                text = SystemColors.HighlightText;
            }
        }
        else if (Checked)
        {
            ExplorerStyle.FillButton(graphics, bounds, ExplorerStyle.Blend(background, ExplorerStyle.Accent, 0.22f), radius);
            ExplorerStyle.DrawButtonBorder(graphics, bounds, ExplorerStyle.Accent, radius);
        }
        else if (hovered)
        {
            ExplorerStyle.FillButton(graphics, bounds, ExplorerStyle.Hover(background), radius);
        }

        if (!Enabled)
        {
            text = ExplorerStyle.DisabledText;
        }

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            ClientRectangle,
            text,
            TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine |
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter);
        if (Focused &&
            ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }
}
