/// <summary>
/// The pages of a dialog down its left side, as Windows 11's Settings lists its own: an icon
/// and a name a row, the page shown shaded, with an accent bar at its left. Click a row, or use
/// the arrow keys, Home and End, to show its page.
/// </summary>
sealed class PageList : Control
{
    readonly List<(string Text, char Glyph)> pages = [];
    int selected = -1;
    int hovered = -1;

    /// <summary>
    /// Another page was picked.
    /// </summary>
    public event Action? SelectedChanged;

    public PageList()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
    }

    public void Add(string text, char glyph)
    {
        pages.Add((text, glyph));
        if (selected < 0)
        {
            selected = 0;
        }

        Invalidate();
    }

    public int Count => pages.Count;

    public string PageText(int index) =>
        pages[index].Text;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selected;
        set
        {
            value = Math.Clamp(value, 0, pages.Count - 1);
            if (value == selected)
            {
                return;
            }

            selected = value;
            Invalidate();
            SelectedChanged?.Invoke();
            if (IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.Selection, value);
                if (Focused)
                {
                    AccessibilityNotifyClients(AccessibleEvents.Focus, value);
                }
            }
        }
    }

    int RowHeight => LogicalToDeviceUnits(36);
    int IconSize => LogicalToDeviceUnits(16);
    int IconLeft => LogicalToDeviceUnits(14);
    int TextLeft => LogicalToDeviceUnits(44);

    Rectangle RowBounds(int index) =>
        new(0, index * RowHeight, Width, RowHeight);

    // The shaded part of a row, with a gap between rows and at the sides.
    Rectangle Shading(int index) =>
        Rectangle.Inflate(RowBounds(index), -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(2));

    int RowAt(Point point)
    {
        if (point.X < 0 ||
            point.X >= Width ||
            point.Y < 0)
        {
            return -1;
        }

        var index = point.Y / RowHeight;
        if (index >= pages.Count)
        {
            return -1;
        }

        return index;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var widest = 0;
        foreach (var (text, _) in pages)
        {
            widest = Math.Max(widest, TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width);
        }

        var width = Math.Max(LogicalToDeviceUnits(160), TextLeft + widest + LogicalToDeviceUnits(16));
        return new(width, pages.Count * RowHeight);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var graphics = args.Graphics;
        for (var index = 0; index < pages.Count; index++)
        {
            DrawRow(graphics, index);
        }
    }

    void DrawRow(Graphics graphics, int index)
    {
        var shading = Shading(index);
        var (text, glyph) = pages[index];
        if (index == selected || index == hovered)
        {
            // Settings' shades: a little darker than the background, the page shown more so.
            var amount = 0.035f;
            if (index == selected)
            {
                amount = 0.065f;
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(ExplorerStyle.Blend(BackColor, ForeColor, amount)))
            using (var path = ExplorerStyle.RoundedRectangle(shading, LogicalToDeviceUnits(4)))
            {
                graphics.FillPath(brush, path);
            }

            if (index == selected)
            {
                var barHeight = LogicalToDeviceUnits(16);
                var bar = new Rectangle(
                    shading.Left,
                    shading.Top + (shading.Height - barHeight) / 2,
                    LogicalToDeviceUnits(3),
                    barHeight);
                using var accent = new SolidBrush(ExplorerStyle.Accent);
                using var barPath = ExplorerStyle.RoundedRectangle(bar, bar.Width / 2);
                graphics.FillPath(accent, barPath);
            }

            graphics.SmoothingMode = SmoothingMode.Default;
        }

        var iconSize = IconSize;
        graphics.DrawImage(
            Icons.Bitmap(glyph, iconSize, ForeColor),
            shading.Left + IconLeft - LogicalToDeviceUnits(4),
            shading.Top + (shading.Height - iconSize) / 2);

        var textBounds = Rectangle.FromLTRB(TextLeft, shading.Top, shading.Right - LogicalToDeviceUnits(4), shading.Bottom);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            textBounds,
            ForeColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);

        if (index == selected &&
            Focused &&
            ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, shading);
        }
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        var index = RowAt(args.Location);
        if (index < 0)
        {
            return;
        }

        Focus();
        SelectedIndex = index;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        SetHovered(RowAt(args.Location));
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        SetHovered(-1);
    }

    void SetHovered(int index)
    {
        if (index == hovered)
        {
            return;
        }

        hovered = index;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        switch (args.KeyData)
        {
            case Keys.Up:
                SelectedIndex = selected - 1;
                break;
            case Keys.Down:
                SelectedIndex = selected + 1;
                break;
            case Keys.Home:
                SelectedIndex = 0;
                break;
            case Keys.End:
                SelectedIndex = pages.Count - 1;
                break;
            default:
                return;
        }

        args.Handled = true;
    }

    protected override void OnGotFocus(EventArgs args)
    {
        base.OnGotFocus(args);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        base.OnLostFocus(eventArgs);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new ListObject(this);

    // A list of the pages for screen readers, each an item that is selected when shown.
    sealed class ListObject(PageList owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() =>
            owner.pages.Count;

        public override AccessibleObject? GetChild(int index)
        {
            if (index < 0 ||
                index >= owner.pages.Count)
            {
                return null;
            }

            return new PageObject(owner, index);
        }

        public override AccessibleObject? GetSelected() =>
            GetChild(owner.selected);

        public override AccessibleObject? GetFocused()
        {
            if (!owner.Focused)
            {
                return null;
            }

            return GetChild(owner.selected);
        }
    }

    sealed class PageObject(PageList owner, int index) : AccessibleObject
    {
        public override string Name => owner.pages[index].Text;

        public override AccessibleRole Role => AccessibleRole.ListItem;

        public override AccessibleObject Parent => owner.AccessibilityObject;

        public override Rectangle Bounds => owner.RectangleToScreen(owner.RowBounds(index));

        public override string DefaultAction => "Select";

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (index == owner.selected)
                {
                    state |= AccessibleStates.Selected;
                    if (owner.Focused)
                    {
                        state |= AccessibleStates.Focused;
                    }
                }

                return state;
            }
        }

        public override void DoDefaultAction() =>
            owner.SelectedIndex = index;

        public override void Select(AccessibleSelection flags)
        {
            if (flags.HasFlag(AccessibleSelection.TakeFocus))
            {
                owner.Focus();
            }

            if (flags.HasFlag(AccessibleSelection.TakeSelection) ||
                flags.HasFlag(AccessibleSelection.AddSelection))
            {
                owner.SelectedIndex = index;
            }
        }
    }
}
