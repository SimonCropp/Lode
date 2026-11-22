/// <summary>
/// A tab strip drawn as Explorer's: each tab carries a cross that closes it, and the selected
/// one, its top corners rounded, joins the page below it. A tab dragged along the row moves
/// along it; dragged off the row, it is handed over (<see cref="TabDraggedOff"/>).
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
    Dictionary<TabPage, int> icons = [];
    Dictionary<int, Bitmap> iconImages = [];

    int hoveredTab = -1;
    int hoveredCross = -1;

    // The tab under a left button press, and where it was pressed, until the button is
    // released. Moving far enough with the button down turns the press into a drag. The page,
    // not its index, so a tab closed meanwhile, by Ctrl+W say, doesn't move the drag onto
    // another. Its index is where it goes back to if the drag is given up.
    TabPage? pressedTab;
    int pressedIndex;
    Point pressedAt;
    bool dragging;

    // While tabs are being put in another order: a tab taken out is going straight back in.
    bool moving;

    public event Action<TabPage>? CloseRequested;

    /// <summary>
    /// A tab was dragged and released away from the row of tabs. The point is on the screen.
    /// </summary>
    public event Action<TabPage, Point>? TabDraggedOff;

    /// <summary>
    /// Where on the screen a tab being dragged off the row is, as it moves, for what it would be
    /// dropped on to show it; null once it is back on the row, dropped or given up on.
    /// </summary>
    public event Action<Point?>? TabDraggingOff;

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
    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        QueueFit();
    }

    protected override void OnControlRemoved(ControlEventArgs args)
    {
        base.OnControlRemoved(args);
        if (!moving &&
            args.Control is TabPage page)
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

    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
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

    // The tab across from a point along the row, or the first or last beyond them all. Tabs
    // scrolled off to the left are left of every point.
    int TabAlong(int x)
    {
        for (var index = 0; index < TabCount; index++)
        {
            if (x < GetTabRect(index).Right)
            {
                return index;
            }
        }

        return TabCount - 1;
    }

    /// <summary>
    /// Puts a tab at <paramref name="index"/>, the rest keeping their order. Taken out while
    /// selected, a tab would have Windows Forms select the first, which would show, read its
    /// folder and have the tree walk to it. So the tab selected stays in, and the tabs it passes
    /// go round to its other side.
    /// </summary>
    public void MoveTab(TabPage page, int index)
    {
        var from = TabPages.IndexOf(page);
        if (from < 0 ||
            from == index)
        {
            return;
        }

        moving = true;
        try
        {
            if (page != SelectedTab)
            {
                Reinsert(page, index);
            }
            else
            {
                for (; from < index; from++)
                {
                    Reinsert(TabPages[from + 1], from);
                }

                for (; from > index; from--)
                {
                    Reinsert(TabPages[from - 1], from);
                }
            }
        }
        finally
        {
            moving = false;
        }

        Invalidate();
    }

    void Reinsert(TabPage page, int index)
    {
        TabPages.Remove(page);
        TabPages.Insert(index, page);
    }

    /// <summary>
    /// Where a tab from another window, dropped at a point on the screen, goes among these:
    /// on the row, before the first tab whose middle is past the point; anywhere else, at the
    /// end.
    /// </summary>
    public int DropIndex(Point screen)
    {
        var location = PointToClient(screen);
        if (TabRow.Contains(location))
        {
            for (var index = 0; index < TabCount; index++)
            {
                var tab = GetTabRect(index);
                if (location.X < tab.Left + tab.Width / 2)
                {
                    return index;
                }
            }
        }

        return TabCount;
    }

    /// <summary>
    /// The icon a tab shows, or null for none. For tests.
    /// </summary>
    internal int? IconOf(TabPage page)
    {
        if (icons.TryGetValue(page, out var icon))
        {
            return icon;
        }

        return null;
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

    protected override void OnPaint(PaintEventArgs args)
    {
        QueueFit();
        PaintTabs(args.Graphics);
    }

    /// <summary>
    /// Paints the row of tabs, and the line they sit on.
    /// </summary>
    internal void PaintTabs(Graphics graphics)
    {
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

        // Windows Forms counts a tab taken out gone before it tells the window, so a paint
        // between the two, as the selected tab is dragged to another window say, finds the window
        // still selecting it, past the tabs counted. Then none is painted as selected.
        var selected = SelectedIndex;
        if (selected >= TabCount)
        {
            selected = -1;
        }

        for (var index = 0; index < TabCount; index++)
        {
            if (index != selected)
            {
                PaintTab(graphics, border, index, baseline);
            }
        }

        PaintDividers(graphics, border, baseline);
        if (selected >= 0)
        {
            PaintTab(graphics, border, selected, baseline);
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
            background = ExplorerStyle.Window;
            using var outline = SelectedOutline(tab, baseline, radius);

            // Filled down over the line the tabs sit on, which the outline only reaches.
            using var body = (GraphicsPath) outline.Clone();
            var foot = LogicalToDeviceUnits(footRadius);
            body.AddLine(tab.Right + foot, baseline + 1, tab.Left - foot, baseline + 1);
            body.CloseFigure();
            using var window = new SolidBrush(ExplorerStyle.Window);
            graphics.FillPath(window, body);
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
            if (index >= 0 &&
                CrossBounds(index).Contains(location))
            {
                SetHovered(-1, -1);
                CloseRequested?.Invoke(TabPages[index]);
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnSelectedIndexChanged(EventArgs args)
    {
        base.OnSelectedIndexChanged(args);
        Invalidate();
    }

    // The strip the tabs sit in, above the pages.
    Rectangle TabRow => new(0, 0, Width, DisplayRectangle.Top);

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button == MouseButtons.Left)
        {
            pressedTab = null;
            var index = TabAt(args.Location);
            if (index >= 0)
            {
                pressedTab = TabPages[index];
            }

            pressedIndex = index;
            pressedAt = args.Location;
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        var page = pressedTab;
        var wasDragging = dragging;
        EndDrag();
        if (wasDragging &&
            page != null &&
            TabPages.Contains(page) &&
            !TabRow.Contains(args.Location))
        {
            TabDraggedOff?.Invoke(page, PointToScreen(args.Location));
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs args)
    {
        base.OnMouseCaptureChanged(args);

        // Another window took the mouse (Alt+Tab, say): give up on the drag.
        if (dragging &&
            !Capture)
        {
            CancelDrag();
        }
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (dragging &&
            args.KeyCode == Keys.Escape)
        {
            args.Handled = true;
            CancelDrag();
        }
    }

    // Given up on, a drag puts its tab back where it was, as near as the tabs closed meanwhile
    // allow.
    void CancelDrag()
    {
        var page = pressedTab;
        EndDrag();
        if (page != null)
        {
            MoveTab(page, Math.Min(pressedIndex, TabCount - 1));
        }
    }

    void EndDrag()
    {
        var wasDragging = dragging;
        dragging = false;
        pressedTab = null;
        if (wasDragging)
        {
            Capture = false;
            Cursor = Cursors.Default;
            TabDraggingOff?.Invoke(null);
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        // A press lasts until its release (see OnMouseUp), or until the pointer leaves before a
        // drag starts, so the button needs no checking here.
        if (pressedTab != null)
        {
            if (!dragging)
            {
                var threshold = SystemInformation.DragSize;
                var still = new Rectangle(
                    pressedAt.X - threshold.Width / 2,
                    pressedAt.Y - threshold.Height / 2,
                    threshold.Width,
                    threshold.Height);
                if (still.Contains(args.Location))
                {
                    return;
                }

                // Captured, so the release arrives here even when it happens over another window.
                dragging = true;
                Capture = true;
                SetHovered(-1, -1);
            }

            if (!TabRow.Contains(args.Location))
            {
                Cursor = Cursors.SizeAll;
                TabDraggingOff?.Invoke(PointToScreen(args.Location));
                return;
            }

            // Along the row, the tab goes where the pointer is, past the tabs it crosses. They
            // are all as wide, so it is then under the pointer, and stays put until it moves on.
            Cursor = Cursors.Default;
            TabDraggingOff?.Invoke(null);
            MoveTab(pressedTab, TabAlong(args.X));
            return;
        }

        var tab = TabAt(args.Location);
        var overCross = tab >= 0 &&
                        CrossBounds(tab).Contains(args.Location);
        SetHovered(tab, overCross ? tab : -1);
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        SetHovered(-1, -1);
        if (!dragging)
        {
            pressedTab = null;
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
