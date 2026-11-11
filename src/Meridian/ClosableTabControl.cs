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

    // The tab under a left button press, and where it was pressed, until the button is
    // released. Moving far enough with the button down turns the press into a drag.
    int pressedTab = -1;
    Point pressedAt;
    bool dragging;

    public event Action<TabPage>? CloseRequested;

    /// <summary>
    /// A tab was dragged and released away from the row of tabs. The point is on the screen.
    /// </summary>
    public event Action<TabPage, Point>? TabDraggedOff;

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
                new(body.Left, body.Bottom - 1), new(body.Left, body.Top), new(body.Right - 1, body.Top), new(body.Right - 1, body.Bottom - 1));
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

    // The strip the tabs sit in, above the pages.
    Rectangle TabRow => new(0, 0, Width, DisplayRectangle.Top);

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button == MouseButtons.Left)
        {
            pressedTab = TabAt(eventArgs.Location);
            pressedAt = eventArgs.Location;
        }
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        var page = dragging && pressedTab < TabCount ? TabPages[pressedTab] : null;
        EndDrag();
        if (page != null &&
            !TabRow.Contains(eventArgs.Location))
        {
            TabDraggedOff?.Invoke(page, PointToScreen(eventArgs.Location));
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);

        // Another window took the mouse (Alt+Tab, say): give up on the drag.
        if (dragging && !Capture)
        {
            EndDrag();
        }
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (dragging &&
            eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            EndDrag();
        }
    }

    void EndDrag()
    {
        var wasDragging = dragging;
        dragging = false;
        pressedTab = -1;
        if (wasDragging)
        {
            Capture = false;
            Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        // A press lasts until its release (see OnMouseUp), or until the pointer leaves before a
        // drag starts, so the button needs no checking here.
        if (pressedTab >= 0)
        {
            if (!dragging)
            {
                var threshold = SystemInformation.DragSize;
                var still = new Rectangle(
                    pressedAt.X - threshold.Width / 2,
                    pressedAt.Y - threshold.Height / 2,
                    threshold.Width,
                    threshold.Height);
                if (still.Contains(eventArgs.Location))
                {
                    return;
                }

                // Captured, so the release arrives here even when it happens over another window.
                dragging = true;
                Capture = true;
                SetHovered(-1, -1);
            }

            Cursor = TabRow.Contains(eventArgs.Location) ? Cursors.Default : Cursors.SizeAll;
            return;
        }

        var tab = TabAt(eventArgs.Location);
        var overCross = tab >= 0 &&
                        CrossBounds(tab).Contains(eventArgs.Location);
        SetHovered(tab, overCross ? tab : -1);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        SetHovered(-1, -1);
        if (!dragging)
        {
            pressedTab = -1;
        }
    }

    void SetHovered(int tab, int cross)
    {
        if (tab == hoveredTab &&
            cross == hoveredCross)
        {
            return;
        }

        hoveredTab = tab;
        hoveredCross = cross;
        Invalidate();
    }
}
