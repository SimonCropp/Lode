using System.Drawing.Drawing2D;

/// <summary>
/// A flat tab strip whose tabs each carry a cross that closes them.
/// </summary>
sealed class ClosableTabControl : TabControl
{
    const int leftButtonDownMessage = 0x0201;

    // Pixels at 100% scale.
    const int crossBoxSize = 16;
    const int crossSize = 8;
    const int crossMargin = 5;
    const int textMargin = 8;
    const int horizontalPadding = 17;
    const int verticalPadding = 5;

    int hoveredTab = -1;
    int hoveredCross = -1;

    public event Action<TabPage>? CloseRequested;

    public ClosableTabControl()
    {
        // Painted here rather than by Windows, so the cross can be part of each tab. Windows
        // still lays the tabs out: it sizes each to its text plus this padding, which is what
        // leaves room for the cross.
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        Padding = new(LogicalToDeviceUnits(horizontalPadding), LogicalToDeviceUnits(verticalPadding));
        ShowToolTips = true;
    }

    Rectangle CrossBounds(int index)
    {
        var tab = GetTabRect(index);
        var size = LogicalToDeviceUnits(crossBoxSize);
        return new(
            tab.Right - size - LogicalToDeviceUnits(crossMargin),
            tab.Top + (tab.Height - size) / 2,
            size,
            size);
    }

    int TabAt(Point location)
    {
        for (var index = 0; index < TabCount; index++)
        {
            if (GetTabRect(index).Contains(location))
            {
                return index;
            }
        }

        return -1;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.Clear(SystemColors.Control);
        if (TabCount == 0)
        {
            return;
        }

        // The line the tabs sit on. The selected tab is painted over it, so it reads as
        // joined to the page below.
        using var border = new Pen(SystemColors.ActiveBorder);
        var baseline = DisplayRectangle.Top - 1;
        graphics.DrawLine(border, 0, baseline, Width, baseline);

        for (var index = 0; index < TabCount; index++)
        {
            if (index != SelectedIndex)
            {
                PaintTab(graphics, border, index, baseline);
            }
        }

        if (SelectedIndex >= 0)
        {
            PaintTab(graphics, border, SelectedIndex, baseline);
        }
    }

    void PaintTab(Graphics graphics, Pen border, int index, int baseline)
    {
        var tab = GetTabRect(index);
        var selected = index == SelectedIndex;
        if (selected)
        {
            var body = Rectangle.FromLTRB(tab.Left, tab.Top, tab.Right, baseline + 1);
            graphics.FillRectangle(SystemBrushes.Window, body);
            graphics.DrawLines(
                border,
                [
                    new(body.Left, body.Bottom - 1),
                    new(body.Left, body.Top),
                    new(body.Right - 1, body.Top),
                    new(body.Right - 1, body.Bottom - 1)
                ]);
        }
        else if (index == hoveredTab)
        {
            graphics.FillRectangle(SystemBrushes.ControlLight, Rectangle.FromLTRB(tab.Left, tab.Top, tab.Right, baseline));
        }

        var cross = CrossBounds(index);
        var left = tab.Left + LogicalToDeviceUnits(textMargin);
        TextRenderer.DrawText(
            graphics,
            TabPages[index].Text,
            Font,
            Rectangle.FromLTRB(left, tab.Top, cross.Left, tab.Bottom),
            SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        PaintCross(graphics, cross, index == hoveredCross);
    }

    void PaintCross(Graphics graphics, Rectangle box, bool hovered)
    {
        var previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered)
        {
            graphics.FillEllipse(SystemBrushes.ControlDark, box);
        }

        var inset = (box.Width - LogicalToDeviceUnits(crossSize)) / 2f;
        var left = box.Left + inset;
        var top = box.Top + inset;
        var right = box.Right - inset;
        var bottom = box.Bottom - inset;
        using var pen = new Pen(hovered ? SystemColors.Window : SystemColors.GrayText, LogicalToDeviceUnits(10) / 8f);
        graphics.DrawLine(pen, left, top, right, bottom);
        graphics.DrawLine(pen, left, bottom, right, top);
        graphics.SmoothingMode = previous;
    }

    protected override void WndProc(ref Message message)
    {
        // Caught before the tab strip sees it, so clicking the cross of a background tab
        // closes that tab without first switching to it.
        if (message.Msg == leftButtonDownMessage)
        {
            var packed = unchecked((int)message.LParam.ToInt64());
            var location = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            var index = TabAt(location);
            if (index >= 0 && CrossBounds(index).Contains(location))
            {
                SetHovered(-1, -1);
                CloseRequested?.Invoke(TabPages[index]);
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnSelectedIndexChanged(EventArgs eventArgs)
    {
        base.OnSelectedIndexChanged(eventArgs);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        var tab = TabAt(eventArgs.Location);
        var overCross = tab >= 0 && CrossBounds(tab).Contains(eventArgs.Location);
        SetHovered(tab, overCross ? tab : -1);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        SetHovered(-1, -1);
    }

    void SetHovered(int tab, int cross)
    {
        if (tab == hoveredTab && cross == hoveredCross)
        {
            return;
        }

        hoveredTab = tab;
        hoveredCross = cross;
        Invalidate();
    }
}
