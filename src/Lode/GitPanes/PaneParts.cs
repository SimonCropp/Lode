/// <summary>
/// The two panes on the window's right edge.
/// </summary>
enum GitPane
{
    Changes,
    History
}

/// <summary>
/// The top of a pane: its name, and the pin that keeps it beside the list, or lets it slide away
/// again.
/// </summary>
sealed class PaneHeader : Panel
{
    GlyphButton pin = new()
    {
        Dock = DockStyle.Right,
        Width = 32,
        TabStop = false
    };

    ToolTip tip = new();
    Font bold;
    string title;
    bool pinned;

    public event Action? PinClicked;

    public PaneHeader(string title)
    {
        this.title = title;
        bold = new(Font, FontStyle.Bold);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ExplorerStyle.Window;
        Dock = DockStyle.Top;
        Height = 36;
        Padding = new(0, 3, 4, 3);
        pin.Click += (_, _) => PinClicked?.Invoke();
        Controls.Add(pin);
        Pinned = false;
    }

    /// <summary>
    /// Whether the pane is pinned: the pin then offers to unpin it.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Pinned
    {
        get => pinned;
        set
        {
            pinned = value;
            if (value)
            {
                pin.Text = Icons.Unpin.ToString();
                pin.AccessibleName = $"Unpin {title}";
                pin.AccessibleDescription = "Unpin: slide away when not in use";
                tip.SetToolTip(pin, pin.AccessibleDescription);
                return;
            }

            pin.Text = Icons.Pin.ToString();
            pin.AccessibleName = $"Pin {title}";
            pin.AccessibleDescription = "Pin: keep beside the list";
            tip.SetToolTip(pin, pin.AccessibleDescription);
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        var bounds = Rectangle.FromLTRB(LogicalToDeviceUnits(12), 0, pin.Left, Height);
        TextRenderer.DrawText(
            args.Graphics,
            title,
            bold,
            bounds,
            ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        using var pen = new Pen(ExplorerStyle.Divider);
        args.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bold.Dispose();
            tip.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A button filled with the accent colour, as GitHub Desktop's Commit is, its text in two parts,
/// the second bold: "Commit to " and the branch.
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
        graphics.Clear(Parent?.BackColor ?? ExplorerStyle.Window);
        var fill = ExplorerStyle.Accent;
        var text = Color.White;
        if (SystemInformation.HighContrast)
        {
            fill = SystemColors.Highlight;
            text = SystemColors.HighlightText;
        }

        if (!Enabled)
        {
            fill = ExplorerStyle.Blend(fill, ExplorerStyle.Window, 0.55f);
        }
        else if (hovered)
        {
            fill = ExplorerStyle.Blend(fill, Color.Black, 0.1f);
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(fill))
        using (var shape = ExplorerStyle.RoundedRectangle(Rectangle.FromLTRB(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(4)))
        {
            graphics.FillPath(brush, shape);
        }

        graphics.SmoothingMode = SmoothingMode.Default;
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
        var rowHeight = SmallImageList?.ImageSize.Height ?? 1;
        if (IsHandleCreated &&
            VirtualListSize > 0)
        {
            rowHeight = Math.Max(rowHeight, GetItemRect(0).Height);
        }

        if (VirtualListSize * rowHeight > Height)
        {
            width -= SystemInformation.VerticalScrollBarWidth;
        }

        width = Math.Max(1, width);
        if (Columns[0].Width != width)
        {
            Columns[0].Width = width;
        }
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
    /// The row's background: Explorer's tint of the accent when selected, a shade when selected
    /// in a list without the focus, the system's highlight with high contrast on. The colours
    /// for text on it follow.
    /// </summary>
    public (Color Background, Color Text, Color Secondary) Colours(bool selected)
    {
        var background = ExplorerStyle.Window;
        var text = ForeColor;
        var secondary = ExplorerStyle.SecondaryText;
        if (!selected)
        {
            return (background, text, secondary);
        }

        if (SystemInformation.HighContrast)
        {
            return (SystemColors.Highlight, SystemColors.HighlightText, SystemColors.HighlightText);
        }

        if (Focused)
        {
            return (ExplorerStyle.Blend(background, ExplorerStyle.Accent, 0.2f), text, secondary);
        }

        return (ExplorerStyle.Hover(background), text, secondary);
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
/// How a changed file is drawn in a row, in the Changes pane and under a commit in History: its
/// folder dimmed, then its name, and at the right a letter for what happened to it, as VS Code
/// marks them, in the Git column's colour for it.
/// </summary>
static class ChangeRows
{
    const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    /// <summary>
    /// The letter for what happened to a file: M modified, A added, D deleted, R renamed, U one
    /// git doesn't track, ! one left to resolve.
    /// </summary>
    public static string Letter(GitChange kind) =>
        kind switch
        {
            GitChange.Added => "A",
            GitChange.Deleted => "D",
            GitChange.Renamed => "R",
            GitChange.Untracked => "U",
            GitChange.Conflicted => "!",
            _ => "M"
        };

    /// <summary>
    /// What happened to a file, in words, for a screen reader and the row's tooltip.
    /// </summary>
    public static string Describe(Change change) =>
        change.Kind switch
        {
            GitChange.Added => $"{change.Path}, added",
            GitChange.Deleted => $"{change.Path}, deleted",
            GitChange.Renamed => $"{change.Path}, renamed from {change.OldPath}",
            GitChange.Untracked => $"{change.Path}, not tracked",
            GitChange.Conflicted => $"{change.Path}, conflicted",
            _ => $"{change.Path}, modified"
        };

    public static void Draw(Graphics graphics, Control control, Font font, Rectangle bounds, Change change, Color text, Color secondary)
    {
        var letterWidth = control.LogicalToDeviceUnits(18);
        var gap = control.LogicalToDeviceUnits(8);
        var letterBounds = new Rectangle(bounds.Right - letterWidth - gap, bounds.Top, letterWidth, bounds.Height);
        TextRenderer.DrawText(graphics, Letter(change.Kind), font, letterBounds, ExplorerStyle.ChangeColor(change.Kind), flags | TextFormatFlags.HorizontalCenter);

        var room = Rectangle.FromLTRB(bounds.Left, bounds.Top, letterBounds.Left - gap, bounds.Bottom);
        var slash = change.Path.LastIndexOf('/');
        var folder = change.Path[..(slash + 1)];
        var name = change.Path[(slash + 1)..];

        // The name whole where it can be, the folder giving way first.
        var nameWidth = TextRenderer.MeasureText(graphics, name, font, Size.Empty, flags).Width;
        var folderWidth = Math.Max(0, Math.Min(room.Width - nameWidth, TextRenderer.MeasureText(graphics, folder, font, Size.Empty, flags).Width));
        if (folder.Length > 0 &&
            folderWidth > 0)
        {
            TextRenderer.DrawText(graphics, folder, font, new Rectangle(room.Left, room.Top, folderWidth, room.Height), secondary, flags | TextFormatFlags.PathEllipsis);
        }

        var nameBounds = Rectangle.FromLTRB(room.Left + folderWidth, room.Top, room.Right, room.Bottom);
        var colour = text;
        if (change.Kind == GitChange.Deleted)
        {
            colour = secondary;
        }

        TextRenderer.DrawText(graphics, name, font, nameBounds, colour, flags | TextFormatFlags.EndEllipsis);
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

    protected override void OnPaint(PaintEventArgs args) =>
        TextFrame.Draw(args.Graphics, this, Box.Focused);
}

/// <summary>
/// Where a tick is drawn, and how: an accent box with a tick in it, an empty box, or, for some of
/// several, a dash.
/// </summary>
static class Ticks
{
    // Pixels at 100% scale.
    public const int Size = 16;

    public static void Draw(Graphics graphics, Control control, Rectangle box, bool? ticked, bool enabled)
    {
        var radius = control.LogicalToDeviceUnits(3);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = ExplorerStyle.Accent;
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
            using var pen = new Pen(ExplorerStyle.SecondaryText);
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
