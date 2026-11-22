/// <summary>
/// The strip down the window's right edge, inside a repository: a tab for each of the Changes and
/// History panes that isn't pinned, its name written down the strip, as Visual Studio has its
/// windows that hide themselves. Hovering a tab slides its pane out; clicking one opens it with
/// the focus in it. Changes says how many files have changed.
/// </summary>
sealed class EdgeStrip : Control
{
    // Pixels at 100% scale.
    const int width = 28;
    const int padding = 12;
    const int gap = 4;

    // Which tabs show, in order, and what each says.
    Dictionary<GitPane, bool> shown = new()
    {
        [GitPane.Changes] = true,
        [GitPane.History] = true
    };

    int changes;
    GitPane? hovered;
    GitPane? open;
    StringFormat vertical = new(StringFormatFlags.DirectionVertical | StringFormatFlags.NoWrap);

    /// <summary>
    /// The pointer came onto a tab.
    /// </summary>
    public event Action<GitPane>? TabEntered;

    /// <summary>
    /// The pointer left the tab it was on.
    /// </summary>
    public event Action? TabLeft;

    public event Action<GitPane>? TabClicked;

    public EdgeStrip()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Dock = DockStyle.Right;
        Width = width;
        BackColor = ExplorerStyle.Window;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Changes and History";
    }

    /// <summary>
    /// Shows the tab of each pane that isn't pinned.
    /// </summary>
    public void ShowTabs(bool changesTab, bool historyTab)
    {
        if (shown[GitPane.Changes] == changesTab &&
            shown[GitPane.History] == historyTab)
        {
            return;
        }

        shown[GitPane.Changes] = changesTab;
        shown[GitPane.History] = historyTab;
        Invalidate();
    }

    /// <summary>
    /// How many files have changed, for the Changes tab to say.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Changes
    {
        get => changes;
        set
        {
            if (changes == value)
            {
                return;
            }

            changes = value;
            Invalidate();
        }
    }

    /// <summary>
    /// The pane slid out, whose tab is drawn as chosen.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GitPane? Open
    {
        get => open;
        set
        {
            if (open == value)
            {
                return;
            }

            open = value;
            Invalidate();
        }
    }

    /// <summary>
    /// What a pane's tab says.
    /// </summary>
    internal string TextOf(GitPane pane)
    {
        if (pane == GitPane.Changes &&
            changes > 0)
        {
            return $"Changes ({changes:N0})";
        }

        return pane.ToString();
    }

    /// <summary>
    /// Where each tab showing is, top down.
    /// </summary>
    internal List<(GitPane Pane, Rectangle Bounds)> Tabs()
    {
        var tabs = new List<(GitPane, Rectangle)>();
        var top = LogicalToDeviceUnits(gap);
        using var graphics = CreateGraphics();
        foreach (var (pane, isShown) in shown)
        {
            if (!isShown)
            {
                continue;
            }

            var length = (int) Math.Ceiling(graphics.MeasureString(TextOf(pane), Font, PointF.Empty, vertical).Height);
            var height = length + LogicalToDeviceUnits(padding) * 2;
            tabs.Add((pane, new(0, top, Width, height)));
            top += height + LogicalToDeviceUnits(gap);
        }

        return tabs;
    }

    /// <summary>
    /// Where a pane's tab is on the screen, for the pane to stay out while the pointer is on it.
    /// Empty when it doesn't show.
    /// </summary>
    public Rectangle ScreenBoundsOf(GitPane pane)
    {
        foreach (var (tab, bounds) in Tabs())
        {
            if (tab == pane)
            {
                return RectangleToScreen(bounds);
            }
        }

        return Rectangle.Empty;
    }

    GitPane? TabAt(Point point)
    {
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
        if (at == hovered)
        {
            return;
        }

        hovered = at;
        Invalidate();
        if (at is { } pane)
        {
            TabEntered?.Invoke(pane);
            return;
        }

        TabLeft?.Invoke();
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
        TabLeft?.Invoke();
    }

    protected override void OnMouseClick(MouseEventArgs args)
    {
        base.OnMouseClick(args);
        if (args.Button == MouseButtons.Left &&
            TabAt(args.Location) is { } pane)
        {
            TabClicked?.Invoke(pane);
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.Clear(BackColor);
        using (var divider = new Pen(ExplorerStyle.PaneDivider))
        {
            graphics.DrawLine(divider, 0, 0, 0, Height);
        }

        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        foreach (var (pane, bounds) in Tabs())
        {
            var inner = Rectangle.Inflate(bounds, -LogicalToDeviceUnits(3), 0);
            var text = ForeColor;
            if (pane == hovered ||
                pane == open)
            {
                var shade = ExplorerStyle.Highlighted(BackColor, pressed: pane == open && pane != hovered);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(shade);
                using var shape = ExplorerStyle.RoundedRectangle(inner, LogicalToDeviceUnits(4));
                graphics.FillPath(brush, shape);
                graphics.SmoothingMode = SmoothingMode.Default;
                text = ExplorerStyle.HighlightedText(text);
            }

            // A line down the open one's side, as a selected tab has along its edge.
            if (pane == open)
            {
                using var accent = new SolidBrush(ExplorerStyle.Accent);
                var line = LogicalToDeviceUnits(2);
                graphics.FillRectangle(accent, inner.Right - line, inner.Top + LogicalToDeviceUnits(padding) / 2, line, inner.Height - LogicalToDeviceUnits(padding));
            }

            using var textBrush = new SolidBrush(text);
            var size = graphics.MeasureString(TextOf(pane), Font, PointF.Empty, vertical);
            var x = bounds.Left + (bounds.Width - size.Width) / 2;
            var y = bounds.Top + LogicalToDeviceUnits(padding);
            graphics.DrawString(TextOf(pane), Font, textBrush, x, y, vertical);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new StripAccessibility(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            vertical.Dispose();
        }

        base.Dispose(disposing);
    }

    // A tab for each pane, for a screen reader, which Click opens.
    sealed class StripAccessibility(EdgeStrip strip) : ControlAccessibleObject(strip)
    {
        public override int GetChildCount() =>
            strip.Tabs().Count;

        public override AccessibleObject? GetChild(int index)
        {
            var tabs = strip.Tabs();
            if (index < 0 ||
                index >= tabs.Count)
            {
                return null;
            }

            return new TabAccessibility(strip, tabs[index].Pane, this);
        }
    }

    sealed class TabAccessibility(EdgeStrip strip, GitPane pane, AccessibleObject parent) : AccessibleObject
    {
        public override string Name => strip.TextOf(pane);
        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleObject Parent => parent;
        public override string DefaultAction => "Open";

        public override Rectangle Bounds => strip.ScreenBoundsOf(pane);

        public override void DoDefaultAction() =>
            strip.TabClicked?.Invoke(pane);
    }
}
