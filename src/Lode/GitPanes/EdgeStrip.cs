/// <summary>
/// The strip down the window's right edge, inside a repository: a tab for each of the Changes and
/// History panes that isn't pinned, as Visual Studio has its windows that hide themselves. Each
/// tab has the pane's icon, and its name written down the strip, across under the icon, or not at
/// all, as chosen from the strip's right-click menu. Hovering a tab slides its pane out; clicking
/// one opens it with the focus in it. Changes says how many files have changed: in its name, or,
/// where the name isn't written down the strip, in a badge on its icon.
/// </summary>
sealed class EdgeStrip : Control
{
    // Pixels at 100% scale.
    const int leastWidth = 28;
    const int iconSize = 16;
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
    StripLabels labels;
    StringFormat vertical = new(StringFormatFlags.DirectionVertical | StringFormatFlags.NoWrap);

    // The names written across, under the icons, smaller than the window's text as JetBrains'
    // are, and the count in the badge on Changes' icon.
    Font small;
    Font badgeFont;

    /// <summary>
    /// The pointer came onto a tab.
    /// </summary>
    public event Action<GitPane>? TabEntered;

    /// <summary>
    /// The pointer left the tab it was on.
    /// </summary>
    public event Action? TabLeft;

    public event Action<GitPane>? TabClicked;

    /// <summary>
    /// How the tabs are to be labelled was chosen from the strip's right-click menu.
    /// </summary>
    public event Action<StripLabels>? LabelsChosen;

    public EdgeStrip()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Dock = DockStyle.Right;
        Width = leastWidth;
        BackColor = ExplorerStyle.Window;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Changes and History";
        (small, badgeFont) = Fonts(Font);
    }

    static (Font Small, Font Badge) Fonts(Font font) =>
        (new(font.FontFamily, Math.Max(7f, font.SizeInPoints - 1)), new(font.FontFamily, Math.Max(6f, font.SizeInPoints - 2), FontStyle.Bold));

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
        Fit();
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
    /// How the tabs are labelled, the strip as wide as they need.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StripLabels Labels
    {
        get => labels;
        set
        {
            if (labels == value)
            {
                return;
            }

            labels = value;
            Fit();
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
    /// What a pane's tab says, to a screen reader, and written down the strip.
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
    /// What the badge on Changes' icon says: how many files have changed, up to 99. Null for none.
    /// </summary>
    internal static string? BadgeText(int changes)
    {
        if (changes <= 0)
        {
            return null;
        }

        if (changes > 99)
        {
            return "99+";
        }

        return changes.ToString(CultureInfo.CurrentCulture);
    }

    static char GlyphOf(GitPane pane)
    {
        if (pane == GitPane.Changes)
        {
            return Icons.Changes;
        }

        return Icons.History;
    }

    const TextFormatFlags acrossFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

    // How long a tab is down the strip, for its icon and its label.
    int LengthOf(GitPane pane, Graphics graphics)
    {
        var icon = LogicalToDeviceUnits(iconSize);
        switch (labels)
        {
            case StripLabels.Horizontal:
                return LogicalToDeviceUnits(8) + icon + LogicalToDeviceUnits(3) + TextRenderer.MeasureText(graphics, pane.ToString(), small, Size.Empty, acrossFlags).Height + LogicalToDeviceUnits(7);
            case StripLabels.IconsOnly:
                return LogicalToDeviceUnits(8) + icon + LogicalToDeviceUnits(8);
            default:
                var text = (int) Math.Ceiling(graphics.MeasureString(TextOf(pane), Font, PointF.Empty, vertical).Height);
                return LogicalToDeviceUnits(10) + icon + LogicalToDeviceUnits(6) + text + LogicalToDeviceUnits(12);
        }
    }

    /// <summary>
    /// How wide the strip is for its labels: room for the icons, and for the names written down it
    /// or across it.
    /// </summary>
    internal int FitWidth()
    {
        var width = LogicalToDeviceUnits(leastWidth);
        switch (labels)
        {
            case StripLabels.Horizontal:
                foreach (var pane in shown.Keys)
                {
                    width = Math.Max(width, TextRenderer.MeasureText(pane.ToString(), small, Size.Empty, acrossFlags).Width + LogicalToDeviceUnits(12));
                }

                return width;
            case StripLabels.IconsOnly:
                return width;
            default:
                return Math.Max(width, Font.Height + LogicalToDeviceUnits(12));
        }
    }

    void Fit()
    {
        var width = FitWidth();
        if (Width != width)
        {
            Width = width;
        }
    }

    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args);
        small.Dispose();
        badgeFont.Dispose();
        (small, badgeFont) = Fonts(Font);
        Fit();
    }

    // Scaled by the form for a display of another scale, it is made as wide as its labels need
    // at that scale, rather than its width scaled.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        Fit();
    }

    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args);
        Fit();
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

            var height = LengthOf(pane, graphics);
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

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button == MouseButtons.Right)
        {
            PaneMenu.Show(this, args.Location, MenuCommands(), _ => { });
        }
    }

    /// <summary>
    /// The right-click menu: how the tabs are labelled, the one chosen ticked.
    /// </summary>
    internal List<QuickMenu.Command?> MenuCommands() =>
    [
        Choice("Vertical names", StripLabels.Vertical),
        Choice("Horizontal names", StripLabels.Horizontal),
        Choice("Icons only", StripLabels.IconsOnly)
    ];

    QuickMenu.Command Choice(string text, StripLabels choice) =>
        new(text, () => LabelsChosen?.Invoke(choice))
        {
            Checked = labels == choice
        };

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.Clear(BackColor);
        using (var divider = new Pen(ExplorerStyle.Divider))
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
                var inset = LogicalToDeviceUnits(5);
                graphics.FillRectangle(accent, inner.Right - line, inner.Top + inset, line, inner.Height - inset * 2);
            }

            DrawLabel(graphics, pane, bounds, text);
        }
    }

    void DrawLabel(Graphics graphics, GitPane pane, Rectangle bounds, Color text)
    {
        var size = LogicalToDeviceUnits(iconSize);
        var x = bounds.Left + (bounds.Width - size) / 2;
        var icon = Icons.Bitmap(GlyphOf(pane), size, text);
        switch (labels)
        {
            case StripLabels.Horizontal:
            {
                var iconBounds = new Rectangle(x, bounds.Top + LogicalToDeviceUnits(8), size, size);
                graphics.DrawImageUnscaled(icon, iconBounds.Location);
                var name = Rectangle.FromLTRB(bounds.Left, iconBounds.Bottom + LogicalToDeviceUnits(3), bounds.Right, bounds.Bottom);
                TextRenderer.DrawText(graphics, pane.ToString(), small, name, text, acrossFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
                DrawBadge(graphics, pane, iconBounds, bounds);
                return;
            }
            case StripLabels.IconsOnly:
            {
                var iconBounds = new Rectangle(x, bounds.Top + (bounds.Height - size) / 2, size, size);
                graphics.DrawImageUnscaled(icon, iconBounds.Location);
                DrawBadge(graphics, pane, iconBounds, bounds);
                return;
            }
            default:
            {
                var top = bounds.Top + LogicalToDeviceUnits(10);
                graphics.DrawImageUnscaled(icon, x, top);
                using var textBrush = new SolidBrush(text);
                var measured = graphics.MeasureString(TextOf(pane), Font, PointF.Empty, vertical);
                var textX = bounds.Left + (bounds.Width - measured.Width) / 2;
                graphics.DrawString(TextOf(pane), Font, textBrush, textX, top + size + LogicalToDeviceUnits(6), vertical);
                return;
            }
        }
    }

    // How many files have changed, in a pill over the bottom right of Changes' icon, in the
    // accent colour, as the count on VS Code's Source Control icon.
    void DrawBadge(Graphics graphics, GitPane pane, Rectangle icon, Rectangle tab)
    {
        if (pane != GitPane.Changes ||
            BadgeText(changes) is not { } badge)
        {
            return;
        }

        var measured = TextRenderer.MeasureText(graphics, badge, badgeFont, Size.Empty, acrossFlags);
        var height = measured.Height + LogicalToDeviceUnits(1);
        var width = Math.Max(height, measured.Width + LogicalToDeviceUnits(6));
        var right = Math.Min(icon.Right + LogicalToDeviceUnits(6), tab.Right - LogicalToDeviceUnits(2));
        var bounds = new Rectangle(right - width, icon.Bottom - height + LogicalToDeviceUnits(4), width, height);

        var fill = ExplorerStyle.Accent;
        var ink = ContrastingText(fill);
        if (ExplorerStyle.HighContrast())
        {
            fill = SystemColors.Highlight;
            ink = SystemColors.HighlightText;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var ring = new Pen(BackColor, LogicalToDeviceUnits(2)))
        using (var brush = new SolidBrush(fill))
        using (var shape = ExplorerStyle.RoundedRectangle(bounds, height / 2))
        {
            // A ring of the strip's colour round it, so it stands off the icon under it.
            graphics.DrawPath(ring, shape);
            graphics.FillPath(brush, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(graphics, badge, badgeFont, bounds, ink, acrossFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>
    /// Black or white, whichever reads better on <paramref name="background"/>.
    /// </summary>
    internal static Color ContrastingText(Color background)
    {
        var luminance = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
        if (luminance > 150)
        {
            return Color.Black;
        }

        return Color.White;
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new StripAccessibility(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            vertical.Dispose();
            small.Dispose();
            badgeFont.Dispose();
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
