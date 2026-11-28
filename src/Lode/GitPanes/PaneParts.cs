/// <summary>
/// The two panes on the window's right edge.
/// </summary>
enum GitPane
{
    Changes,
    History
}

/// <summary>
/// Where the Changes and History panes are, for how much room there is beside the tree.
/// </summary>
enum PaneLayout
{
    // Too little room for either beside the list: each is a tab on the strip down the window's
    // right edge, and slides out over the list.
    Narrow,

    // Room for one beside the list: the two share it, as tabs along its top.
    Medium,

    // Room for both beside the list, side by side.
    Wide
}

/// <summary>
/// How the tabs on the strip down the window's right edge are labelled.
/// </summary>
enum StripLabels
{
    // The pane's icon, and its name written down the strip, as Visual Studio has them.
    Vertical,

    // The pane's icon, and its name across under it, as JetBrains' IDEs have them.
    Horizontal,

    // The pane's icon alone.
    IconsOnly
}

//// <summary>
/// The top of a pane, drawn as GitHub Desktop's tab bar: the pane's icon and name, for Changes
/// how many files have changed in a grey pill, as Desktop's tab has, and Desktop's blue line under
/// the tab chosen. So each pane says which it is at a glance, and where History starts beside
/// Changes. Where the two share the room beside the list, it has a tab for each, Changes then
/// History, its own chosen; clicking the other shows that pane instead.
/// </summary>
sealed class PaneHeader : Panel
{
    // Pixels at 100% scale.
    const int iconSize = 16;

    const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

    Font bold;
    Font counter;
    GitPane own;
    bool tabbed;
    int changesCount;
    GitPane? hovered;

    /// <summary>
    /// The other pane's tab was clicked, for it to show instead.
    /// </summary>
    public event Action<GitPane>? TabClicked;

    public PaneHeader(GitPane own)
    {
        this.own = own;
        (bold, counter) = Fonts(Font);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ExplorerStyle.Window;
        Dock = DockStyle.Top;
        Height = 34;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = own.ToString();
    }

    // The name semibold, and the count in Desktop's 9 pixel semibold.
    static (Font Bold, Font Counter) Fonts(Font font) =>
        (DesktopStyle.Semibold(font, font.SizeInPoints), DesktopStyle.Semibold(font, Math.Max(6f, font.SizeInPoints * 0.75f)));

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        bold.Dispose();
        counter.Dispose();
        (bold, counter) = Fonts(Font);
        Invalidate();
    }

    /// <summary>
    /// The count in the pill after Changes' name, none when 0: how many files have changed.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ChangesCount
    {
        get => changesCount;
        set
        {
            if (changesCount == value)
            {
                return;
            }

            changesCount = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Whether it has a tab for each pane, its own chosen, rather than its own alone.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Tabbed
    {
        get => tabbed;
        set
        {
            if (tabbed == value)
            {
                return;
            }

            tabbed = value;
            hovered = null;
            Invalidate();
        }
    }

    static char GlyphOf(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return Icons.Changes;
        }

        return Icons.History;
    }

    // The tab chosen semibold, the other as the window's text is, as Desktop has them.
    Font FontOf(GitPane pane)
    {
        if (pane == own)
        {
            return bold;
        }

        return Font;
    }

    string? CountText(GitPane pane)
    {
        if (pane != GitPane.Changes ||
            changesCount <= 0)
        {
            return null;
        }

        return changesCount.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The tabs, left to right: the pane's own alone, or, tabbed, Changes then History. Each as
    /// wide as its icon, name and count, with the inset either side, none wider than what is left
    /// of the header.
    /// </summary>
    internal List<(GitPane Pane, Rectangle Bounds)> Tabs()
    {
        using var graphics = CreateGraphics();
        return Tabs(graphics);
    }

    List<(GitPane Pane, Rectangle Bounds)> Tabs(Graphics graphics)
    {
        List<GitPane> panes = [own];
        if (tabbed)
        {
            panes = [GitPane.Changes, GitPane.History];
        }

        var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
        var size = LogicalToDeviceUnits(iconSize);
        var tabs = new List<(GitPane, Rectangle)>();
        var left = 0;
        foreach (var pane in panes)
        {
            var width = inset + size + LogicalToDeviceUnits(6) + TextRenderer.MeasureText(graphics, pane.ToString(), FontOf(pane), Size.Empty, flags).Width;
            if (CountText(pane) is { } number)
            {
                width += LogicalToDeviceUnits(15) + TextRenderer.MeasureText(graphics, number, counter, Size.Empty, flags).Width;
            }

            width = Math.Clamp(width + inset, 0, Math.Max(0, Width - left));
            tabs.Add((pane, new(left, 0, width, Height)));
            left += width;
        }

        return tabs;
    }

    GitPane? TabAt(Point point)
    {
        if (!tabbed)
        {
            return null;
        }

        foreach (var (pane, bounds) in Tabs())
        {
            if (bounds.Contains(point))
            {
                return pane;
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var at = TabAt(args.Location);
        if (at == own)
        {
            at = null;
        }

        if (at == hovered)
        {
            return;
        }

        hovered = at;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        if (hovered == null)
        {
            return;
        }

        hovered = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs args)
    {
        base.OnMouseClick(args);
        if (args.Button == MouseButtons.Left &&
            TabAt(args.Location) is { } pane &&
            pane != own)
        {
            TabClicked?.Invoke(pane);
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        var inset = LogicalToDeviceUnits(DesktopStyle.Inset);
        var bar = LogicalToDeviceUnits(3);

        // What is drawn sits in the room above the line under it.
        var middle = (Height - bar) / 2;
        var size = LogicalToDeviceUnits(iconSize);
        var text = DesktopStyle.Text;
        var chosen = Rectangle.Empty;
        foreach (var (pane, bounds) in Tabs(graphics))
        {
            if (pane == own)
            {
                chosen = bounds;
            }

            if (pane == hovered)
            {
                using var hover = new SolidBrush(DesktopStyle.Hover);
                graphics.FillRectangle(hover, bounds.Left, 0, bounds.Width, Height - 1);
            }

            var font = FontOf(pane);
            graphics.DrawImageUnscaled(Icons.Bitmap(GlyphOf(pane), size, text), bounds.Left + inset, middle - size / 2);
            var left = bounds.Left + inset + size + LogicalToDeviceUnits(6);
            var room = Math.Max(0, bounds.Right - inset - left);
            var title = pane.ToString();
            var measured = TextRenderer.MeasureText(graphics, title, font, Size.Empty, flags);
            var titleWidth = Math.Min(measured.Width, room);
            TextRenderer.DrawText(graphics, title, font, new Rectangle(left, middle - measured.Height / 2, titleWidth, measured.Height), text, flags | TextFormatFlags.EndEllipsis);

            if (CountText(pane) is not { } number)
            {
                continue;
            }

            var numberSize = TextRenderer.MeasureText(graphics, number, counter, Size.Empty, flags);
            var pill = new Rectangle(
                left + titleWidth + LogicalToDeviceUnits(5),
                middle - numberSize.Height / 2 - LogicalToDeviceUnits(2),
                numberSize.Width + LogicalToDeviceUnits(10),
                numberSize.Height + LogicalToDeviceUnits(4));
            if (pill.Right <= bounds.Right)
            {
                DesktopStyle.DrawPill(graphics, pill, pill.Height / 2, DesktopStyle.Pill, number, counter, DesktopStyle.PillText);
            }
        }

        // The line along the foot, and the blue line under the tab chosen.
        using (var pen = new Pen(DesktopStyle.Border))
        {
            graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        using var blue = new SolidBrush(DesktopStyle.Blue);
        graphics.FillRectangle(blue, chosen.Left, Height - bar, chosen.Width, bar);
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new HeaderAccessibility(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bold.Dispose();
            counter.Dispose();
        }

        base.Dispose(disposing);
    }

    // A tab for each pane it has, for a screen reader, the other's Click showing it.
    sealed class HeaderAccessibility(PaneHeader header) : ControlAccessibleObject(header)
    {
        public override int GetChildCount() =>
            header.Tabs().Count;

        public override AccessibleObject? GetChild(int index)
        {
            var tabs = header.Tabs();
            if (index < 0 ||
                index >= tabs.Count)
            {
                return null;
            }

            return new TabAccessibility(header, tabs[index].Pane, this);
        }
    }

    sealed class TabAccessibility(PaneHeader header, GitPane pane, AccessibleObject parent) : AccessibleObject
    {
        public override string Name
        {
            get
            {
                if (header.CountText(pane) is { } number)
                {
                    return $"{pane} ({number})";
                }

                return pane.ToString();
            }
        }

        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleObject Parent => parent;
        public override string DefaultAction => "Show";

        public override AccessibleStates State
        {
            get
            {
                if (pane == header.own)
                {
                    return AccessibleStates.Selectable | AccessibleStates.Selected;
                }

                return AccessibleStates.Selectable;
            }
        }

        public override Rectangle Bounds
        {
            get
            {
                foreach (var (tab, bounds) in header.Tabs())
                {
                    if (tab == pane)
                    {
                        return header.RectangleToScreen(bounds);
                    }
                }

                return Rectangle.Empty;
            }
        }

        public override void DoDefaultAction()
        {
            if (pane != header.own)
            {
                header.TabClicked?.Invoke(pane);
            }
        }
    }
}

// <summary>
/// A button filled with GitHub Desktop's blue, as its Commit is, its text white, in two parts, the
/// second bold: "Commit 2 files to " and the branch. A shade lighter under the pointer, and faded
/// while it does nothing, as Desktop has it.
/// </summary>
sealed class AccentButton : Button
{
    bool hovered;
    Font bold;

    public AccentButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        bold = new(Font, FontStyle.Bold);
    }

    /// <summary>
    /// The bold part, after <see cref="Control.Text"/>.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Bold { get; set; } = "";

    /// <summary>
    /// Whether it is drawn as Windows draws a push button, for a dialog's buttons beside one,
    /// such as Cancel: a pixel in from each edge, its corners less rounded, so the two line up.
    /// Otherwise as Desktop's, filling its bounds.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool BesideButtons
    {
        get => besideButtons;
        set
        {
            besideButtons = value;
            Invalidate();
        }
    }

    bool besideButtons;

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

    protected override void OnEnabledChanged(EventArgs args)
    {
        base.OnEnabledChanged(args);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        var behind = Parent?.BackColor ?? ExplorerStyle.Window;
        graphics.Clear(behind);
        var fill = DesktopStyle.Blue;
        if (hovered &&
            Enabled)
        {
            fill = DesktopStyle.BlueHover;
        }

        var text = DesktopStyle.OnBlue;

        // Desktop's opacity of 0.6: the button, and its text, part way to what is behind it.
        if (!Enabled)
        {
            fill = ExplorerStyle.Blend(fill, behind, 0.4f);
            text = ExplorerStyle.Blend(text, behind, 0.4f);
        }

        if (besideButtons)
        {
            const int inset = ExplorerStyle.PushButtonInset;
            ExplorerStyle.FillButton(graphics, Rectangle.Inflate(ClientRectangle, -inset, -inset), fill, ExplorerStyle.PushButtonRadius);
        }
        else
        {
            ExplorerStyle.FillButton(graphics, ClientRectangle, fill, LogicalToDeviceUnits(DesktopStyle.Radius));
        }

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var plain = TextRenderer.MeasureText(graphics, Text, Font, Size.Empty, flags);
        var strong = TextRenderer.MeasureText(graphics, Bold, bold, Size.Empty, flags);
        var width = Math.Min(plain.Width + strong.Width, Width - LogicalToDeviceUnits(16));
        var x = (Width - width) / 2;
        var y = (Height - plain.Height) / 2;
        TextRenderer.DrawText(graphics, Text, Font, new Point(x, y), text, flags);
        var boldBounds = Rectangle.FromLTRB(x + plain.Width, y, Width - LogicalToDeviceUnits(8), y + strong.Height);
        TextRenderer.DrawText(graphics, Bold, bold, boldBounds, text, flags | TextFormatFlags.EndEllipsis);

        if (Focused &&
            ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -3, -3), text, fill);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bold.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A list of rows drawn by its owner, holding none itself: it is told how many there are and
/// draws each as it shows, as Find Results' list does. Through a buffer, so a refresh doesn't
/// flicker.
/// </summary>
sealed class RowList : ListView
{
    public RowList(int rowHeight)
    {
        DoubleBuffered = true;
        View = View.Details;
        HeaderStyle = ColumnHeaderStyle.None;
        FullRowSelect = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        BackColor = ExplorerStyle.Window;
        VirtualMode = true;
        OwnerDraw = true;
        Dock = DockStyle.Fill;
        Columns.Add("", 100);

        // A row as tall as asked: a list view's rows are as tall as its small icons.
        SmallImageList = new()
        {
            ImageSize = new(1, rowHeight)
        };
        RetrieveVirtualItem += (_, args) => args.Item = new("");
        DrawColumnHeader += (_, args) => args.DrawDefault = true;

        // Drawn as the row's one cell, not as the row: the theme fills a selected row with its
        // own shade after the row is drawn, and before its cells are.
        DrawItem += (_, _) => { };
        DrawSubItem += (_, args) => DrawRow?.Invoke(args.Graphics, args.Bounds, args.ItemIndex);
        Resize += (_, _) => FitColumn();
        HandleCreated += (_, _) =>
        {
            _ = SetWindowTheme(Handle, ExplorerStyle.ControlTheme, null);
            FitColumn();
        };
    }

    /// <summary>
    /// Draws a row, in the bounds given, by its index.
    /// </summary>
    public event Action<Graphics, Rectangle, int>? DrawRow;

    /// <summary>
    /// How many rows there are, the column fitted to them.
    /// </summary>
    public void SetRows(int count)
    {
        // Back at the top before every row goes: a list scrolled down that loses them all is left
        // as far above its first row as it was below it, its scrollbar at the top, and once it has
        // rows again draws them that far down, or past the bottom, with nothing above them.
        if (count == 0 &&
            VirtualListSize > 0 &&
            IsHandleCreated)
        {
            EnsureVisible(0);
        }

        VirtualListSize = count;
        FitColumn();
    }

    // The one column across the list, less a scrollbar's room when the rows run past the
    // bottom: as wide as the list, a scrollbar coming would bring one along the bottom too.
    void FitColumn()
    {
        if (Columns.Count == 0)
        {
            return;
        }

        // From the list's own height, not its client area's, which a scrollbar along the bottom
        // takes from: the width set here would bring that scrollbar and take it away again,
        // round and round.
        var width = Width;
        var rowHeight = RowHeight();

        // The scrollbar as wide as on the screen the list is on, not the one Windows started on.
        if (VirtualListSize * rowHeight > Height)
        {
            width -= SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        }

        width = Math.Max(1, width);
        if (Columns[0].Width != width)
        {
            Columns[0].Width = width;
        }
    }

    const int listViewGetItemRect = 0x100E;

    /// <summary>
    /// How tall a row is as the list lays them out, or, with none laid out yet, as tall as asked.
    /// Asked of the list itself rather than through GetItemRect: given its rows while hidden, as
    /// the stashes under Changes' files are, the list is made and sized when it is shown, and told
    /// how many rows there are only after, so it has no first row then, and GetItemRect threw for
    /// it, from the handle being made and the size changing.
    /// </summary>
    public int RowHeight()
    {
        var height = SmallImageList?.ImageSize.Height ?? 1;
        if (!IsHandleCreated ||
            VirtualListSize == 0)
        {
            return height;
        }

        // LVIR_BOUNDS, the whole row, in Left.
        var bounds = new NativeRect();
        if (SendMessage(Handle, listViewGetItemRect, IntPtr.Zero, ref bounds) == IntPtr.Zero)
        {
            return height;
        }

        return Math.Max(height, bounds.Bottom - bounds.Top);
    }

    /// <summary>
    /// The rows selected, in order.
    /// </summary>
    public List<int> Selected()
    {
        var rows = new List<int>(SelectedIndices.Count);
        foreach (int index in SelectedIndices)
        {
            rows.Add(index);
        }

        return rows;
    }

    /// <summary>
    /// Whether the row under the pointer is shaded, as Desktop's lists of files shade it. Its list
    /// of commits doesn't.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShadesHovered { get; set; } = true;

    int hovered = -1;

    /// <summary>
    /// The row under the pointer, -1 for none.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Hovered
    {
        get => hovered;
        set
        {
            if (hovered == value)
            {
                return;
            }

            InvalidateRow(hovered);
            hovered = value;
            InvalidateRow(hovered);
        }
    }

    void InvalidateRow(int index)
    {
        if (index >= 0 &&
            index < VirtualListSize &&
            IsHandleCreated)
        {
            Invalidate(GetItemRect(index));
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (ShadesHovered)
        {
            Hovered = HitTest(args.Location).Item?.Index ?? -1;
        }
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        Hovered = -1;
    }

    const int leftButtonDown = 0x0201;
    const int leftButtonUp = 0x0202;

    // A mouse message's MK_SHIFT and MK_CONTROL: the keys held with the button.
    const nint shiftHeld = 0x0004;
    const nint controlHeld = 0x0008;

    /// <summary>
    /// Whether a click on the row already picked, the only one, picks none, as in History, where
    /// that puts the commit's files away. Not with Ctrl or Shift held, which pick as they always do.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DeselectsOnClick { get; set; }

    /// <summary>
    /// A click on the row already picked picked none. See <see cref="DeselectsOnClick"/>.
    /// </summary>
    public event Action? Deselected;

    /// <summary>
    /// What a click does at a point of a row that is a button rather than part of the row, the
    /// arrow of a commit not pushed say: null where there is none. Pressed and let go on it, it
    /// does that, and the row is neither picked nor put away.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Point, Action?>? ButtonAt { get; set; }

    // The row whose button was pressed, to act once the button comes up on it. -1 while none.
    int pressedButton = -1;

    // A press, let go or double-click on a button, kept from the list, so it picks nothing. A
    // double-click is a second press, which acts on nothing, else two clicks asked twice.
    bool OnButton(int message, Point point)
    {
        if (ButtonAt == null)
        {
            return false;
        }

        if (message == leftButtonUp)
        {
            var pressed = pressedButton;
            pressedButton = -1;
            if (pressed < 0)
            {
                return false;
            }

            if (HitTest(point).Item?.Index == pressed &&
                ButtonAt(point) is { } act)
            {
                act();
            }

            return true;
        }

        if (message != leftButtonDown &&
            message != leftButtonDoubleClick)
        {
            return false;
        }

        pressedButton = -1;
        if (ButtonAt(point) == null ||
            HitTest(point).Item is not { } item)
        {
            return false;
        }

        if (message == leftButtonDown)
        {
            pressedButton = item.Index;
        }

        return true;
    }

    const int leftButtonDoubleClick = 0x0203;
    const int timerMessage = 0x0113;

    // The list's own timer, comctl32's IDT_NAMEEDIT, set by a click on the row already picked:
    // once the double-click time has passed with no second click, it picks that row again, and
    // starts renaming it where rows can be renamed. Left to run, it picked the commit again a
    // moment after the click had picked none.
    const int clickedPickedTimer = 42;

    // The row pressed while it was the one picked, to pick none once the button comes up on it.
    int pressedPicked = -1;

    // A click picked none, and the list's timer is yet to pick the row again.
    bool unpicked;

    const int nonClientCalcSize = 0x0083;

    protected override void WndProc(ref Message message)
    {
        // No scrollbar along the bottom, as its frame is measured: the column is fitted across,
        // so there is nothing to scroll to, but the list brought one whenever the column was
        // wider than the room for a moment, as more rows than fit came or the list narrowed, and
        // kept it once the column fitted.
        if (message.Msg == nonClientCalcSize &&
            IsHandleCreated)
        {
            var style = GetWindowLong(Handle, WindowStyle);
            if ((style & WindowStyleHorizontalScroll) != 0)
            {
                SetWindowLong(Handle, WindowStyle, style & ~WindowStyleHorizontalScroll);
            }
        }

        if (message.Msg is leftButtonDown or leftButtonUp or leftButtonDoubleClick &&
            OnButton(message.Msg, PointOf(message.LParam)))
        {
            return;
        }

        if (!DeselectsOnClick)
        {
            base.WndProc(ref message);
            return;
        }

        if (message.Msg == timerMessage &&
            unpicked &&
            message.WParam == clickedPickedTimer)
        {
            unpicked = false;
            KillTimer(Handle, clickedPickedTimer);
            return;
        }

        if (message.Msg == leftButtonDoubleClick)
        {
            unpicked = false;
        }

        if (message.Msg != leftButtonDown &&
            message.Msg != leftButtonUp)
        {
            base.WndProc(ref message);
            return;
        }

        var point = PointOf(message.LParam);
        if (message.Msg == leftButtonUp)
        {
            base.WndProc(ref message);
            ReleasedOn(point);
            return;
        }

        pressedPicked = -1;
        unpicked = false;
        // Ctrl and Shift as they were when the button went down, which the press carries. Read
        // from the keyboard instead, they could be a key held in another window, and the click
        // left the commit picked.
        if ((message.WParam & (controlHeld | shiftHeld)) == 0 &&
            HitTest(point).Item is { } item &&
            SelectedIndices.Count == 1 &&
            SelectedIndices.Contains(item.Index))
        {
            pressedPicked = item.Index;
        }

        // The list doesn't come back from a press until the button is up, or a drag starts.
        base.WndProc(ref message);
        if ((MouseButtons & MouseButtons.Left) == 0)
        {
            ReleasedOn(point);
        }
    }

    void ReleasedOn(Point point)
    {
        var pressed = pressedPicked;
        pressedPicked = -1;
        if (pressed < 0 ||
            HitTest(point).Item?.Index != pressed ||
            !SelectedIndices.Contains(pressed))
        {
            return;
        }

        SelectedIndices.Clear();
        unpicked = true;
        Deselected?.Invoke();
    }

    static Point PointOf(IntPtr lParam) =>
        new((short) (lParam & 0xFFFF), (short) ((lParam >> 16) & 0xFFFF));

    /// <summary>
    /// A row's background, as Desktop draws it: blue when picked in a list with the focus, a grey
    /// when picked in one without, the folder no longer dimmed, and a lighter grey under the
    /// pointer. The system's highlight when picked with high contrast on. The colours for text on
    /// it follow, and whether it is drawn as text on the blue is, for icons drawn in the text's
    /// colour rather than their own.
    /// </summary>
    public (Color Background, Color Text, Color Secondary, bool OnBlue) Colours(int index)
    {
        var selected = SelectedIndices.Contains(index);
        if (selected &&
            (Focused || SystemInformation.HighContrast))
        {
            return (DesktopStyle.Blue, DesktopStyle.OnBlue, DesktopStyle.OnBlue, true);
        }

        if (index == hovered &&
            ShadesHovered)
        {
            return (DesktopStyle.Hover, DesktopStyle.Text, DesktopStyle.SecondaryText, false);
        }

        if (selected)
        {
            return (DesktopStyle.SelectedUnfocused, DesktopStyle.Text, DesktopStyle.Text, false);
        }

        return (DesktopStyle.Background, DesktopStyle.Text, DesktopStyle.SecondaryText, false);
    }

    /// <summary>
    /// The line along the foot of every row, as Desktop has between them.
    /// </summary>
    public static void DrawRowLine(Graphics graphics, Rectangle bounds)
    {
        using var pen = new Pen(DesktopStyle.Border);
        graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SmallImageList?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A list's tooltip that follows the pointer from row to row, and from part to part of a row,
/// each shown once the pointer rests on it. A tooltip set on the list as a whole, its text changed
/// as the pointer goes from row to row, showed only now and then: Windows takes the list for one
/// thing, whose tip it shows once the pointer comes onto it, and not again until it leaves.
/// </summary>
sealed class RowTip : IDisposable
{
    ToolTip tip = new();
    Timer wait = new();
    Control list;
    Func<Point, string?> textAt;
    string? pending;
    bool visible;

    /// <param name="textAt">The tip at a point of the list, null for none.</param>
    public RowTip(Control list, Func<Point, string?> textAt)
    {
        this.list = list;
        this.textAt = textAt;
        wait.Tick += (_, _) => Show();
        list.MouseMove += (_, args) => Moved(args.Location);
        list.MouseLeave += (_, _) => Hide();
        list.MouseDown += (_, _) => Hide();
        list.MouseWheel += (_, _) => Hide();
        list.KeyDown += (_, _) => Hide();
    }

    /// <summary>
    /// The tip waiting to show, or showing. For tests.
    /// </summary>
    internal string? Pending => pending;

    internal void Moved(Point point)
    {
        var text = textAt(point);
        if (text == pending)
        {
            return;
        }

        Hide();
        pending = text;
        if (text == null)
        {
            return;
        }

        wait.Interval = Math.Max(1, tip.InitialDelay);
        wait.Start();
    }

    void Show()
    {
        wait.Stop();
        if (pending == null ||
            !list.IsHandleCreated ||
            !list.ClientRectangle.Contains(list.PointToClient(Cursor.Position)))
        {
            return;
        }

        // Below the pointer, as Windows puts a tooltip, so it is never under it.
        var at = list.PointToClient(Cursor.Position);
        at.Y += list.LogicalToDeviceUnits(20);
        tip.Show(pending, list, at, tip.AutoPopDelay);
        visible = true;
    }

    void Hide()
    {
        wait.Stop();
        pending = null;
        if (visible)
        {
            tip.Hide(list);
            visible = false;
        }
    }

    public void Dispose()
    {
        wait.Dispose();
        tip.Dispose();
    }
}

/// <summary>
/// A pane's right-click menu, as QuickMenu builds it, what is picked running once it has closed.
/// The pane is told while it is open, so it doesn't slide away from under it.
/// </summary>
static class PaneMenu
{
    public static void Show(Control owner, Point location, IReadOnlyList<QuickMenu.Command?> commands, Action<bool> open)
    {
        if (commands.Count == 0)
        {
            return;
        }

        Action? chosen = null;
        var strip = QuickMenu.Build(commands, null, _ => chosen = _);
        open(true);
        strip.Closed += (_, _) => strip.BeginInvoke(
            () =>
            {
                strip.Dispose();
                open(false);
                chosen?.Invoke();
            });
        strip.Show(owner, location);
    }
}

/// <summary>
/// A text box of several lines drawn as the address bar is: the rounded background, and the
/// accent line along the bottom while it has the focus.
/// </summary>
sealed class FramedArea : Control
{
    public FrameTextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None,
        Multiline = true,
        AcceptsReturn = true,
        Dock = DockStyle.Fill
    };

    public FramedArea()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Padding = new(6, 5, 4, 5);
        Box.BackColor = ExplorerStyle.Window;
        Box.Enter += (_, _) => Invalidate();
        Box.Leave += (_, _) => Invalidate();
        Controls.Add(Box);
    }

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

            // Desktop's 5 pixels round the text, and one for the border and one for the ring.
            Padding = value ? new(7, 6, 6, 6) : new(6, 5, 4, 5);
            Invalidate();
        }
    }

    bool desktop;

    // The box docked inside the padding. A Control, unlike a Panel, docks its children over all
    // of itself, padding or not, so the box covered the frame, its border and its rounded corners,
    // and its text ran from the very edge.
    public override Rectangle DisplayRectangle
    {
        get
        {
            var bounds = ClientRectangle;
            return Rectangle.FromLTRB(
                bounds.Left + Padding.Left,
                bounds.Top + Padding.Top,
                Math.Max(bounds.Left + Padding.Left, bounds.Right - Padding.Right),
                Math.Max(bounds.Top + Padding.Top, bounds.Bottom - Padding.Bottom));
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        if (desktop)
        {
            TextFrame.DrawDesktop(args.Graphics, this, Box.Focused);
            return;
        }

        TextFrame.Draw(args.Graphics, this, Box.Focused);
    }
}

/// <summary>
/// Where a tick is drawn, and how: an accent box with a tick in it, an empty box, or, for some of
/// several, a dash.
/// </summary>
static class Ticks
{
    // Pixels at 100% scale: near Desktop's 13 pixel tick boxes.
    public const int Size = 14;

    public static void Draw(Graphics graphics, Control control, Rectangle box, bool? ticked, bool enabled)
    {
        var radius = control.LogicalToDeviceUnits(3);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = DesktopStyle.Tick;
        var mark = Color.White;
        if (SystemInformation.HighContrast)
        {
            accent = SystemColors.Highlight;
            mark = SystemColors.HighlightText;
        }

        if (!enabled)
        {
            accent = ExplorerStyle.Blend(accent, ExplorerStyle.Window, 0.55f);
        }

        if (ticked == false)
        {
            using var outline = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(box.Left, box.Top, box.Right - 1, box.Bottom - 1), radius);
            using (var inside = new SolidBrush(DesktopStyle.Background))
            {
                // White inside on the blue of a row picked, as a tick box is.
                graphics.FillPath(inside, outline);
            }

            using var pen = new Pen(DesktopStyle.BoxBorder);
            graphics.DrawPath(pen, outline);
            graphics.SmoothingMode = SmoothingMode.Default;
            return;
        }

        using (var shape = ExplorerStyle.RoundedRectangle(box, radius))
        using (var brush = new SolidBrush(accent))
        {
            graphics.FillPath(brush, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
        if (ticked == true)
        {
            var size = box.Width - control.LogicalToDeviceUnits(4);
            graphics.DrawImage(Icons.Bitmap(Icons.CheckMark, size, mark), box.Left + (box.Width - size) / 2, box.Top + (box.Height - size) / 2, size, size);
            return;
        }

        using var dash = new SolidBrush(mark);
        var thickness = Math.Max(2, control.LogicalToDeviceUnits(2));
        var inset = control.LogicalToDeviceUnits(4);
        graphics.FillRectangle(dash, box.Left + inset, box.Top + (box.Height - thickness) / 2, box.Width - inset * 2, thickness);
    }
}
