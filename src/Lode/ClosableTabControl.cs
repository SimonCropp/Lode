/// <summary>
/// A tab strip drawn as Explorer's: each tab carries a cross that closes it, and the selected
/// one, its top corners rounded, joins the page below it.
/// </summary>
sealed class ClosableTabControl : TabControl
{
    const int leftButtonDownMessage = 0x0201;

    // Pixels at 100% scale.
    const int crossBoxSize = 20;
    const int crossSize = 8;
    const int crossMargin = 5;
    const int crossCornerRadius = 4;
    const int textMargin = 8;
    const int horizontalPadding = 17;
    const int verticalPadding = 5;
    const int cornerRadius = 8;
    const int footRadius = 3;
    const int dividerHeight = 16;
    const int iconGap = 8;

    // Each tab's icon, from the system image list, drawn before its name as Explorer's tabs
    // have their folder's. Each icon is drawn to a bitmap once.
    readonly Dictionary<TabPage, int> icons = [];
    readonly Dictionary<int, Bitmap> iconImages = [];

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
        // still places the tabs, at the size FitTabs gives them.
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        ApplyPadding();
        SizeMode = TabSizeMode.Fixed;
        ShowToolTips = true;
    }

    // Set again when the screen's scale changes, so the room left for the cross scales with it.
    void ApplyPadding() =>
        Padding = new(LogicalToDeviceUnits(horizontalPadding), LogicalToDeviceUnits(verticalPadding));

    /// <summary>
    /// Shows the icon at <paramref name="systemIcon"/> in the system image list before the
    /// tab's name.
    /// </summary>
    public void SetIcon(TabPage page, int systemIcon)
    {
        if (icons.TryGetValue(page, out var current) &&
            current == systemIcon)
        {
            return;
        }

        icons[page] = systemIcon;

        // The first icon makes every tab wider, so they are fitted again once this is done,
        // rather than once they have been painted at their old width.
        QueueFit();
        Invalidate();
    }

    // Fitted before the tabs are first painted, which may be well after: a window off screen
    // isn't painted at all.
    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        QueueFit();
    }

    protected override void OnControlRemoved(ControlEventArgs eventArgs)
    {
        base.OnControlRemoved(eventArgs);
        if (eventArgs.Control is TabPage page)
        {
            icons.Remove(page);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var image in iconImages.Values)
            {
                image.Dispose();
            }

            iconImages.Clear();
        }

        base.Dispose(disposing);
    }

    Bitmap IconImage(int icon)
    {
        if (!iconImages.TryGetValue(icon, out var image))
        {
            image = ShellIcons.Render(icon, ShellIcons.SmallIconSize, Point.Empty);
            iconImages[icon] = image;
        }

        return image;
    }

    // The room an icon takes before a tab's name, its gap included. None while no tab has one.
    int IconRoom
    {
        get
        {
            if (icons.Count == 0)
            {
                return 0;
            }

            return ShellIcons.SmallIconSize.Width + LogicalToDeviceUnits(iconGap);
        }
    }

    // Windows would size each tab from its text, but it measures text its own way, which at
    // some display scales comes out narrower than the text as drawn here. So every tab gets the
    // same width, worked out here from the widest name as it is drawn, as browser tabs do.
    // Depends only on the names and whether there are icons, so setting it never makes it need
    // setting again.
    void FitTabs()
    {
        if (!IsHandleCreated || TabCount == 0)
        {
            return;
        }

        var widest = 0;
        for (var index = 0; index < TabCount; index++)
        {
            widest = Math.Max(widest, TextRenderer.MeasureText(TabPages[index].Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width);
        }

        var size = new Size(
            widest + IconRoom + LogicalToDeviceUnits(textMargin * 2 + crossBoxSize + crossMargin),
            Font.Height + LogicalToDeviceUnits(verticalPadding) * 2);
        if (ItemSize != size)
        {
            ItemSize = size;
        }
    }

    // Checked on every paint, since a tab's text changes without a layout. Resizing tabs from
    // inside a paint is asking for trouble, so that waits until painting is over.
    bool fitQueued;

    void QueueFit()
    {
        if (fitQueued || !IsHandleCreated)
        {
            return;
        }

        fitQueued = true;
        BeginInvoke(
            () =>
            {
                fitQueued = false;
                FitTabs();
            });
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        ApplyPadding();
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

    // The pages fill the control below the row of tabs, edge to edge, as Explorer's folder fills
    // its window below the address bar. The tab strip's own frame left a margin round them,
    // which showed as a gray border left of, right of and below the folder.
    public override Rectangle DisplayRectangle
    {
        get
        {
            var top = base.DisplayRectangle.Top;
            return new(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top));
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        QueueFit();
        var graphics = eventArgs.Graphics;
        graphics.Clear(SystemColors.Control);
        if (TabCount == 0)
        {
            return;
        }

        // The line the tabs sit on, along the top of the page. The selected tab is painted over
        // it, so it reads as joined to the page below.
        using var border = new Pen(ExplorerStyle.TabBorder);
        var baseline = DisplayRectangle.Top - 1;
        graphics.DrawLine(border, 0, baseline, Width, baseline);

        for (var index = 0; index < TabCount; index++)
        {
            if (index != SelectedIndex)
            {
                PaintTab(graphics, border, index, baseline);
            }
        }

        PaintDividers(graphics, border, baseline);
        if (SelectedIndex >= 0)
        {
            PaintTab(graphics, border, SelectedIndex, baseline);
        }
    }

    void PaintTab(Graphics graphics, Pen border, int index, int baseline)
    {
        var tab = GetTabRect(index);
        var background = SystemColors.Control;
        var radius = LogicalToDeviceUnits(cornerRadius);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (index == SelectedIndex)
        {
            background = SystemColors.Window;
            using var outline = SelectedOutline(tab, baseline, radius);

            // Filled down over the line the tabs sit on, which the outline only reaches.
            using var body = (GraphicsPath) outline.Clone();
            var foot = LogicalToDeviceUnits(footRadius);
            body.AddLine(tab.Right + foot, baseline + 1, tab.Left - foot, baseline + 1);
            body.CloseFigure();
            graphics.FillPath(SystemBrushes.Window, body);
            graphics.DrawPath(border, outline);
        }
        else if (index == hoveredTab)
        {
            background = ExplorerStyle.Hover(SystemColors.Control);
            using var brush = new SolidBrush(background);
            using var shape = TopRounded(Rectangle.FromLTRB(tab.Left, tab.Top, tab.Right, baseline), radius);
            graphics.FillPath(brush, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
        var cross = CrossBounds(index);
        var left = tab.Left + LogicalToDeviceUnits(textMargin);
        if (icons.TryGetValue(TabPages[index], out var icon))
        {
            var size = ShellIcons.SmallIconSize;
            graphics.DrawImage(IconImage(icon), new Rectangle(new(left, tab.Top + (tab.Height - size.Height) / 2), size));
            left += IconRoom;
        }

        TextRenderer.DrawText(
            graphics,
            TabPages[index].Text,
            Font,
            Rectangle.FromLTRB(left, tab.Top, cross.Left, tab.Bottom),
            SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        PaintCross(graphics, cross, index == hoveredCross, background);
    }

    // The selected tab's outline: up from a foot that curves out into the line the tabs sit on,
    // round the top corners, and down to a foot on the other side. Open along the bottom, where
    // the tab joins the page. Through the middle of the pixels it is drawn on, so it is crisp.
    GraphicsPath SelectedOutline(Rectangle tab, int baseline, int radius)
    {
        var left = tab.Left + 0.5f;
        var right = tab.Right - 0.5f;
        var top = tab.Top + 0.5f;
        var bottom = baseline + 0.5f;
        var corner = radius * 2f;
        var foot = LogicalToDeviceUnits(footRadius) * 2f;
        var path = new GraphicsPath();
        path.AddArc(left - foot, bottom - foot, foot, foot, 90, -90);
        path.AddArc(left, top, corner, corner, 180, 90);
        path.AddArc(right - corner, top, corner, corner, 270, 90);
        path.AddArc(right, bottom - foot, foot, foot, 180, -90);
        return path;
    }

    // A tab under the mouse: its top corners rounded as the selected tab's are.
    static GraphicsPath TopRounded(Rectangle bounds, int radius)
    {
        var corner = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, corner, corner, 180, 90);
        path.AddArc(bounds.Right - corner, bounds.Top, corner, corner, 270, 90);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
        path.CloseFigure();
        return path;
    }

    // A short line between two tabs where neither is selected or under the mouse, which have a
    // shape of their own to tell them apart.
    void PaintDividers(Graphics graphics, Pen border, int baseline)
    {
        for (var index = 0; index < TabCount - 1; index++)
        {
            if (index == SelectedIndex ||
                index + 1 == SelectedIndex ||
                index == hoveredTab ||
                index + 1 == hoveredTab)
            {
                continue;
            }

            var tab = GetTabRect(index);
            var height = LogicalToDeviceUnits(dividerHeight);
            var top = tab.Top + (baseline - tab.Top - height) / 2;
            graphics.DrawLine(border, tab.Right - 1, top, tab.Right - 1, top + height);
        }
    }

    // Under the mouse, a shade darker than the tab behind it, its corners rounded, as Explorer's
    // close button is.
    void PaintCross(Graphics graphics, Rectangle box, bool hovered, Color background)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hovered)
        {
            using var brush = new SolidBrush(ExplorerStyle.Hover(background));
            using var shape = ExplorerStyle.RoundedRectangle(box, LogicalToDeviceUnits(crossCornerRadius));
            graphics.FillPath(brush, shape);
        }

        var inset = (box.Width - LogicalToDeviceUnits(crossSize)) / 2f;
        var left = box.Left + inset;
        var top = box.Top + inset;
        var right = box.Right - inset;
        var bottom = box.Bottom - inset;
        using var pen = new Pen(SystemColors.ControlText, LogicalToDeviceUnits(10) / 8f);
        graphics.DrawLine(pen, left, top, right, bottom);
        graphics.DrawLine(pen, left, bottom, right, top);
        graphics.SmoothingMode = SmoothingMode.Default;
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
